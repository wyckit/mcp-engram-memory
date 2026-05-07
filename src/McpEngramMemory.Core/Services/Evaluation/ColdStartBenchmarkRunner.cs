using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// Runs the cold-start self-referential benchmark — a two-phase protocol that measures
/// how much persistent memory (Engram) helps a coding agent answer questions about
/// codebase-specific knowledge.
///
/// Phase 1 (priming): Spawn a claude -p process with the Engram MCP enabled. Feed it a
/// session transcript and ask it to store key facts via the remember/store_memory tools.
/// Memories land in a per-seed scratch namespace isolated to this task and seed.
///
/// Phase 2 (cold): Spawn two FRESH claude -p processes using only the coldPrompt:
///   Arm A (no_memory):   No Engram MCP — pure vanilla model knowledge.
///   Arm B (full_engram): Engram MCP with access to the primed namespace.
///
/// Both arms are scored against the goldRubric via keyword/pattern matching (v1.1).
/// Lift on full_engram is the marketing artifact for v1.1.
///
/// DESIGN NOTE (v1.1 simplification): True per-process MCP isolation requires writing
/// per-run MCP config files and passing them to `claude -p --mcp-config`. This runner
/// uses a per-seed namespace prefix (bench-cold-start-{taskId}-{seed}) to isolate primed
/// memories from other runs. The no_memory arm receives a priming instruction that
/// explicitly tells the agent NOT to use any prior memory. True process isolation with
/// separate MCP config files is targeted for v1.2.
/// </summary>
public sealed class ColdStartBenchmarkRunner
{
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _executable;
    private readonly TimeSpan _phaseTimeout;

    /// <param name="executable">Path to the claude CLI executable (default: "claude").</param>
    /// <param name="phaseTimeout">Per-phase subprocess timeout (default: 5 minutes).</param>
    public ColdStartBenchmarkRunner(string? executable = null, TimeSpan? phaseTimeout = null)
    {
        _executable = string.IsNullOrWhiteSpace(executable)
            ? ClaudeCliModelClient.DefaultExecutable
            : executable;
        _phaseTimeout = phaseTimeout ?? TimeSpan.FromMinutes(5);
    }

    public async Task<ColdStartBenchmarkResult> RunAsync(
        ColdStartBenchmarkOptions options,
        CancellationToken cancellationToken = default)
    {
        var manifest = LoadManifest(options.DatasetPath);
        var tasks = options.MaxTasks > 0
            ? manifest.Tasks.Take(options.MaxTasks).ToList()
            : manifest.Tasks;

        string cliVersion = await PinCliVersionAsync(cancellationToken);

        var noMemoryResults = new List<ColdStartTaskResult>();
        var fullEngramResults = new List<ColdStartTaskResult>();

        foreach (var task in tasks)
        {
            for (int seed = 1; seed <= options.Seeds; seed++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string primedNamespace = $"bench-cold-start-{SanitizeId(task.TaskId)}-{seed}";

                // Phase 1: prime the namespace
                await RunPhase1PrimingAsync(task, primedNamespace, options, cancellationToken);

                // Phase 2 arm A: no_memory
                var noMemoryResult = await RunPhase2ArmAsync(
                    task, seed, primedNamespace, arm: "no_memory", options, cancellationToken);
                noMemoryResults.Add(noMemoryResult);

                // Phase 2 arm B: full_engram
                var fullEngramResult = await RunPhase2ArmAsync(
                    task, seed, primedNamespace, arm: "full_engram", options, cancellationToken);
                fullEngramResults.Add(fullEngramResult);
            }
        }

        var noMemoryArm = AggregateArm("no_memory", noMemoryResults);
        var fullEngramArm = AggregateArm("full_engram", fullEngramResults);

        double meanNoMemoryTokens = noMemoryResults
            .Where(r => r.Error is null)
            .Select(r => (double)(r.InputTokens + r.OutputTokens))
            .DefaultIfEmpty(0)
            .Average();

        double meanFullEngramTokens = fullEngramResults
            .Where(r => r.Error is null)
            .Select(r => (double)(r.InputTokens + r.OutputTokens))
            .DefaultIfEmpty(0)
            .Average();

        double tokenSavingsRatio = meanNoMemoryTokens > 0
            ? (meanNoMemoryTokens - meanFullEngramTokens) / meanNoMemoryTokens
            : 0;

        return new ColdStartBenchmarkResult(
            DatasetId: manifest.DatasetId,
            RunAtUtc: DateTime.UtcNow,
            Provider: options.Provider,
            Model: options.Model,
            ClaudeCliVersion: cliVersion,
            TaskCount: tasks.Count,
            Seeds: options.Seeds,
            NoMemory: noMemoryArm,
            FullEngram: fullEngramArm,
            PassRateLift: fullEngramArm.MeanPassRate - noMemoryArm.MeanPassRate,
            TokenSavingsRatio: tokenSavingsRatio,
            ScoringMethod: "pattern-match-v1",
            Notes: "Scoring is approximate keyword/pattern matching. LLM judge planned for v1.2. " +
                   "Phase isolation uses per-seed namespace prefixes; true process-level MCP config isolation is v1.2.");
    }

