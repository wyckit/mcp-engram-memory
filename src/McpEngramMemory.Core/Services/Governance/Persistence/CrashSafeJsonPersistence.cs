using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace McpEngramMemory.Core.Services.Governance.Persistence;

public sealed record PersistenceDiagnostic(
    string Code,
    string Path,
    long? RecordNumber,
    string Message,
    bool Recovered);

public sealed record PersistenceLoadResult<T>(T? Value, IReadOnlyList<PersistenceDiagnostic> Diagnostics);

internal sealed record JournalRecord<T>(string TenantId, long Sequence, T Value);

internal sealed record JournalReplay<T>(
    IReadOnlyList<JournalRecord<T>> Records,
    IReadOnlyList<PersistenceDiagnostic> Diagnostics);

internal static class CrashSafeJsonPersistence
{
    internal const int CurrentSchemaVersion = 1;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string TenantDirectory(string root, string tenantId)
        => Path.Combine(root, "tenants", HashText(tenantId ?? string.Empty)[..32]);

    public static string ArtifactFileName(string @namespace, string artifactId)
        => $"{HashText($"{@namespace}\n{artifactId}")[..32]}.json";

    /// <summary>
    /// Acquires a cross-instance/process lock beside a snapshot. The stable lock file is never
    /// deleted (deleting creates inode races on Unix); exclusive FileShare semantics release on
    /// handle disposal and after process failure.
    ///
    /// <paramref name="timeout"/> is required rather than defaulted: every caller so far serves a
    /// request while holding an in-process gate, so silently waiting forever on a peer that leaked
    /// the handle is never the behavior they want. Past the deadline the holder's IOException
    /// surfaces, leaving a retryable failure instead of a hang. Pass
    /// <see cref="Timeout.InfiniteTimeSpan"/> only for genuine background work.
    /// </summary>
    public static async ValueTask<FileStream> AcquireExclusiveLockAsync(
        string snapshotPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string lockPath = $"{snapshotPath}.lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        long deadline = timeout == Timeout.InfiniteTimeSpan
            ? long.MaxValue
            : Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.WriteThrough);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static async ValueTask WriteSnapshotAsync<T>(
        string path,
        string store,
        string tenantId,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        using var payloadDocument = JsonDocument.Parse(payload);
        var normalizedPayload = payloadDocument.RootElement.Clone();
        var envelope = new SnapshotEnvelope(
            CurrentSchemaVersion,
            store,
            tenantId ?? string.Empty,
            Hash(JsonSerializer.SerializeToUtf8Bytes(normalizedPayload, JsonOptions)),
            normalizedPayload);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static async ValueTask<PersistenceLoadResult<T>> ReadSnapshotAsync<T>(
        string path,
        string store,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var diagnostics = FindStaleTemps(path);
        if (!File.Exists(path))
            return new PersistenceLoadResult<T>(default, diagnostics);

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        SnapshotEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SnapshotEnvelope>(bytes, JsonOptions)
                       ?? throw new InvalidDataException("Snapshot envelope is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Snapshot '{path}' is corrupt.", exception);
        }

        ValidateEnvelope(envelope.SchemaVersion, envelope.Store, envelope.TenantId,
            store, tenantId, path);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(envelope.Payload, JsonOptions);
        if (!string.Equals(Hash(payload), envelope.PayloadHash, StringComparison.Ordinal))
            throw new InvalidDataException($"Snapshot '{path}' payload checksum is invalid.");

        T value = envelope.Payload.Deserialize<T>(JsonOptions)
                  ?? throw new InvalidDataException($"Snapshot '{path}' has an empty payload.");
        return new PersistenceLoadResult<T>(value, diagnostics);
    }

    public static async ValueTask AppendAsync<T>(
        string path,
        string store,
        string tenantId,
        long sequence,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        using var payloadDocument = JsonDocument.Parse(payload);
        var normalizedPayload = payloadDocument.RootElement.Clone();
        var envelope = new JournalEnvelope(
            CurrentSchemaVersion,
            store,
            tenantId ?? string.Empty,
            sequence,
            Hash(JsonSerializer.SerializeToUtf8Bytes(normalizedPayload, JsonOptions)),
            normalizedPayload);
        byte[] line = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);

