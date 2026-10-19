using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class RecallConflictTests
{
    private const string Ns = "expert_conflicts";
    private const string Query = "What is the Atlas gateway timeout?";
    private const string First = "Atlas gateway timeout is 30 seconds.";
    private const string Second = "Atlas gateway timeout is 45 seconds.";

    [Theory]
    [InlineData("direct", 1, true)]
    [InlineData("expert", 1, true)]
    [InlineData("broadcast", 1, true)]
    [InlineData("direct", 5, true)]
    [InlineData("expert", 5, true)]
    [InlineData("broadcast", 5, true)]
    [InlineData("direct", 1, false)]
    public void UnmarkedDisagreementIsVisibleEvenWhenOnlyOneHitFits(string route, int k, bool hybrid)
    {
        using var store = new RecallTestStore();
        // Neither insertion order nor opaque ID identifies the correct value.
        var alternative = store.Add("a", Ns, Second, cosine: .8f);
        store.Add("z", Ns, First, cosine: .99f);
        int before = alternative.AccessCount;
        if (route == "expert") store.Dispatcher.CreateExpert("conflicts", Query);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, route == "direct" ? Ns : null,
            k: k, hybrid: hybrid, expandGraph: false, spectralMode: "none"));
        Assert.InRange(result.Results.Count, 1, k);
        var json = JsonSerializer.SerializeToElement(result);
        Assert.True(json.TryGetProperty("conflicts", out var conflicts), "Recall silently returned disagreeing claims.");
        var conflict = Assert.Single(conflicts.EnumerateArray());
        Assert.Equal("unresolved", conflict.GetProperty("status").GetString());
        Assert.Equal(new[] { "a", "z" }, conflict.GetProperty("evidence").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()).Order());
        Assert.All(conflict.GetProperty("evidence").EnumerateArray(), e =>
            Assert.Equal(Ns, e.GetProperty("ns").GetString()));
        if (k == 1 && result.Results.All(r => r.Id != "a")) Assert.Equal(before, alternative.AccessCount);
        Assert.Equal(First, store.Index.Get("z", Ns, "")!.Text);
        Assert.Empty(store.Graph.GetEdgesForEntry("z", tenantId: ""));
    }

    [Theory]
    [InlineData("Atlas gateway timeout is 30 seconds.", "Atlas gateway timeout is 45 seconds.", true)]
    [InlineData("Atlas gateway timeout is 1 minute.", "Atlas gateway timeout is 60 seconds.", false)]
    [InlineData("Atlas gateway timeout is 0.5 seconds.", "Atlas gateway timeout is 500 ms.", false)]
    [InlineData("Atlas gateway timeout is 1.5 seconds.", "Atlas gateway timeout is 500 ms.", true)]
    [InlineData("Atlas backup retention period is 1 day.", "Atlas backup retention period is 24 hours.", false)]
    [InlineData("Atlas retry limit is 3.", "Atlas retry limit is 5.", true)]
    [InlineData("Atlas replica count is 03.", "Atlas replica count is 3.", false)]
    [InlineData("Atlas service port is 443.", "Atlas service port is 8443.", true)]
    [InlineData("Atlas deployment owner is Maya.", "Atlas deployment owner is Leon.", true)]
    [InlineData("Atlas primary database region is us-east-1.", "Atlas primary database region is eu-west-1.", true)]
    [InlineData("Atlas health endpoint is /health.", "Atlas health endpoint is /ready.", true)]
    [InlineData("Atlas runtime version is v8.0.1.", "Atlas runtime version is 8.0.1.", false)]
    [InlineData("Atlas runtime version is 8.0.1.", "Atlas runtime version is 9.0.1.", true)]
    [InlineData("Atlas audit logging is enabled.", "Atlas audit logging is disabled.", true)]
    [InlineData("Atlas audit logging is not enabled.", "Atlas audit logging is disabled.", false)]
    [InlineData("The Atlas audit logging is enabled.", "Atlas audit logging is not disabled.", false)]
    [InlineData("Atlas audit logging is enabled.", "Boreal audit logging is disabled.", false)]
    [InlineData("Atlas staging timeout is 30 seconds.", "Atlas production timeout is 45 seconds.", false)]
    [InlineData("Atlas gateway timeout is enabled.", "Atlas gateway timeout is 45 seconds.", false)]
    public void OnlyMatchingSingleValuedSubjectsWithDifferentValuesConflict(string a, string b, bool expected)
    {
        var first = RecallConflictDetection.Parse(a);
        var second = RecallConflictDetection.Parse(b);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(expected, first.ConflictsWith(second));
    }

    [Theory]
    [InlineData("Example: Atlas gateway timeout is 30 seconds.")]
    [InlineData("'Atlas gateway timeout is 30 seconds.'")]
    [InlineData("Atlas gateway timeout is 30 seconds if the migration succeeds.")]
    [InlineData("Atlas gateway timeout is 30 seconds. This is a proposed setting.")]
    [InlineData("Atlas gateway timeout was 30 seconds.")]
    [InlineData("Atlas gateway timeout is 30 seconds as of 2025.")]
    [InlineData("Atlas gateway timeout is 30 seconds after the migration.")]
    [InlineData("Atlas gateway timeout is 30 or 45 seconds.")]
    [InlineData("Atlas gateway timeout is not 30 seconds.")]
    [InlineData("Atlas gateway timeout is about 30 seconds.")]
    [InlineData("Atlas gateway timeout might be 30 seconds.")]
    [InlineData("Superseded: Atlas gateway timeout is 30 seconds.")]
    [InlineData("Atlas gateway timeout is 9999999999999999999999999999999 days.")]
    [InlineData("Atlas is a service.")]
    [InlineData("Atlas supports Python.")]
    [InlineData("")]
    public void UnsupportedOrQualifiedClaimsAreNotFlattenedIntoConflicts(string text)
    {
        Assert.Null(RecallConflictDetection.Parse(text));
    }

    [Theory]
    [InlineData(false, Query)]
    [InlineData(true, "Compare the previous and current Atlas gateway timeout")]
    [InlineData(true, "What was the Atlas gateway timeout?")]
    public void OptOutAndHistoricalQueriesKeepTheirExistingOutput(bool rerank, string query)
    {
        using var store = new RecallTestStore();
        store.Add("a", Ns, First);
        store.Add("b", Ns, Second, cosine: .8f);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(query, Ns,
            hybrid: false, rerank: rerank, expandGraph: false, spectralMode: "none"));
        Assert.Null(result.Conflicts);
        Assert.False(JsonSerializer.SerializeToElement(result).TryGetProperty("conflicts", out _));
        Assert.Equal(new[] { "a", "b" }, result.Results.Select(r => r.Id));
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("broadcast")]
    public void EvidenceDoesNotCrossPrivateNamespacesTenantsOrThresholds(string route)
    {
        using var store = new RecallTestStore();
        store.Add("visible", Ns, First, tenant: "team-a");
        var low = store.Add("low", Ns, Second, cosine: .2f, tenant: "team-a");
        var denied = store.Add("private", "private", Second, tenant: "team-a");
        var foreign = store.Add("foreign", Ns, Second, tenant: "team-b");
        store.Registry.EnsureOwnership(Ns, "alice", "team-a");
        store.Registry.EnsureOwnership("private", "alice", "team-a");
        Assert.Equal("shared", store.Registry.Share(Ns, "alice", "bob", "read", "team-a").Status);
        store.Graph.AddEdge(new GraphEdge("visible", "private", "contradicts", tenantId: "team-a"));
        var result = Assert.IsType<RecallResult>(store.Tools("bob", "team-a").Recall(Query,
            route == "direct" ? Ns : null, k: 1, minScore: .8f));
        Assert.Null(result.Conflicts);
        Assert.Equal("visible", Assert.Single(result.Results).Id);
        Assert.All(new[] { low, denied, foreign }, e => Assert.Equal(1, e.AccessCount));
    }

    [Fact]
    public void BroadcastDoesNotCompareUnrelatedNamespacesEvenWithIdenticalSubjectsAndIds()
    {
        using var store = new RecallTestStore();
        store.Add("same", "project-a", First);
        store.Add("same", "project-b", Second);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, expandGraph: false, spectralMode: "none"));
        Assert.Equal(2, result.Results.Count);
        Assert.Null(result.Conflicts);
    }

    [Fact]
    public void SummaryOrExplicitlySupersededEvidenceDoesNotCreateUnresolvedConflicts()
    {
        using var store = new RecallTestStore();
        store.Add("source", Ns, First);
        store.Add("summary", Ns, Second, summary: true);
        store.Add("old", Ns, "Superseded: " + Second);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, spectralMode: "none"));
        Assert.Null(result.Conflicts);
    }

    [Fact]
    public void ConflictDiagnosticsSurviveRestartWithoutResurrectingDisputedArchives()
    {
        using var store = new RecallTestStore();
        store.Add("a", Ns, First, state: "archived");
        store.Add("b", Ns, Second, state: "archived");
        store.Restart();
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, expandGraph: false, spectralMode: "none"));
        Assert.Single(result.Conflicts!);
        Assert.All(result.Results, r => Assert.Equal("archived", r.LifecycleState));
        Assert.All(new[] { "a", "b" }, id =>
        {
            Assert.Equal("archived", store.Index.Get(id, Ns, "")!.LifecycleState);
            Assert.Equal(2, store.Index.Get(id, Ns, "")!.AccessCount);
        });
    }

    [Fact]
    public void DiagnosisPreservesPrimaryIdsScoresMetadataAndRawSearchBehavior()
    {
        using var store = new RecallTestStore();
        store.Add("a", Ns, First);
        var second = store.Add("b", Ns, Second, cosine: .85f);
        second.Metadata["authority"] = "verified"; // Arbitrary metadata is not proof.
        var baseline = store.Index.Search(new SearchRequest
        {
            Query = [1f, 0f], QueryText = Query, Namespace = Ns, TenantId = "", K = 1,
            Hybrid = true, Rerank = true, MinScore = .3f
        });
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: 1,
            expandGraph: false, spectralMode: "none"));
        Assert.Equal(baseline.Select(r => (r.Id, r.Score)), result.Results.Select(r => (r.Id, r.Score)));
        Assert.Single(result.Conflicts!);
        Assert.Equal("verified", second.Metadata["authority"]);
    }

    [Fact]
    public void ConflictOutputIsBoundedAndDeduplicated()
    {
        using var store = new RecallTestStore();
        for (int i = 1; i <= 20; i++) store.Add($"id-{i}", Ns, $"Atlas gateway timeout is {i} seconds.");
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: 5,
            expandGraph: false, spectralMode: "none"));
        Assert.Equal(8, result.Conflicts!.Count);
        Assert.All(result.Conflicts, c => Assert.Equal(2, c.Evidence.Count));
        Assert.Equal(8, result.Conflicts.Select(c => string.Join(",", c.Evidence.Select(e => e.Id).Order())).Distinct().Count());
        Assert.Equal(5, result.Results.Count);
    }
}