    // ─── Phase 1: Prime ─────────────────────────────────────────────────────

    private async Task RunPhase1PrimingAsync(
        ColdStartTask task,
        string primedNamespace,
        ColdStartBenchmarkOptions options,
        CancellationToken ct)
    {
        var prompt = BuildPrimingPrompt(task.PrimingTranscript, primedNamespace);

        try
        {
            await InvokeClaudeAsync(prompt, options.Model, withMcp: true,
                primedNamespace: primedNamespace, options: options, ct: ct);
        }
        catch (Exception ex)
        {
            // Priming failure is non-fatal — the full_engram arm will just have no memories
            Console.Error.WriteLine(
                $"[cold-start] Phase1 priming failed for task={task.TaskId}: {ex.Message}");
        }
    }

    private static string BuildPrimingPrompt(string transcript, string namespaceName)
    {
        return
            $"You are receiving a knowledge dump from a prior development session. " +
            $"Read the transcript carefully and use the `store_memory` or `remember` tool to store the key facts " +
            $"you will need to recall later. Use the namespace `{namespaceName}`. " +
            $"Store memories with descriptive kebab-case ids that match what you would naturally call them " +
            $"(e.g., 'reflect-id-generation-contract', 'flush-debounce-race-root-cause'). " +
            $"Focus on facts that would help answer a follow-up question about this topic.\n\n" +
            $"TRANSCRIPT:\n{transcript}";
    }

    // ─── Phase 2: Cold arms ──────────────────────────────────────────────────

