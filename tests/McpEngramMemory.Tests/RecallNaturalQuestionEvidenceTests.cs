using System.Text.Json;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class RecallNaturalQuestionEvidenceTests
{
    private const string Ns = "expert_nectar";

    public static TheoryData<string, string, string> Cases => new()
    {
        { "Who looks after Nectar production releases?", "Nectar production deployment owner is Leon Vale.", "Nectar production deployment review links to a checklist; the ticket does not identify a deployment owner." },
        { "Who owns Nectar deployment?", "Nectar staging deployment owner is Leon Vale.", "Nectar production deployment review links to a checklist; the ticket does not identify a deployment owner." },
        { "Which command rolls back Nectar production?", "Nectar production rollback command is deployctl undo nectar --stable.", "Nectar production rollback command is documented in the handbook." },
        { "How long did Nectar production recovery take in the latest rehearsal?", "Nectar production completed its latest recovery rehearsal in 14 minutes. The snapshot was mounted first; transaction replay took four minutes.", "Nectar production recovery time objective is 20 minutes." },
        { "If Nectar production stops accepting traffic, which URL should I probe?", "Nectar production readiness endpoint is /ready/live.", "Nectar production readiness endpoint is documented in the handbook." },
        { "What is Nectar production API credential value?", "Nectar production API credential value is DEMO_ONLY.", "Nectar production rotated its API credential during the September exercise. The value itself is intentionally not recorded." }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void NaturalQuestionsRequireTheRequestedEvidence(string query, string answer, string note)
    {
        foreach (string phrasing in new[] { query, query.ToLowerInvariant() })
        {
            var evidence = Assert.IsType<RecallQueryEvidence>(RecallQueryEvidence.Parse(phrasing));
            Assert.True(evidence.Matches(answer, Ns));
            Assert.False(evidence.Matches(note, Ns));
            Assert.False(evidence.Matches(phrasing, Ns));
            Assert.False(evidence.Matches(answer.Replace("Nectar", "Cedar"), Ns));
            if (phrasing.Contains("production"))
                Assert.False(evidence.Matches(answer.Replace("production", "staging"), Ns));
        }
    }

    [Theory]
    [InlineData("Nectar production API credential is DEMO_ONLY.", true)]
    [InlineData("Nectar production API credential: DEMO_ONLY.", true)]
    [InlineData("Nectar production API credential was rotated. The value itself is DEMO_ONLY.", true)]
    [InlineData("Nectar production API credential is documented in the handbook as DEMO_ONLY.", true)]
    [InlineData("Nectar production API credential value is DEMO_ONLY, but its audit date is unknown.", true)]
    [InlineData("Nectar production API credential value is DEMO_ONLY without identifying its issuer.", true)]
    [InlineData("Nectar production API credential value is 'rotated'.", true)]
    [InlineData("Nectar production API credential was rotated. The value itself is deliberately not provided.", false)]
    [InlineData("Nectar production API credential value is intentionally not recorded.", false)]
    [InlineData("Nectar production API credential was rotated. Its timeout value is 45 seconds.", false)]
    [InlineData("Nectar production API credential was rotated. Cedar production API credential value is DEMO_ONLY.", false)]
    [InlineData("Nectar production API credential was rotated. The staging credential value is DEMO_ONLY.", false)]
    [InlineData("Nectar production API credential was rotated. Cedar production notes. The value is DEMO_ONLY.", false)]
    [InlineData("Nectar production API credential value is rotated every week.", false)]
    [InlineData("Nectar production API credential is rotated every week.", false)]
    public void ValueQuestionsDistinguishAssignmentsFromActivityAndPreserveScope(string text, bool matches) =>
        Assert.Equal(matches, RecallQueryEvidence.Parse("What is Nectar production API credential value?")!.Matches(text, Ns));

    [Theory]
    [InlineData("Who looks after Nectar production releases?", "Leon looks after Nectar production releases.", true)]
    [InlineData("Which command rolls back Nectar production?", "Nectar production rolls back with deployctl undo nectar.", true)]
    [InlineData("How long did Nectar production recovery take?", "Nectar production recovered in 14 minutes.", true)]
    [InlineData("How long did Nectar production recovery take?", "Nectar production recovery took 14 minutes.", true)]
    [InlineData("How long did Nectar production recovery take?", "Nectar production recovery rehearsal starts in 14 minutes.", false)]
    [InlineData("How long did Nectar production recovery take?", "Nectar production recovery must finish within 14 minutes.", false)]
    [InlineData("How quickly must Nectar production recover?", "Nectar production recovery rehearsal took 14 minutes.", false)]
    [InlineData("If nectar production stops accepting traffic, which URL should I probe?", "cedar production readiness endpoint is /ready.", false)]
    [InlineData("When Nectar production fails, which URL should I probe?", "Nectar production readiness endpoint is /ready.", true)]
    [InlineData("If Nectar staging fails, which production URL should I probe?", "Nectar production readiness endpoint is /ready.", false)]
    [InlineData("If Cedar production fails, which Nectar production URL should I probe?", "Nectar production readiness endpoint is /ready.", false)]
    [InlineData("Where is Nectar production API credential value recorded?", "Nectar production API credential value is recorded in the secret manager.", true)]
    [InlineData("Which document specifies Nectar production deployment owner?", "Nectar production deployment owner is documented in the release handbook.", true)]
    public void AdjacentConstructionsKeepTheirMeaning(string query, string text, bool matches) =>
        Assert.Equal(matches, RecallQueryEvidence.Parse(query)!.Matches(text, Ns));

    [Theory]
    [MemberData(nameof(Cases))]
    public void AllRoutesRecoverAnswersWithoutAccessingRejectedNotes(string query, string answer, string note)
    {
        foreach (string route in new[] { "direct", "expert", "broadcast" })
        {
            using var store = new RecallTestStore();
            store.Add("note", Ns, note, .99f, state: "archived");
            store.Add("wrong-project", Ns, answer.Replace("Nectar", "Cedar"), .99f);
            if (route == "expert") store.Dispatcher.CreateExpert("nectar", "Nectar production operations");
            string? ns = route == "direct" ? Ns : null;
            var absent = Assert.IsType<RecallResult>(store.Tools().Recall(query, ns));
            Assert.Empty(absent.Results);
            Assert.Equal("insufficient_evidence", absent.QueryEvidence?.Status);
            store.Add("answer", Ns, answer, .7f);
            store.Restart();
            var present = Assert.IsType<RecallResult>(store.Tools().Recall(query, ns));
            Assert.Equal("answer", Assert.Single(present.Results).Id);
            Assert.DoesNotContain("Cedar", JsonSerializer.Serialize(present));
            Assert.Equal("archived", store.Index.Get("note", Ns, "")!.LifecycleState);
            Assert.Equal(1, store.Index.Get("note", Ns, "")!.AccessCount);
            Assert.Equal(1, store.Index.Get("wrong-project", Ns, "")!.AccessCount);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void NewQuestionShapesPreserveTenantAccessThresholdAndArchiveRules(string query, string answer, string note)
    {
        using var store = new RecallTestStore();
        foreach (string ns in new[] { Ns, "private", "empty" }) store.Registry.EnsureOwnership(ns, "owner", "team");
        store.Registry.Share(Ns, "owner", "reader", "read", "team");
        store.Registry.Share("empty", "owner", "reader", "read", "team");
        store.Add("note", Ns, note, tenant: "team");
        store.Add("same", "private", "PRIVATE_CANARY " + answer, tenant: "team");
        store.Add("same", Ns, "FOREIGN_CANARY " + answer, tenant: "foreign");
        store.Add("same", Ns, "LEGACY_CANARY " + answer);
        var tools = store.Tools("reader", "team");
        Assert.Empty(Assert.IsType<RecallResult>(tools.Recall(query)).Results);
        store.Add("answer", Ns, answer, .6f, state: "archived", tenant: "team");
        store.Restart();
        tools = store.Tools("reader", "team");
        foreach (string? ns in new string?[] { Ns, null })
        {
            Assert.Empty(Assert.IsType<RecallResult>(tools.Recall(query, ns, minScore: .8f)).Results);
            var result = Assert.IsType<RecallResult>(tools.Recall(query, ns));
            Assert.Equal("answer", Assert.Single(result.Results).Id);
            Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(result));
        }
        Assert.Empty(Assert.IsType<RecallResult>(tools.Recall(query, "private")).Results);
        Assert.Empty(Assert.IsType<RecallResult>(tools.Recall(query, "empty")).Results);
        Assert.Equal("archived", store.Index.Get("answer", Ns, "team")!.LifecycleState);
        Assert.Equal(1, store.Index.Get("same", "private", "team")!.AccessCount);
        Assert.Equal(1, store.Index.Get("same", Ns, "foreign")!.AccessCount);
        Assert.Equal(1, store.Index.Get("same", Ns, "")!.AccessCount);
    }
}
