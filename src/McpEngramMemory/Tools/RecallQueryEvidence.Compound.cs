using System.Text.RegularExpressions;

namespace McpEngramMemory.Tools;

internal sealed partial class RecallQueryEvidence
{
    public IReadOnlyList<RecallQueryEvidence> Facets { get; private init; } = [];
    public bool IsCompound => Facets.Count > 0;
    private bool CompoundFacet { get; init; }

    // Two explicit factual clauses with a shared subject/environment. Do not split
    // topic lists, quoted precision requests, explanations, or arbitrary conjunctions.
    // The caller enables this only on the default current factual recall path.
    public static RecallQueryEvidence? ParseCompound(string query)
    {
        if (query.Length > 1024 || query.Any(char.IsDigit) || query.Contains('"') || query.Contains('\''))
            return null;
        query = NormalizeQuestion(query);
        var joins = CompoundQuestion().Matches(query);
        if (joins.Count != 1) return null;
        var join = joins[0];
        string[] clauses = [query[..join.Index], query[join.Groups[1].Index..]];
        var facets = clauses.Select(c => Parse(CadenceQuestion().Replace(c.Trim(), "When do "))).ToArray();
        if (facets.Any(f => f is null || !f._rejectNonAnswers || f._referenceQuestion || f._proposalQuestion))
            return null;
        var first = facets[0]!;
        var second = facets[1]!;
        // An explicit second scope must agree. Unsupported multi-scope questions
        // retain the existing path; never silently replace an explicit qualifier.
        if ((first._names.Count > 0 && second._names.Count > 0 && !first._names.SetEquals(second._names)) ||
            (first._environments.Count > 0 && second._environments.Count > 0 &&
                !first._environments.SetEquals(second._environments))) return null;
        var names = first._names.Union(second._names).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var environments = first._environments.Union(second._environments).ToHashSet();
        RecallQueryEvidence Scoped(RecallQueryEvidence f) => new(names, environments, f._focus,
            f._owner, f._backupSchedule, f._recoveryDuration, f._rejectNonAnswers, f._referenceQuestion,
            observedRecovery: f._observedRecovery, restoreProcedure: f._restoreProcedure) { CompoundFacet = true };
        // The outer scope guard checks the entire original record, including mixed
        // statements; each inner matcher requires actual evidence for its own facet.
        return new(names, environments, [], false, false, false, false, false, scopeOnly: true)
        {
            Facets = [Scoped(first), Scoped(second)]
        };
    }

    [GeneratedRegex(@"\s*,?\s+and\s+(who|what|which|where|when|how)\b", Options)]
    private static partial Regex CompoundQuestion();
    [GeneratedRegex(@"^\s*how\s+often\s+(?:are|is|do|does)\s+", Options)]
    private static partial Regex CadenceQuestion();
    [GeneratedRegex(@"\bevery\s+(?:\d+(?:\.\d+)?|one|two|three|four|five|six|seven|eight|nine|ten)\s+(?:minutes?|hours?|days?|weeks?|months?)\b", Options)]
    private static partial Regex CadenceInterval();
}