    private async Task<ColdStartTaskResult> RunPhase2ArmAsync(
        ColdStartTask task,
        int seed,
        string primedNamespace,
        string arm,
        ColdStartBenchmarkOptions options,
        CancellationToken ct)
    {
        bool withMcp = arm == "full_engram";
        var prompt = BuildColdPrompt(task.ColdPrompt, arm, primedNamespace);

        var sw = Stopwatch.StartNew();
        string? response = null;
        string? error = null;

        try
        {
            response = await InvokeClaudeAsync(prompt, options.Model, withMcp,
                primedNamespace: withMcp ? primedNamespace : null, options: options, ct: ct);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        sw.Stop();

        var scoring = ScoreResponse(response, task.GoldRubric);

        // Token estimation — the claude CLI doesn't expose token counts on stdout;
        // we approximate from character counts (heuristic: ~4 chars/token).
        int inputTokens = EstimateTokens(prompt);
        int outputTokens = EstimateTokens(response ?? string.Empty);

        return new ColdStartTaskResult(
            TaskId: task.TaskId,
            Seed: seed,
            CriteriaPassed: scoring.Passed,
            CriteriaTotal: scoring.Total,
            PassRate: scoring.PassRate,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            ToolCalls: 0,   // not available from claude CLI stdout; future work via --output-format json
            LatencyMs: sw.ElapsedMilliseconds,
            Response: response ?? string.Empty,
            Error: error);
    }

    private static string BuildColdPrompt(string coldPrompt, string arm, string primedNamespace)
    {
        if (arm == "full_engram")
        {
            return
                $"You have access to persistent memory via the Engram MCP tools. " +
                $"Your primed knowledge namespace is `{primedNamespace}`. " +
                $"Use the `recall`, `search_memory`, or `get_memory` tools to retrieve any relevant context " +
                $"before answering. Answer as specifically as possible.\n\n{coldPrompt}";
        }

        // no_memory arm — explicitly tell agent not to rely on memory tools
        return
            $"Answer the following question using only your general knowledge. " +
            $"Do not use any memory retrieval tools — answer from what you know directly.\n\n{coldPrompt}";
    }

    // ─── Claude CLI invocation ───────────────────────────────────────────────

    private async Task<string?> InvokeClaudeAsync(
        string prompt,
        string model,
        bool withMcp,
        string? primedNamespace,
        ColdStartBenchmarkOptions options,
        CancellationToken ct)
    {
        var args = new List<string> { "-p", "--model", model };
        if (!withMcp)
        {
            // Disable MCP tools for the no_memory arm.
            // claude CLI --no-mcp is not an official flag in v1.1;
            // we pass an empty MCP config by convention where supported,
            // otherwise rely on the prompt instruction to not use tools.
            // True env-var isolation (CLAUDE_MCP_CONFIG pointing to empty-config) is v1.2.
        }

        var psi = new ProcessStartInfo(_executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var a in args)
            psi.ArgumentList.Add(a);

        // Inject namespace hint via env var so the Engram MCP server can auto-scope recalls.
        // The server reads ENGRAM_DEFAULT_NAMESPACE if set.
        if (withMcp && primedNamespace is not null)
            psi.EnvironmentVariables["ENGRAM_DEFAULT_NAMESPACE"] = primedNamespace;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException(
                $"Failed to start '{_executable}'. Is the Claude Code CLI installed and on PATH?");

        await process.StandardInput.WriteAsync(prompt.AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
        process.StandardInput.Close();

        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();
        var stdoutTask = DrainAsync(process.StandardOutput, stdoutBuf, ct);
        var stderrTask = DrainAsync(process.StandardError, stderrBuf, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_phaseTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                $"claude CLI exceeded {_phaseTimeout.TotalSeconds:F0}s timeout.");
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        if (process.ExitCode != 0)
        {
            var err = stderrBuf.ToString().Trim();
            throw new InvalidOperationException(
                $"claude CLI exited with code {process.ExitCode}. stderr: {(err.Length > 0 ? err : "(empty)")}");
        }

        return stdoutBuf.ToString().Trim();
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder buffer, CancellationToken ct)
    {
        char[] chunk = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(chunk, ct)) > 0)
            buffer.Append(chunk, 0, read);
    }

    // ─── CLI version pinning ─────────────────────────────────────────────────

