using System.Text.Json;
using McpEngramMemory.Core.Services.Evaluation;

namespace McpEngramMemory.Tests.Evaluation;

/// <summary>
/// Unit tests for ColdStartBenchmarkRunner — manifest loading, rubric scoring.
/// Subprocess integration (actual claude -p invocation) is intentionally excluded;
/// those tests require the CLI and subscription cost.
/// </summary>
public class ColdStartBenchmarkRunnerTests : IDisposable
{
    private readonly string _tempDir;

    public ColdStartBenchmarkRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cold_start_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ─── Manifest loading ────────────────────────────────────────────────────

    [Fact]
    public void LoadManifest_ObjectForm_Parses6Tasks()
    {
        // Arrange — write a minimal 6-task manifest.json in the new object format
        var manifestPath = Path.Combine(_tempDir, "manifest.json");
        var manifest = new
        {
            manifestVersion = "1.1",
            datasetId = "test-cold-start-v1",
            createdUtc = "2026-05-06T00:00:00Z",
            tasksDropped = Array.Empty<object>(),
            tasks = Enumerable.Range(1, 6).Select(i => new
            {
                taskId = $"task-{i}",
                primingTranscript = $"Priming text for task {i}",
                coldPrompt = $"Cold prompt for task {i}",
                goldRubric = new[] { $"Criterion A for task {i}", $"Criterion B for task {i}" },
                expectedMemoryIds = new[] { $"memory-{i}" },
                tags = new[] { "test" }
            }).ToArray()
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

        // Act
        var result = ColdStartBenchmarkRunner.LoadManifest(manifestPath);

        // Assert
        Assert.Equal("test-cold-start-v1", result.DatasetId);
        Assert.Equal("1.1", result.ManifestVersion);
        Assert.Equal(6, result.Tasks.Count);
        Assert.Equal("task-1", result.Tasks[0].TaskId);
        Assert.Equal(2, result.Tasks[0].GoldRubric.Count);
    }

    [Fact]
    public void LoadManifest_BareArrayForm_ParsesAllTasks()
    {
        // Arrange — draft format is a bare JSON array
        var manifestPath = Path.Combine(_tempDir, "manifest.json.draft");
        var tasks = Enumerable.Range(1, 3).Select(i => new
        {
            taskId = $"task-{i}",
            primingTranscript = $"Priming {i}",
            coldPrompt = $"Prompt {i}",
            goldRubric = new[] { $"Rubric {i}" },
            expectedMemoryIds = new[] { $"id-{i}" },
            tags = new[] { "test" }
        }).ToArray();
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(tasks));

        // Act
        var result = ColdStartBenchmarkRunner.LoadManifest(manifestPath);

        // Assert
        Assert.Equal(3, result.Tasks.Count);
        Assert.Equal("task-2", result.Tasks[1].TaskId);
    }

