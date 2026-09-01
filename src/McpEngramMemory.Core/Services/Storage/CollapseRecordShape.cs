using McpEngramMemory.Core.Models;
using System.Text.Json;

namespace McpEngramMemory.Core.Services.Storage;

/// <summary>
/// The ONE structural judgement every backend's receipt reads share — a strict read refuses a
/// row this class faults, a lenient boot load drops it. Centralized so the JSON and SQL
/// providers cannot drift: a rule that exists on one backend only is a rule an operator can
/// bypass by choosing a store.
///
/// Raw validation deliberately runs BEFORE construction. The read constructor normalizes a null
/// tenant to "" and trims the rest, so object-only validation cannot distinguish an omitted legacy
/// tenant from an explicit JSON null or see a control character trimmed from the input. It also
/// cannot preserve an unknown future field through a read-modify-write. Receipt JSON therefore
/// fails closed on unknown fields and lossy partition shapes instead of laundering them.
/// </summary>
internal static class CollapseRecordShape
{
    private static readonly HashSet<string> KnownJsonFields = new(StringComparer.Ordinal)
    {
        "collapseId", "clusterId", "summaryEntryId", "ns", "tenantId", "memberIds",
        "previousStates", "collapsedAt", "appliedLifecycleRevisions",
        "expectedLifecycleRevisions", "generation", "clusterStamp", "clusterInstance"
    };

    private static readonly string[] RequiredJsonFields =
    {
        "collapseId", "clusterId", "summaryEntryId", "ns", "memberIds",
        "previousStates", "collapsedAt"
    };

    private static readonly string[] DictionaryJsonFields =
    {
        "previousStates", "appliedLifecycleRevisions", "expectedLifecycleRevisions"
    };

    /// <summary>Returns a human-readable defect description, or null when the row is well formed.</summary>
    internal static string? Describe(CollapseRecord? r)
    {
        if (r is null || r.CollapseId is null || r.Ns is null || r.ClusterId is null
            || r.SummaryEntryId is null || r.MemberIds is null || r.PreviousStates is null
            // NESTED poison refuses too: a null member id or a null previous-state value
            // reaches the undo's planning loops as a receipt claim that throws mid-restore.
            || r.MemberIds.Any(m => m is null)
            || r.PreviousStates.Values.Any(v => v is null))
        {
            return "a record row is malformed (null row, null required field, or null nested element)";
        }

        // BLANK is as unusable as null. Ns and TenantId together address a partition, and a
        // blank Ns addresses a different one than the collapse ran in; a blank CollapseId
        // matches no undo request and cannot be superseded; a blank ClusterId or
        // SummaryEntryId names nothing to clean up. TenantId is excluded on purpose — "" is
        // the LEGACY partition there, a real value rather than an absence.
        if (string.IsNullOrWhiteSpace(r.CollapseId)
            || string.IsNullOrWhiteSpace(r.Ns)
            || string.IsNullOrWhiteSpace(r.ClusterId)
            || string.IsNullOrWhiteSpace(r.SummaryEntryId)
            || r.MemberIds.Any(string.IsNullOrWhiteSpace)
            || r.PreviousStates.Keys.Any(string.IsNullOrWhiteSpace)
            || (r.AppliedLifecycleRevisions?.Keys.Any(string.IsNullOrWhiteSpace) ?? false)
            || (r.ExpectedLifecycleRevisions?.Keys.Any(string.IsNullOrWhiteSpace) ?? false)
            || (r.ClusterStamp is not null && string.IsNullOrWhiteSpace(r.ClusterStamp))
            || (r.ClusterInstance is not null && string.IsNullOrWhiteSpace(r.ClusterInstance)))
        {
            return "a record row has a blank required identifier";
        }

        if (r.Generation < 0)
            return "a record row has a negative generation";

        try
        {
            Tenancy.ValidatePartitionComponent(r.Ns, nameof(r.Ns));
            Tenancy.ValidatePartitionComponent(r.TenantId, nameof(r.TenantId));
            if (r.TenantId.Length > Tenancy.MaxTenantIdLength)
                return $"a record row's tenant exceeds {Tenancy.MaxTenantIdLength} characters";
            if (r.Ns.Length > CognitiveEntry.MaxNamespaceLength)
                return $"a record row's namespace exceeds {CognitiveEntry.MaxNamespaceLength} characters";
        }
        catch (ArgumentException)
        {
            return "a record row has an invalid tenant or namespace partition component";
        }

        // The undo iterates PreviousStates and feeds each VALUE to a lifecycle transition as
        // its target state. A value outside the known set installs a state nothing else in
        // the system recognises, through a CAS that would happily succeed.
        if (r.PreviousStates.Values.Any(v => v is not ("stm" or "ltm" or "archived")))
            return "a record row names a previous state outside the known lifecycle set";

        // The member map must not EXCEED the members. Undo walks PreviousStates, not
        // MemberIds, so a key the receipt never claimed is an entry this collapse has no
        // authority over being restored under its name. Duplicates in the member list are
        // refused for the same reason the map is keyed. The map deliberately MAY cover a
        // SUBSET of the members: the protocol's own records are shaped that way — an INTENT
        // record has no states yet, a partial attempt records states only for planned
        // members — and requiring count equality refused the protocol's every second write.
        var claimed = new HashSet<string>(r.MemberIds, StringComparer.Ordinal);
        if (claimed.Count != r.MemberIds.Count
            || !r.PreviousStates.Keys.All(claimed.Contains))
        {
            return "a record row's previous-state map names members outside its member list";
        }

        // CROSS-FIELD: every INSTALLED or ARMED claim must have its previous state recorded.
        // The claims write installs prevStates[m], appliedAll[m], and expectedAll[m]
        // together, so no protocol shape faults here — but a damaged row whose PreviousStates
        // lost a key that AppliedLifecycleRevisions still holds would let the undo walk
        // PreviousStates, skip that member's restore entirely, and retire the receipt as
        // though it had restored everything: permanent archived stranding under a success
        // reply. ReleaseClaims already fails closed on exactly this shape; the read does too.
        if (r.AppliedLifecycleRevisions is not null
            && !r.AppliedLifecycleRevisions.Keys.All(r.PreviousStates.ContainsKey))
        {
            return "a record row holds an installed claim with no recorded previous state";
        }
        if (r.ExpectedLifecycleRevisions is not null
            && !r.ExpectedLifecycleRevisions.Keys.All(r.PreviousStates.ContainsKey))
        {
            return "a record row holds an armed claim with no recorded previous state";
        }

        return null;
    }