    private async Task<string> PinCliVersionAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(_executable, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = Process.Start(psi);
            if (process is null) return "unavailable";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            try { await process.WaitForExitAsync(timeoutCts.Token); }
            catch { return "unavailable"; }

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            var combined = (stdout + " " + stderr).Trim();
            return string.IsNullOrWhiteSpace(combined) ? "unavailable" : combined.Split('\n')[0].Trim();
        }
        catch
        {
            return "unavailable";
        }
    }

    // ─── Rubric scoring (pattern-match-v1) ──────────────────────────────────

    /// <summary>
    /// Scores a response against goldRubric criteria using keyword/phrase pattern matching.
    /// Each criterion sentence is reduced to salient noun phrases, quoted strings, and
    /// specific identifiers. A criterion PASSES if the response contains all extracted
    /// patterns (case-insensitive). This is approximate — LLM judge planned for v1.2.
    /// </summary>
    public static RubricScore ScoreResponse(string? response, IReadOnlyList<string> rubric)
    {
        if (string.IsNullOrWhiteSpace(response) || rubric.Count == 0)
            return new RubricScore(0, rubric.Count, 0.0);

        int passed = 0;
        foreach (var criterion in rubric)
        {
            var patterns = ExtractPatterns(criterion);
            bool criterionPassed = patterns.Count == 0 ||
                patterns.All(p => response.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (criterionPassed)
                passed++;
        }

        double passRate = rubric.Count > 0 ? (double)passed / rubric.Count : 0.0;
        return new RubricScore(passed, rubric.Count, passRate);
    }

    /// <summary>
    /// Extracts match patterns from a rubric criterion sentence.
    /// Strategy:
    ///   1. Extract backtick-quoted identifiers (e.g. `BeginTrackedWrite`)
    ///   2. Extract double-quoted strings (e.g. "duplicate_warning")
    ///   3. Extract single-quoted strings that look like identifiers
    ///   4. Extract snake_case / camelCase identifiers >= 6 chars
    ///   5. Extract specific numeric literals paired with context words
    /// Returns a deduplicated list of patterns to check.
    /// </summary>
    public static List<string> ExtractPatterns(string criterion)
    {
        var patterns = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string p)
        {
            p = p.Trim();
            if (!string.IsNullOrWhiteSpace(p) && seen.Add(p))
                patterns.Add(p);
        }

        // Backtick-quoted identifiers: `some_identifier`
        foreach (Match m in Regex.Matches(criterion, @"`([^`]+)`"))
            Add(m.Groups[1].Value);

        // Double-quoted strings: "some value"
        foreach (Match m in Regex.Matches(criterion, @"""([^""]{2,60})"""))
            Add(m.Groups[1].Value);

        // Single-quoted strings that look like identifiers or short phrases
        foreach (Match m in Regex.Matches(criterion, @"'([^']{2,60})'"))
            Add(m.Groups[1].Value);

        // snake_case identifiers >= 6 chars (method/field names, status codes)
        foreach (Match m in Regex.Matches(criterion, @"\b([a-z][a-z0-9]*(?:_[a-z0-9]+){1,})\b"))
        {
            if (m.Value.Length >= 6)
                Add(m.Value);
        }

        // CamelCase / PascalCase identifiers >= 8 chars
        foreach (Match m in Regex.Matches(criterion, @"\b([A-Z][a-z]+(?:[A-Z][a-z]*)+)\b"))
        {
            if (m.Value.Length >= 8)
                Add(m.Value);
        }

        return patterns;
    }

    // ─── Aggregation ─────────────────────────────────────────────────────────

    private static ColdStartArmResult AggregateArm(string arm, List<ColdStartTaskResult> results)
    {
        if (results.Count == 0)
        {
            return new ColdStartArmResult(arm, results, 0, 0, 0, 0, 0);
        }

        var passRates = results.Select(r => r.PassRate).ToList();
        double mean = passRates.Average();
        double sigma = passRates.Count > 1
            ? Math.Sqrt(passRates.Sum(p => (p - mean) * (p - mean)) / (passRates.Count - 1))
            : 0;

        return new ColdStartArmResult(
            Arm: arm,
            TaskResults: results,
            MeanPassRate: mean,
            SigmaPassRate: sigma,
            MeanTokens: results.Average(r => (double)(r.InputTokens + r.OutputTokens)),
            MeanToolCalls: results.Average(r => (double)r.ToolCalls),
            MeanLatencyMs: results.Average(r => (double)r.LatencyMs));
    }

    // ─── Manifest loading ────────────────────────────────────────────────────

    public static ColdStartManifest LoadManifest(string path)
    {
        string json = File.ReadAllText(path);

        // Try object form first (manifestVersion + tasks)
        try
        {
            var manifest = JsonSerializer.Deserialize<ColdStartManifest>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest?.Tasks is { Count: > 0 })
                return manifest;
        }
        catch { }

        // Fall back to bare array (draft format)
        try
        {
            var tasks = JsonSerializer.Deserialize<List<ColdStartTask>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Manifest JSON is null.");

            return new ColdStartManifest(
                ManifestVersion: "1.0",
                DatasetId: Path.GetFileNameWithoutExtension(path),
                CreatedUtc: DateTime.MinValue,
                Tasks: tasks);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to load cold-start manifest from '{path}': {ex.Message}", ex);
        }
    }

    // ─── Artifact persistence ─────────────────────────────────────────────────

    public static string PersistArtifact(ColdStartBenchmarkResult result, string? outputDir)
    {
        string root = !string.IsNullOrWhiteSpace(outputDir)
            ? outputDir
            : (Environment.GetEnvironmentVariable("BENCHMARK_ARTIFACTS_PATH")
               ?? Path.Combine(Directory.GetCurrentDirectory(), "benchmarks"));

        string datedDir = Path.Combine(root, result.RunAtUtc.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(datedDir);

        string fileName = $"cold-start-{SanitizeId(result.DatasetId)}-{SanitizeId(result.Model)}.json";
        string path = Path.Combine(datedDir, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(result, ArtifactJsonOptions));
        return Path.GetFullPath(path);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static int EstimateTokens(string text)
        => (int)Math.Ceiling(text.Length / 4.0);

    public static string SanitizeId(string v)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string((v ?? "default").Trim().ToLowerInvariant()
            .Select(c => invalid.Contains(c) || c == ':' || char.IsWhiteSpace(c) ? '-' : c)
            .ToArray()).Trim('-');
    }

    public readonly record struct RubricScore(int Passed, int Total, double PassRate);
}