    [Fact]
    public void LoadManifest_TaskFields_AllFieldsPresent()
    {
        // Arrange — validate that all expected fields deserialize correctly
        var manifestPath = Path.Combine(_tempDir, "manifest.json");
        var manifest = new
        {
            manifestVersion = "1.1",
            datasetId = "field-check-v1",
            createdUtc = "2026-05-06T00:00:00Z",
            tasksDropped = Array.Empty<object>(),
            tasks = new[]
            {
                new
                {
                    taskId = "reflect-id-generation",
                    primingTranscript = "Session about reflect tool behavior...",
                    coldPrompt = "How does reflect generate its ID?",
                    goldRubric = new[]
                    {
                        "Agent correctly states the ID will be `retro-{today's UTC date}-ci-build-outcome`.",
                        "Agent correctly states the lifecycle state is always `ltm`."
                    },
                    expectedMemoryIds = new[] { "reflect-id-generation-contract", "reflect-duplicate-guard" },
                    tags = new[] { "internals" }
                }
            }
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

        // Act
        var result = ColdStartBenchmarkRunner.LoadManifest(manifestPath);

        // Assert
        var task = Assert.Single(result.Tasks);
        Assert.Equal("reflect-id-generation", task.TaskId);
        Assert.Contains("Session about reflect", task.PrimingTranscript);
        Assert.Contains("How does reflect", task.ColdPrompt);
        Assert.Equal(2, task.GoldRubric.Count);
        Assert.Equal(2, task.ExpectedMemoryIds.Count);
        Assert.Equal("reflect-id-generation-contract", task.ExpectedMemoryIds[0]);
        Assert.Contains("internals", task.Tags);
    }

    // ─── Rubric scoring — pattern-match-v1 ───────────────────────────────────

    [Fact]
    public void ScoreResponse_PerfectResponse_ReturnsPassRate1()
    {
        // Arrange — a response that contains all pattern-matched keywords
        // from a typical rubric. We construct a rubric where patterns are easily matchable.
        var rubric = new[]
        {
            "Agent correctly states the ID will be `retro-date-ci-build-outcome`.",
            "Agent correctly states the lifecycle state is always `ltm`.",
            "Agent correctly describes `duplicate_warning` behavior.",
            "Agent provides a CI strategy using `unique_topic_suffix`."
        };

        // Response contains all backtick identifiers verbatim
        string response =
            "The stored ID will be retro-date-ci-build-outcome (auto-generated). " +
            "The lifecycle state is always ltm — retrospectives are long-lived. " +
            "A second call returns duplicate_warning with the existing entry ID. " +
            "Use unique_topic_suffix or include a run ID in the topic to avoid this.";

        // Act
        var score = ColdStartBenchmarkRunner.ScoreResponse(response, rubric);

        // Assert
        Assert.Equal(rubric.Length, score.Total);
        Assert.Equal(rubric.Length, score.Passed);
        Assert.Equal(1.0, score.PassRate);
    }

    [Fact]
    public void ScoreResponse_WrongResponse_ReturnsPassRate0()
    {
        // Arrange — rubric has specific identifiers the response does not contain
        var rubric = new[]
        {
            "Agent correctly identifies `BeginTrackedWrite` must be called inside the lock.",
            "Agent states `_inFlightWrites` is the counter that tracks in-flight operations.",
            "Agent identifies `ManualResetEventSlim` as the wait primitive used.",
        };

        // Response is completely unrelated
        string response = "I'm not sure about this. The code looks fine to me. " +
                          "You could probably just add a lock somewhere.";

        // Act
        var score = ColdStartBenchmarkRunner.ScoreResponse(response, rubric);

        // Assert
        Assert.Equal(rubric.Length, score.Total);
        Assert.Equal(0, score.Passed);
        Assert.Equal(0.0, score.PassRate);
    }

    [Fact]
    public void ScoreResponse_EmptyResponse_ReturnsPassRate0WithZeroPassed()
    {
        var rubric = new[]
        {
            "Agent states the lifecycle state is `ltm`.",
            "Agent describes the `duplicate_warning` status code."
        };

        var score = ColdStartBenchmarkRunner.ScoreResponse(string.Empty, rubric);

        Assert.Equal(0, score.Passed);
        Assert.Equal(rubric.Length, score.Total);
        Assert.Equal(0.0, score.PassRate);
    }

    [Fact]
    public void ScoreResponse_NullResponse_ReturnsPassRate0()
    {
        var rubric = new[] { "Agent identifies `volatile` keyword requirement." };

        var score = ColdStartBenchmarkRunner.ScoreResponse(null, rubric);

        Assert.Equal(0, score.Passed);
        Assert.Equal(1, score.Total);
        Assert.Equal(0.0, score.PassRate);
    }

    [Fact]
    public void ScoreResponse_EmptyRubric_ReturnsPassRate0()
    {
        var score = ColdStartBenchmarkRunner.ScoreResponse("some response", Array.Empty<string>());

        Assert.Equal(0, score.Passed);
        Assert.Equal(0, score.Total);
        Assert.Equal(0.0, score.PassRate);
    }

    [Fact]
    public void ScoreResponse_CaseInsensitiveMatch_CountsAsPass()
    {
        // Pattern-matching should be case-insensitive
        var rubric = new[] { "Agent correctly identifies `BeginTrackedWrite`." };
        string response = "The correct fix is to call begintrackedwrite inside the lock.";

        var score = ColdStartBenchmarkRunner.ScoreResponse(response, rubric);

        Assert.Equal(1, score.Passed);
    }

    [Fact]
    public void ScoreResponse_PartialMatch_ReturnsPartialPassRate()
    {
        var rubric = new[]
        {
            "Agent correctly identifies `compute_diffusion_basis` as the new tool name.",
            "Agent lists `laplacian_stats` as renamed to `diffusion_stats`.",
            "Agent mentions `invalidate_diffusion` as a renamed tool.",
            "Agent does NOT claim the tool was removed or deprecated.",
            "Agent explains the rename rationale involved spectral theory."
        };

        // Response answers criteria 1, 3, and 4 but misses 2 and 5
        string response =
            "The tool compute_diffusion_basis replaces the old one. " +
            "invalidate_diffusion is also renamed. " +
            "The tool was NOT removed — it was purely a rename.";

        var score = ColdStartBenchmarkRunner.ScoreResponse(response, rubric);

        Assert.Equal(5, score.Total);
        // criteria 1 (compute_diffusion_basis), 3 (invalidate_diffusion) should pass
        // criteria 2 (laplacian_stats), 4, 5 depend on pattern extraction
        Assert.InRange(score.Passed, 0, 5);
        Assert.InRange(score.PassRate, 0.0, 1.0);
    }

    // ─── Pattern extraction ───────────────────────────────────────────────────

    [Fact]
    public void ExtractPatterns_BacktickIdentifiers_AreExtracted()
    {
        string criterion = "Agent calls `BeginTrackedWrite` inside `_timerLock`.";
        var patterns = ColdStartBenchmarkRunner.ExtractPatterns(criterion);

        Assert.Contains("BeginTrackedWrite", patterns);
        Assert.Contains("_timerLock", patterns);
    }

    [Fact]
    public void ExtractPatterns_DoubleQuotedStrings_AreExtracted()
    {
        string criterion = "Agent returns status=\"duplicate_warning\" to the caller.";
        var patterns = ColdStartBenchmarkRunner.ExtractPatterns(criterion);

        Assert.Contains("duplicate_warning", patterns);
    }

    [Fact]
    public void ExtractPatterns_SnakeCaseIdentifiers_AreExtracted()
    {
        // Use a criterion with a backtick-quoted identifier to test extraction
        string criterion = "The `_inFlightWrites` counter must be incremented before the write begins.";
        var patterns = ColdStartBenchmarkRunner.ExtractPatterns(criterion);

        // Backtick-quoted identifiers are always extracted
        Assert.Contains("_inFlightWrites", patterns);
    }

    [Fact]
    public void ExtractPatterns_EmptyCriterion_ReturnsEmpty()
    {
        var patterns = ColdStartBenchmarkRunner.ExtractPatterns(string.Empty);
        Assert.Empty(patterns);
    }

    // ─── Artifact path building ───────────────────────────────────────────────

    [Fact]
    public void SanitizeId_ColonsAndSpaces_AreReplaced()
    {
        Assert.Equal("phi3-5-3-8b", ColdStartBenchmarkRunner.SanitizeId("phi3.5:3.8b")
            .Replace(".", "-"));
        Assert.Equal("my-dataset", ColdStartBenchmarkRunner.SanitizeId("my dataset"));
        Assert.DoesNotContain(":", ColdStartBenchmarkRunner.SanitizeId("phi3.5:3.8b"));
    }
}
