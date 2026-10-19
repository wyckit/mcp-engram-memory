using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class BroaderRecallConflictTests
{
    [Theory]
    [InlineData("Atlas gateway times out after 30 seconds.", "Atlas gateway timeout is 45 seconds.")]
    [InlineData("The timeout for the Atlas gateway is 30 seconds.", "Atlas gateway timeout is 45 seconds.")]
    [InlineData("Atlas's gateway timeout is 30 seconds.", "Atlas gateway timeout is 45 seconds.")]
    [InlineData("Atlas gateway timeout is configured to 30 seconds.", "Atlas gateway timeout is 45 seconds.")]
    [InlineData("Atlas gateway timeout: 30 seconds.", "Atlas gateway timeout is 45 seconds.")]
    [InlineData("Atlas uses a gateway timeout of 30 seconds.", "Atlas gateway timeout is 45 seconds.")]
    [InlineData("Maya owns Atlas deployments.", "Atlas deployment owner is Leon.")]
    [InlineData("Atlas deployments are owned by Maya.", "Atlas deployment owner is Leon.")]
    [InlineData("Maya is responsible for Atlas deployments.", "Atlas deployment owner is Leon.")]
    [InlineData("Atlas primary database is hosted in us-east-1.", "Atlas primary database region is eu-west-1.")]
    [InlineData("Audit logging is enabled for Atlas.", "Atlas audit logging is disabled.")]
    [InlineData("Atlas gateway timeout is not 30 seconds.", "Atlas gateway timeout is 30 seconds.")]
    [InlineData("Atlas permits public access.", "Atlas does not allow public access.")]
    [InlineData("Atlas supports Linux.", "Atlas doesn't support Linux.")]
    [InlineData("Atlas requires authentication.", "Atlas does not require authentication.")]
    [InlineData("Atlas operational notes. Atlas gateway timeout is 30 seconds.", "Atlas gateway timeout is 45 seconds.")]
    public void DetectsBroaderExpressionsOfTheSameConflictingClaim(string a, string b)
    {
        var first = RecallConflictDetection.Parse(a);
        var second = RecallConflictDetection.Parse(b);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(first.ConflictsWith(second));
        Assert.True(second.ConflictsWith(first));
    }

    [Theory]
    [InlineData("Atlas gateway times out after 1 minute.", "Atlas gateway timeout is 60 seconds.")]
    [InlineData("The timeout for the Atlas gateway is 30 seconds.", "Atlas's gateway timeout is 30 seconds.")]
    [InlineData("Maya owns Atlas deployments.", "Atlas deployments are owned by Maya.")]
    [InlineData("Maya is responsible for Atlas deployments.", "Atlas deployment owner is Maya.")]
    [InlineData("Atlas primary database is hosted in us-east-1.", "Atlas primary database region is us-east-1.")]
    [InlineData("Audit logging is off for Atlas.", "Atlas audit logging is not enabled.")]
    [InlineData("Atlas permits public access.", "Atlas allows public access.")]
    [InlineData("Atlas doesn't support Linux.", "Atlas does not support Linux.")]
    [InlineData("Atlas gateway timeout is not 30 seconds.", "Atlas gateway timeout is 45 seconds.")]
    [InlineData("Atlas gateway timeout is not 30 seconds.", "Atlas gateway timeout is not 45 seconds.")]
    [InlineData("Atlas deployment owner is not Maya.", "Atlas deployment owner is Leon.")]
    [InlineData("Atlas supports Linux.", "Atlas supports Windows.")]
    [InlineData("Atlas supports Linux.", "Atlas does not support Windows.")]
    [InlineData("Atlas supports authentication.", "Atlas does not require authentication.")]
    [InlineData("Atlas supports Linux.", "Boreal does not support Linux.")]
    [InlineData("Maya owns Atlas staging deployments.", "Atlas production deployment owner is Leon.")]
    [InlineData("Atlas disaster recovery database runs in us-east-1.", "Atlas primary database region is eu-west-1.")]
    public void EquivalentAndComplementaryClaimsRemainCompatible(string a, string b)
    {
        var first = RecallConflictDetection.Parse(a);
        var second = RecallConflictDetection.Parse(b);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.False(first.ConflictsWith(second));
        Assert.False(second.ConflictsWith(first));
    }

    [Theory]
    [InlineData("If approved. Atlas gateway timeout is 30 seconds.")]
    [InlineData("Unless the migration fails. Atlas gateway timeout is 30 seconds.")]
    [InlineData("Proposed settings. Atlas gateway timeout is 30 seconds.")]
    [InlineData("For instance. Atlas gateway timeout is 30 seconds.")]
    [InlineData("Atlas gateway timeout is 30 seconds. This is only a draft.")]
    [InlineData("Atlas operational notes. Atlas gateway timeout is 30 seconds after migration.")]
    [InlineData("Historical settings. Maya owns Atlas deployments.")]
    [InlineData("Suppose Maya owns Atlas deployments.")]
    [InlineData("Example: 'Atlas's gateway timeout is 30 seconds.'")]
    [InlineData("Atlas only supports Linux.")]
    [InlineData("Atlas does not necessarily require authentication.")]
    [InlineData("Atlas primary database is hosted in Docker.")]
    [InlineData("Maya and Leon own Atlas deployments.")]
    [InlineData("Maya owns Atlas deployments when Leon is away.")]
    public void QualificationAndAmbiguousLocationsDoNotBecomeUnconditionalFacts(string text)
    {
        Assert.Empty(RecallConflictDetection.ParseAll(text));
    }

    [Theory]
    [InlineData("direct", 1)]
    [InlineData("expert", 5)]
    [InlineData("broadcast", 5)]
    public void RecallComparesLaterClaimsAndDeduplicatesTheEvidencePair(string route, int k)
    {
        using var store = new RecallTestStore();
        const string ns = "expert_broader", query = "Atlas gateway timeout";
        store.Add("a", ns, "Atlas retry limit is 3. Atlas gateway times out after 30 seconds. Atlas audit logging is on.");
        store.Add("b", ns, "Atlas retry limit is 3; Atlas gateway timeout is 45 seconds; Atlas audit logging is off.", cosine: .8f);
        if (route == "expert") store.Dispatcher.CreateExpert("broader", query);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(query, route == "direct" ? ns : null,
            k: k, expandGraph: false, spectralMode: "none"));
        var conflict = Assert.Single(result.Conflicts!);
        Assert.Equal("atlas gateway timeout", conflict.Subject);
        Assert.Equal("same_subject_different_value", conflict.Basis);
        Assert.Equal(new[] { "a", "b" }, conflict.Evidence.Select(e => e.Id).Order());
        Assert.InRange(result.Results.Count, 1, k);
    }

    [Fact]
    public void RecallLabelsExplicitDenialWithoutChoosingAnAuthority()
    {
        using var store = new RecallTestStore();
        store.Add("a", "atlas", "Atlas permits public access.");
        store.Add("b", "atlas", "Atlas doesn't allow public access.", cosine: .8f);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall("Atlas public access", "atlas",
            k: 1, expandGraph: false, spectralMode: "none"));
        var conflict = Assert.Single(result.Conflicts!);
        Assert.Equal("explicit_negation", conflict.Basis);
        Assert.Equal("unresolved", conflict.Status);
    }

    [Fact]
    public void ExtractionLimitsDoNotDropQualificationOutsideTheClaimWindow()
    {
        string text = string.Join(" ", Enumerable.Range(1, 10).Select(i => $"Atlas setting{i} timeout is {i} seconds."));
        Assert.Equal(4, RecallConflictDetection.ParseAll(text).Count);
        Assert.Empty(RecallConflictDetection.ParseAll(text + " These are proposed values."));
        Assert.Empty(RecallConflictDetection.ParseAll(new string('a', 2049)));
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("broadcast")]
    public void ParaphrasesCannotImportPrivateOrForeignEvidence(string route)
    {
        using var store = new RecallTestStore();
        store.Add("visible", "atlas", "Atlas deployment owner is Maya.", tenant: "team-a");
        var hidden = store.Add("private", "private", "Leon owns Atlas deployments.", tenant: "team-a");
        var foreign = store.Add("foreign", "atlas", "Leon owns Atlas deployments.", tenant: "team-b");
        store.Registry.EnsureOwnership("atlas", "alice", "team-a");
        store.Registry.EnsureOwnership("private", "alice", "team-a");
        Assert.Equal("shared", store.Registry.Share("atlas", "alice", "bob", "read", "team-a").Status);
        var result = Assert.IsType<RecallResult>(store.Tools("bob", "team-a").Recall("Who owns Atlas deployments?",
            route == "direct" ? "atlas" : null));
        Assert.Null(result.Conflicts);
        Assert.Equal("visible", Assert.Single(result.Results).Id);
        Assert.Equal(1, hidden.AccessCount);
        Assert.Equal(1, foreign.AccessCount);
    }
}
