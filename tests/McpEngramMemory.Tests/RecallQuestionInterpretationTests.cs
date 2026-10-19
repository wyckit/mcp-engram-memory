using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class RecallQuestionInterpretationTests
{
    private const string Ns = "expert_nectar";

    public static TheoryData<string, string, string> Paraphrases => new()
    {
        { "Who Is Responsible For Nectar Production Deployments?", "Leon owns Nectar production deployments.", "Leon owns Nectar staging deployments." },
        { "Who is responsible for Nectar production deployments?", "Leon owns Nectar production deployments.", "Nectar production deployments run every day." },
        { "What URL does nectar use for production health probes?", "Nectar production readiness endpoint is /ready. Internal probes use HTTP.", "Cedar production readiness endpoint is /ready." },
        { "Which HTTP path should nectar readiness probes call?", "Nectar production readiness endpoint is /ready. Internal probes use HTTP.", "Cedar production readiness endpoint is /ready." },
        { "How long does Nectar wait for its payment gateway before timing out?", "Nectar payment gateway timeout is 45 seconds.", "Nectar payment gateway waits for a connection." },
        { "What is the retention period for Nectar database backups?", "Nectar PostgreSQL backups are retained for 35 days.", "Nectar database backups run daily." },
        { "Which fields are included in Nectar's context cache fingerprint?", "Nectar context fingerprint covers content and metadata.", "Cedar context cache fingerprint covers content and metadata." },
        { "How quickly must Nectar recover after an outage?", "Nectar recovery drills run quarterly. The recovery time objective is 40 minutes.", "Nectar recovery drills last 40 minutes." },
        { "What is the RTO for Nectar?", "Nectar must recover within 40 minutes.", "Nectar recovery drills run every month." },
        { "When does Nectar take its nightly database snapshots?", "Nectar PostgreSQL backups run at 04:00 UTC daily.", "Nectar recovered the database from a snapshot in 22 minutes." },
        { "At what time does Nectar back up its database each night?", "Nectar PostgreSQL backups run at 04:00 UTC daily.", "Nectar database was recovered from a snapshot in 22 minutes." },
        { "Could you tell me at what time Nectar backs up its database?", "Nectar PostgreSQL backups run at 04:00 UTC daily.", "Nectar database was recovered from a snapshot in 22 minutes." },
        { "Could you tell me the Nectar QA readiness endpoint?", "Nectar QA readiness endpoint is /qa/ready.", "Nectar testing readiness endpoint is /ready." },
        { "Please show me where Nectar UAT database is hosted.", "Nectar UAT database is hosted in eu-west-2.", "Nectar staging database is hosted in eu-west-2." },
        { "What is Nectar payment gateway timeout jitter?", "Nectar payment gateway timeout jitter is two seconds.", "Nectar payment gateway timeout is 45 seconds." },
        { "What currency is the Nectar monthly budget in?", "Nectar monthly budget is 1000 USD.", "Nectar monthly budget is 1000." }
    };

    [Theory]
    [MemberData(nameof(Paraphrases))]
    public void RewordingPreservesTheAnswerAndRejectsAnAdjacentFact(string query, string answer, string adjacent)
    {
        foreach (string route in new[] { "direct", "expert", "broadcast" })
        {
            using var store = new RecallTestStore();
            var wrong = store.Add("wrong", Ns, adjacent, .99f, state: "archived");
            if (route == "expert") store.Dispatcher.CreateExpert("nectar", query);
            string? ns = route == "direct" ? Ns : null;
            var absent = Assert.IsType<RecallResult>(store.Tools().Recall(query, ns, k: 1));
            Assert.Empty(absent.Results);
            Assert.Equal("insufficient_evidence", absent.QueryEvidence?.Status);
            store.Add("answer", Ns, answer, .7f);
            var present = Assert.IsType<RecallResult>(store.Tools().Recall(query, ns, k: 1));
            Assert.Equal("answer", Assert.Single(present.Results).Id);
            Assert.Null(present.QueryEvidence);
            Assert.Equal(1, wrong.AccessCount);
            Assert.Equal("archived", wrong.LifecycleState);
        }
    }

    [Theory]
    [InlineData("Please tell me what was Nectar staging readiness endpoint?")]
    [InlineData("Can you tell me how Nectar staging and production readiness endpoints compare?")]
    public void WrappedHistoricalAndComparisonRequestsKeepTheirOptOut(string query)
    {
        using var store = new RecallTestStore();
        store.Add("context", Ns, "Nectar production readiness endpoint is /prod.");
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(query, Ns));
        Assert.Single(result.Results);
        Assert.Null(result.QueryEvidence);
    }

    [Theory]
    [InlineData("Can you tell me the Nectar QA readiness endpoint?")]
    [InlineData("Please tell me who owns Nectar production deployments.")]
    public void ARequestStoredAsMemoryIsNotItsOwnAnswer(string query) =>
        Assert.False(RecallQueryEvidence.Parse(query)!.Matches(query, Ns));

    [Theory]
    [InlineData("Nectar QA notes. The staging readiness endpoint is /stage.")]
    [InlineData("Nectar QA notes. The cedar readiness endpoint is /cedar.")]
    [InlineData("Nectar QA notes. The cedar database is in eu-west-2. The readiness endpoint is /cedar.")]
    [InlineData("Nectar QA and production readiness endpoints are /unknown.")]
    public void NewEnvironmentsCannotBeBorrowedAcrossContraryStatements(string text) =>
        Assert.False(RecallQueryEvidence.Parse("Please tell me the Nectar QA readiness endpoint.")!.Matches(text, Ns));
}
