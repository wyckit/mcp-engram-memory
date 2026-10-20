using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Tools;
using static McpEngramMemory.Tests.IndependentRecallValidation;

namespace McpEngramMemory.Tests;

[Trait("Category", "IndependentRecall")]
public sealed class RecallConcurrentLoadTests(ITestOutputHelper output)
{
    private const string Tenant = "load-a", Hot = "expert_citrine_ops", Private = "load-private", Guard = "load-guard";
    private const int Rounds = 48;
    internal sealed record Sample(string Lane, string Phase, int Round, long Start, long End, string? Strategy = null)
    {
        public double Milliseconds => (End - Start) * 1000.0 / Stopwatch.Frequency;
    }
    private static readonly string[] Canaries = ["PRIVATE_LOAD_CANARY", "FOREIGN_LOAD_CANARY", "LEGACY_LOAD_CANARY", "GRAPH_LOAD_CANARY"];

    [Theory]
    [InlineData("json", 1000)]
    [InlineData("json", 5000)]
    [InlineData("sqlite", 1000)]
    [InlineData("sqlite", 5000)]
    public void ConcurrentRecallWritesAndMaintenancePreserveScopeAndDurability(string backend, int size)
    {
        var started = DateTimeOffset.UtcNow;
        var corpus = Load();
        var sourceLabels = corpus.Entries.ToDictionary(e => e.Id);
        using var embedding = new NoisyRecallEvaluation.MeasuredEmbedding();
        using var store = new Store(backend, embedding);
        // Real empty profile catalogue for the broadcast control. All data reads,
        // authorization and mutations still use the SAME contended index/store.
        using var noProfiles = new Store(backend, embedding);
        var positive = corpus.Queries.Single(q => q.Id == "deployment-contact");
        var negative = corpus.Queries.Single(q => q.Id == "absent-secret");
        string answer = corpus.Entries.Single(e => e.Id == "c-owner").Text;
        float[] vector = embedding.Embed(answer);
        var seed = Stopwatch.StartNew();
        foreach (var m in corpus.Entries)
            store.Add(m.Id, m.Ns, m.Text, embedding.Embed(m.Text), Tenant, m.State);
        string[] namespaces = [Hot, "expert_citrine_data", "expert_mariner_ops", "load-0", "load-1", "load-2", "load-3", "load-4"];
        for (int i = corpus.Entries.Length; i < size; i++)
        {
            string text = $"Library catalogue item {i:D5}: the illustrated volume describes coastal birds, tidal plants and navigation charts.";
            store.Add($"background-{i}", namespaces[i % 8], text, embedding.Embed(text), Tenant);
        }
        Assert.Equal(size, store.Index.GetAllAcrossTenants().Count);
        Assert.Equal(8, store.Index.GetAllAcrossTenants().Select(e => e.Ns).Distinct().Count());
        foreach (var ns in namespaces.Append(Guard).Append("load-empty"))
        {
            store.Registry.EnsureOwnership(ns, "owner", Tenant);
            Assert.Equal("shared", store.Registry.Share(ns, "owner", "reader", "read", Tenant).Status);
        }
        store.Registry.EnsureOwnership(Private, "owner", Tenant);
        foreach (var p in corpus.Profiles) store.Profile(p, embedding.Embed(p.Description));
        AddGuard("c-owner", Private, Canaries[0], Tenant);
        AddGuard("c-owner", Hot, Canaries[1], "load-b");
        AddGuard("c-owner", Hot, Canaries[2], "");
        store.Add("guard-anchor", Guard, answer, vector, Tenant, "archived");
        AddGuard("guard-hidden", Private, Canaries[3], Tenant);
        store.Graph.AddEdge(new GraphEdge("guard-anchor", "guard-hidden", "elaborates", tenantId: Tenant));
        Assert.Contains(store.Graph.GetAllEdges(Tenant), e => e.SourceId == "guard-anchor" && e.TargetId == "guard-hidden");
        store.Flush();
        seed.Stop();
        CheckBoundaries();
        double restartMs = store.Restart();
        var samples = new ConcurrentQueue<Sample>();
        var first = Stopwatch.StartNew();
        Read("direct", 0); // loaded restart; query embedding and access path included
        first.Stop();
        double firstRequestMs = first.Elapsed.TotalMilliseconds;
        foreach (string mode in Modes)
            for (int i = 0; i < 12; i++) TimedRead(mode, "serial", i);
        for (int i = 0; i < 12; i++) Timed("writer-serial", "serial", i, () => WriteBatch("serial", i));

        var workers = new ConcurrentTestWorkers();
        var barrier = new Barrier(6);
        int[] progress = new int[6];
        int mutations = 0, maintenance = 0;
        var lifecycle = new LifecycleEngine(store.Index, store.Storage);
        var elapsed = Stopwatch.StartNew();
        var actions = new List<Action<CancellationToken>>();
        for (int lane = 0; lane < 3; lane++)
        {
            int local = lane;
            actions.Add(token => RunRounds(local, token, round =>
            {
                TimedRead(Modes[local], "concurrent", round);
                // Observe complete published versions without requiring a read that races a
                // writer to see its next value. The final acknowledged version is checked later.
                for (int writer = 0; writer < 2; writer++)
                {
                    var entry = store.Index.Get($"version-{writer}", Hot, Tenant);
                    if (entry is not null)
                    {
                        int version = int.Parse(entry.Metadata["version"]);
                        Assert.Equal($"Writer {writer} committed version {version}.", entry.Text);
                        Assert.InRange(version, lastSeen[local, writer], Rounds - 1);
                        lastSeen[local, writer] = version;
                    }
                }
            }));
        }
        for (int lane = 0; lane < 2; lane++)
        {
            int local = lane;
            actions.Add(token => RunRounds(local + 3, token, round =>
                Timed($"writer-{local}", "concurrent", round, () =>
                {
                    WriteBatch(local.ToString(), round);
                    Interlocked.Add(ref mutations, 4);
                })));
        }
        actions.Add(token => RunRounds(5, token, round => Timed("maintenance", "concurrent", round, () =>
        {
            Assert.True(lifecycle.RunDecayCycle(Hot, Tenant, decayRate: 0).ProcessedCount > 0);
            Interlocked.Increment(ref maintenance);
        })));
        try
        {
            workers.RunWithDiagnostics(TimeSpan.FromSeconds(180),
                () => $"backend={backend}, size={size}, rounds=[{string.Join(",", progress)}], mutations={mutations}, maintenance={maintenance}",
                actions.ToArray());
        }
        finally
        {
            // A deadlock must not be disguised by disposing resources under live workers.
            store.Retain = noProfiles.Retain = !workers.IsQuiescent;
            if (workers.IsQuiescent) barrier.Dispose();
        }
        elapsed.Stop();
        Assert.All(progress, p => Assert.Equal(Rounds, p));
        Assert.Equal(384, mutations);
        Assert.Equal(48, maintenance);
        var all = samples.ToArray();
        var reads = all.Where(s => s.Phase == "concurrent" && Modes.Contains(s.Lane)).ToArray();
        var writes = all.Where(s => s.Phase == "concurrent" && s.Lane.StartsWith("writer-")).ToArray();
        Assert.Equal(144, reads.Length);
        Assert.Equal(96, writes.Length);
        int overlappingReads = reads.Count(r => writes.Any(w => r.Start < w.End && w.Start < r.End));
        Assert.True(overlappingReads > 0, "Read/write request intervals did not overlap; load evidence is invalid.");
        CheckBoundaries();
        double finalRestartMs = store.Restart();
        foreach (var (lane, count) in new[] { ("serial", 12), ("0", Rounds), ("1", Rounds) })
        {
            for (int i = 0; i < count; i++)
            {
                Assert.Equal($"Writer {lane} append {i}.", store.Index.Get($"append-{lane}-{i}", Hot, Tenant)!.Text);
                Assert.Null(store.Index.Get($"ephemeral-{lane}-{i}", Hot, Tenant));
            }
            Assert.Equal((count - 1).ToString(), store.Index.Get($"version-{lane}", Hot, Tenant)!.Metadata["version"]);
        }
        Assert.Equal(answer, store.Index.Get("c-owner", Hot, Tenant)!.Text);
        Assert.Equal(vector, store.Index.Get("c-owner", Hot, Tenant)!.Vector);
        CheckBoundaries();
        foreach (string mode in Modes) { Read(mode, 0); Read(mode, 1); }
        var distributions = all.GroupBy(s => (s.Lane, s.Phase))
            .Select(g => new { g.Key.Lane, g.Key.Phase, Milliseconds = Distribution(g.Select(s => s.Milliseconds)) }).ToArray();
        var ratios = Modes.Select(mode => new
        {
            Mode = mode,
            P95Ratio = Percentile(all.Where(s => s.Lane == mode && s.Phase == "concurrent")) /
                Percentile(all.Where(s => s.Lane == mode && s.Phase == "serial"))
        }).ToArray();
        output.WriteLine(Write($"recall-load-{backend}-{size}", new
        {
            SchemaVersion = 1, Backend = backend, DataMemories = size, Namespaces = 8, Metadata = Metadata(),
            StartedAtUtc = started, CompletedAtUtc = DateTimeOffset.UtcNow, SeedMilliseconds = seed.Elapsed.TotalMilliseconds,
            RestartMilliseconds = restartMs, FirstRequestMilliseconds = firstRequestMs, FinalRestartMilliseconds = finalRestartMs,
            ConcurrentMilliseconds = elapsed.Elapsed.TotalMilliseconds, StopwatchFrequency = Stopwatch.Frequency,
            ConcurrentReads = reads.Length, ConcurrentWriterBatches = writes.Length, Mutations = mutations,
            MaintenancePasses = maintenance, OverlappingReads = overlappingReads, DurableAppends = 108,
            FinalVersions = 3, AbsentDeletes = 108, ScopeChecksPassed = true, DurabilityPassed = true,
            Distributions = distributions, ReadP95Ratios = ratios, Samples = all,
            WriterP95Ratios = new[] { "writer-0", "writer-1" }.Select(lane => new
            {
                Lane = lane,
                P95Ratio = Percentile(all.Where(s => s.Lane == lane && s.Phase == "concurrent")) /
                    Percentile(all.Where(s => s.Lane == "writer-serial" && s.Phase == "serial"))
            }).ToArray()
        }));

        void AddGuard(string id, string ns, string marker, string tenant)
        {
            string text = $"Citrine production deployment owner is {marker}.";
            store.Add(id, ns, text, embedding.Embed(text), tenant, "archived");
        }
        CompositeTools Tools(string mode) => store.Tools("reader", Tenant, mode == "broadcast" ? noProfiles.Dispatcher : null);
        void Safe(RecallResult result)
        {
            string response = JsonSerializer.Serialize(result, Json);
            Assert.All(Canaries, marker => Assert.DoesNotContain(marker, response));
            Assert.Null(result.Conflicts?.FirstOrDefault(c => c.Resolution is not null || c.Status == "resolved"));
        }
        string Read(string mode, int round)
        {
            var q = round % 2 == 0 ? positive : negative;
            var result = Assert.IsType<RecallResult>(Tools(mode).Recall(q.Query, mode == "direct" ? Hot : null));
            Safe(result);
            Assert.InRange(result.Results.Count, 0, 5);
            Assert.All(result.Results, r =>
            {
                if (!sourceLabels.TryGetValue(r.Id, out var label)) return;
                Assert.Equal("Citrine", label.Project);
                Assert.Equal("production", label.Environment);
                Assert.Equal(label.Text, r.Text);
                if (mode == "direct") Assert.Equal(Hot, label.Ns);
            });
            if (mode == "broadcast") Assert.Equal("broadcast", result.Strategy);
            if (round % 2 == 0) Assert.Contains(result.Results, r => r.Id == "c-owner" && r.Text == answer);
            else Assert.Empty(result.Results);
            return result.Strategy;
        }
        void WriteBatch(string lane, int round)
        {
            store.Add($"append-{lane}-{round}", Hot, $"Writer {lane} append {round}.", vector, Tenant);
            store.Add($"version-{lane}", Hot, $"Writer {lane} committed version {round}.", vector, Tenant,
                metadata: new() { ["version"] = round.ToString() });
            store.Add($"ephemeral-{lane}-{round}", Hot, "Ephemeral load marker.", vector, Tenant);
            Assert.True(store.Index.Delete($"ephemeral-{lane}-{round}", Hot, Tenant));
        }
        void Timed(string lane, string phase, int round, Action action)
        {
            long begin = Stopwatch.GetTimestamp();
            action();
            samples.Enqueue(new(lane, phase, round, begin, Stopwatch.GetTimestamp()));
        }
        void TimedRead(string mode, string phase, int round)
        {
            long begin = Stopwatch.GetTimestamp();
            string strategy = Read(mode, round);
            samples.Enqueue(new(mode, phase, round, begin, Stopwatch.GetTimestamp(), strategy));
        }
        void RunRounds(int lane, CancellationToken token, Action<int> action)
        {
            for (int round = 0; round < Rounds; round++)
            {
                barrier.SignalAndWait(token);
                token.ThrowIfCancellationRequested();
                action(round);
                Volatile.Write(ref progress[lane], round + 1);
                barrier.SignalAndWait(token);
            }
        }
        void CheckBoundaries()
        {
            var tools = Tools("automatic");
            foreach (string ns in new[] { Private, "load-empty" })
            {
                var result = Assert.IsType<RecallResult>(tools.Recall(positive.Query, ns));
                Assert.Empty(result.Results); Safe(result);
            }
            var threshold = Assert.IsType<RecallResult>(tools.Recall(positive.Query, Hot, minScore: 1f));
            Assert.Empty(threshold.Results); Safe(threshold);
            var guard = Assert.IsType<RecallResult>(tools.Recall(positive.Query, Guard));
            Assert.Contains(guard.Results, r => r.Id == "guard-anchor"); Safe(guard);
            // Authorized reads count as access even under a read-only grant. Only
            // resurrection requires write permission; inaccessible rows stay untouched.
            Assert.Equal("archived", store.Index.Get("guard-anchor", Guard, Tenant)!.LifecycleState);
            foreach (var key in new[] { ("c-owner", Private, Tenant), ("c-owner", Hot, "load-b"),
                         ("c-owner", Hot, ""), ("guard-hidden", Private, Tenant) })
            {
                var entry = store.Index.Get(key.Item1, key.Item2, key.Item3)!;
                Assert.True(entry.AccessCount == 1, $"{key}: access count {entry.AccessCount}");
                Assert.Equal("archived", entry.LifecycleState);
            }
        }
    }

    // Per-test instance state; each reader owns one row and versions are never shared across lanes.
    private readonly int[,] lastSeen = { { -1, -1 }, { -1, -1 }, { -1, -1 } };
    private static double Percentile(IEnumerable<Sample> source)
    {
        var sorted = source.Select(s => s.Milliseconds).Order().ToArray();
        return sorted[(int)Math.Ceiling(sorted.Length * .95) - 1];
    }
}