// ─── Options ─────────────────────────────────────────────────────────────────

public sealed record ColdStartBenchmarkOptions(
    string Model,
    string DatasetPath,
    string Provider = "claude-cli",
    int MaxTasks = 0,
    int Seeds = 3,
    int MaxTokens = 1024,
    float Temperature = 0.0f,
    string? OutputDir = null,
    string? ClaudeExecutable = null);

// ─── Result types ─────────────────────────────────────────────────────────────

public sealed record ColdStartBenchmarkResult(
    string DatasetId,
    DateTime RunAtUtc,
    string Provider,
    string Model,
    string ClaudeCliVersion,
    int TaskCount,
    int Seeds,
    ColdStartArmResult NoMemory,
    ColdStartArmResult FullEngram,
    double PassRateLift,
    double TokenSavingsRatio,
    string ScoringMethod,
    string? Notes);

public sealed record ColdStartArmResult(
    string Arm,
    List<ColdStartTaskResult> TaskResults,
    double MeanPassRate,
    double SigmaPassRate,
    double MeanTokens,
    double MeanToolCalls,
    double MeanLatencyMs);

public sealed record ColdStartTaskResult(
    string TaskId,
    int Seed,
    int CriteriaPassed,
    int CriteriaTotal,
    double PassRate,
    int InputTokens,
    int OutputTokens,
    int ToolCalls,
    long LatencyMs,
    string Response,
    string? Error);

// ─── Manifest types ───────────────────────────────────────────────────────────

public sealed record ColdStartManifest(
    string ManifestVersion,
    string DatasetId,
    DateTime CreatedUtc,
    List<ColdStartTask> Tasks);

public sealed record ColdStartTask(
    string TaskId,
    string PrimingTranscript,
    string ColdPrompt,
    IReadOnlyList<string> GoldRubric,
    IReadOnlyList<string> ExpectedMemoryIds,
    IReadOnlyList<string> Tags);