    /// <summary>
    /// Strictly parse a complete receipt set. Any malformed row, duplicate collapse id, explicit
    /// tenant null, invalid raw partition component, or unknown field refuses the whole set.
    /// </summary>
    internal static bool TryDeserializeStrict(
        string json,
        JsonSerializerOptions options,
        out List<CollapseRecord> records,
        out string? defect)
    {
        records = new();
        defect = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                defect = "collapse-history payload is not an array";
                return false;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (DescribeRaw(row) is { } rawDefect)
                {
                    defect = rawDefect;
                    records.Clear();
                    return false;
                }

                var record = row.Deserialize<CollapseRecord>(options);
                if (Describe(record) is { } objectDefect)
                {
                    defect = objectDefect;
                    records.Clear();
                    return false;
                }
                if (!ids.Add(record!.CollapseId))
                {
                    defect = "collapse-history set contains duplicate collapse ids";
                    records.Clear();
                    return false;
                }
                records.Add(record);
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            defect = "collapse-history payload could not be deserialized";
            records.Clear();
            return false;
        }
    }

    /// <summary>
    /// Lenient boot parser: retain every independently valid row and deterministically keep the
    /// first occurrence of a collapse id. Invalid/unknown rows are counted and dropped.
    /// </summary>
    internal static List<CollapseRecord> DeserializeLenient(
        string json,
        JsonSerializerOptions options,
        out int dropped)
    {
        dropped = 0;
        var records = new List<CollapseRecord>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Collapse-history payload is not an array.");

        foreach (var row in doc.RootElement.EnumerateArray())
        {
            try
            {
                if (DescribeRaw(row) is not null)
                {
                    dropped++;
                    continue;
                }
                var record = row.Deserialize<CollapseRecord>(options);
                if (Describe(record) is not null || !ids.Add(record!.CollapseId))
                {
                    dropped++;
                    continue;
                }
                records.Add(record);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                dropped++;
            }
        }
        return records;
    }

    private static string? DescribeRaw(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
            return "collapse-history set contains a non-object row";

        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in row.EnumerateObject())
        {
            if (!KnownJsonFields.Contains(property.Name))
                return $"a record row contains unknown field '{property.Name}'";
            if (!fields.Add(property.Name))
                return $"a record row repeats field '{property.Name}'";
        }
        foreach (var required in RequiredJsonFields)
        {
            if (!fields.Contains(required))
                return $"a record row is missing required field '{required}'";
        }

        if (!row.TryGetProperty("ns", out var nsElement)
            || nsElement.ValueKind != JsonValueKind.String
            || nsElement.GetString() is not { } rawNs)
        {
            return "a record row has a non-string namespace";
        }

        try
        {
            // Validate the raw spelling before any constructor can trim characters away.
            Tenancy.ValidatePartitionComponent(rawNs, "ns");
            if (rawNs.Length > CognitiveEntry.MaxNamespaceLength)
                return $"a record row's raw namespace exceeds {CognitiveEntry.MaxNamespaceLength} characters";
            if (row.TryGetProperty("tenantId", out var tenantElement))
            {
                if (tenantElement.ValueKind == JsonValueKind.Null)
                    return "a record row has an explicit null tenant";
                if (tenantElement.ValueKind != JsonValueKind.String
                    || tenantElement.GetString() is not { } rawTenant)
                {
                    return "a record row has a non-string tenant";
                }
                Tenancy.ValidatePartitionComponent(rawTenant, "tenantId");
                _ = Tenancy.Normalize(rawTenant);
            }
        }
        catch (ArgumentException)
        {
            return "a record row has an invalid raw tenant or namespace partition component";
        }

        foreach (var mapName in DictionaryJsonFields)
        {
            if (DescribeRawDictionary(row, mapName) is { } mapDefect)
                return mapDefect;
        }

        return null;
    }

    private static string? DescribeRawDictionary(JsonElement row, string fieldName)
    {
        if (!row.TryGetProperty(fieldName, out var map))
            return null;
        if (map.ValueKind == JsonValueKind.Null && fieldName != "previousStates")
            return null;
        if (map.ValueKind != JsonValueKind.Object)
            return $"a record row has a non-object '{fieldName}' map";

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in map.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
                return $"a record row has a blank key in '{fieldName}'";
            if (!keys.Add(property.Name))
                return $"a record row repeats key '{property.Name}' in '{fieldName}'";
        }
        return null;
    }
}