        await using var stream = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(line, cancellationToken);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    public static async ValueTask<JournalReplay<T>> ReplayAsync<T>(
        string path,
        string store,
        string? expectedTenant,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return new JournalReplay<T>(Array.Empty<JournalRecord<T>>(), Array.Empty<PersistenceDiagnostic>());

        string content = await File.ReadAllTextAsync(path, cancellationToken);
        string[] lines = content.Split('\n');
        bool terminated = content.EndsWith('\n');
        int completeCount = terminated ? lines.Length - 1 : lines.Length - 1;
        var diagnostics = new List<PersistenceDiagnostic>();
        if (!terminated && lines[^1].Length > 0)
        {
            diagnostics.Add(new PersistenceDiagnostic(
                "corrupt-tail-ignored", path, lines.Length,
                "The final unterminated journal record was truncated during recovery.", true));
            TruncateToLines(path, lines, completeCount);
        }

        var records = new List<JournalRecord<T>>();
        long expectedSequence = 1;
        for (int index = 0; index < completeCount; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
                continue;
            bool isLastComplete = index == completeCount - 1;
            JournalEnvelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<JournalEnvelope>(lines[index], JsonOptions)
                           ?? throw new JsonException("Empty journal envelope.");
                ValidateEnvelope(envelope.SchemaVersion, envelope.Store, envelope.TenantId,
                    store, expectedTenant, path);
                if (envelope.Sequence != expectedSequence)
                    throw new InvalidDataException(
                        $"Expected journal sequence {expectedSequence}, found {envelope.Sequence}.");
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(envelope.Payload, JsonOptions);
                if (!string.Equals(Hash(payload), envelope.PayloadHash, StringComparison.Ordinal))
                    throw new InvalidDataException("Journal payload checksum is invalid.");
                T value = envelope.Payload.Deserialize<T>(JsonOptions)
                          ?? throw new InvalidDataException("Journal payload is empty.");
                records.Add(new JournalRecord<T>(envelope.TenantId, envelope.Sequence, value));
                expectedSequence++;
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException && isLastComplete)
            {
                diagnostics.Add(new PersistenceDiagnostic(
                    "corrupt-tail-ignored", path, index + 1,
                    $"The final corrupt journal record was truncated: {exception.Message}", true));
                TruncateToLines(path, lines, index);
                break;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Journal '{path}' is corrupt before its tail at record {index + 1}.", exception);
            }
        }

        return new JournalReplay<T>(records, diagnostics);
    }

    private static List<PersistenceDiagnostic> FindStaleTemps(string path)
    {
        var diagnostics = new List<PersistenceDiagnostic>();
        string? directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory))
            return diagnostics;
        foreach (string temporary in Directory.EnumerateFiles(directory, $"{Path.GetFileName(path)}.*.tmp"))
            diagnostics.Add(new PersistenceDiagnostic(
                "stale-temp-ignored", temporary, null,
                "A pre-replace temporary snapshot was ignored; the last committed snapshot remains authoritative.", true));
        return diagnostics;
    }

    private static void TruncateToLines(string path, string[] lines, int validLineCount)
    {
        string prefix = validLineCount == 0
            ? string.Empty
            : string.Join('\n', lines.Take(validLineCount)) + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(prefix);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.SetLength(0);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void ValidateEnvelope(
        int schemaVersion,
        string actualStore,
        string actualTenant,
        string expectedStore,
        string? expectedTenant,
        string path)
    {
        if (schemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Persistence schema {schemaVersion} in '{path}' is not supported; expected {CurrentSchemaVersion}.");
        if (!string.Equals(actualStore, expectedStore, StringComparison.Ordinal))
            throw new InvalidDataException($"Persistence store type mismatch in '{path}'.");
        if (expectedTenant is not null && !string.Equals(actualTenant, expectedTenant, StringComparison.Ordinal))
            throw new InvalidDataException($"Tenant partition mismatch in '{path}'.");
    }

    private static string Hash(ReadOnlySpan<byte> value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static string HashText(string value)
        => Hash(Encoding.UTF8.GetBytes(value));

    private sealed record SnapshotEnvelope(
        int SchemaVersion,
        string Store,
        string TenantId,
        string PayloadHash,
        JsonElement Payload);

    private sealed record JournalEnvelope(
        int SchemaVersion,
        string Store,
        string TenantId,
        long Sequence,
        string PayloadHash,
        JsonElement Payload);
}
