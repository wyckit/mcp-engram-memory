using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// Two properties that survived four review rounds because nothing in the suite could STATE them.
///
/// The first is a cost property. The projection prefilter in <see cref="DuplicateDetector"/> is the
/// innermost loop of the whole subsystem — on the order of 18 million executions in one default
/// background window — and a prefilter that has stopped prefiltering yields exactly the same pairs
/// as one that works, because the full-dimension confirmation behind it makes the final call either
/// way. Nothing in the result, the graph, or <c>AutoLinkScanProbe</c> moves. Only the amount of work
/// moves, so the work is what is asserted here, through the
/// <see cref="ProjectionScanProbe"/> seam added for exactly this reason.
///
/// The second is a lifetime property. <see cref="ClusterManager"/> hands the persistence layer a
/// snapshot that is read minutes later, on a thread-pool thread, by a JSON serializer that holds
/// none of the cluster lock. Every test in the suite asserts on file contents after a
/// <c>Flush()</c>, by which time no mutation is in flight, so the window in which a mutator and the
/// serializer overlap is never entered. It is entered here with no threads and no timing at all:
/// the consumer is suspended INSIDE its own enumeration of the captured member list — precisely
/// where <c>JsonSerializer.Serialize</c> sits — the interfering write is performed, and then the
/// consumer is resumed.
/// </summary>
public sealed class HotPathAndSnapshotTests : IDisposable
{
    private const string Ns = "hotpath";
    private const string Tenant = "";

    private readonly string _dataPath;
    private readonly PersistenceManager _persistence;
    private readonly CapturingClusterStore _store;
    private readonly CognitiveIndex _index;
    private readonly ClusterManager _clusters;

    public HotPathAndSnapshotTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"hotpath_test_{Guid.NewGuid():N}");
        // A debounce long enough that no timer can fire during a test. The captured providers are
        // invoked by the test itself, at the instant the test chooses, which is what makes the
        // interference deterministic instead of a race the test hopes to win.
        _persistence = new PersistenceManager(_dataPath, debounceMs: 600_000);
        _store = new CapturingClusterStore(_persistence);
        _index = new CognitiveIndex(_persistence);
        _clusters = new ClusterManager(_index, _store);

        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, Ns, "entry a", tenantId: Tenant));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f, 0f }, Ns, "entry b", tenantId: Tenant));
        _index.Upsert(new CognitiveEntry("c", new[] { 0f, 0f, 1f }, Ns, "entry c", tenantId: Tenant));
        _index.Upsert(new CognitiveEntry("d", new[] { 1f, 1f, 0f }, Ns, "entry d", tenantId: Tenant));
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 1. THE PROJECTION KERNEL — a pure lowering change, so equivalence is the test
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The vector kernel against a CLOSED FORM, at every length from empty to past four SIMD
    /// vectors, so no tolerance is involved and the assertion is exact.
    ///
    /// Integer-valued operands are the point: every partial sum is exactly representable in FP32, so
    /// re-association cannot move the answer and any difference is a real defect rather than a last
    /// bit. The two failure modes a four-accumulator kernel actually has are both fatal here — a
    /// stride that skips a block loses whole terms and undershoots, and a tail loop that re-walks
    /// elements the quad loop already consumed double-counts and overshoots.
    ///
    /// The length sweep matters because K is not fixed by this class. It is
    /// <c>EmbeddingSubspace.DefaultTopK</c> today (64, a multiple of every SIMD width from 4 to 16
    /// lanes, so the tail paths are never taken in production) and <c>Build</c> silently lowers it
    /// when the candidate count or the embedding dimension is smaller. A kernel correct only at
    /// K=64 would be wrong exactly on the small inputs no benchmark covers.
    /// </summary>
    [Fact]
    public void ProjectionDot_EqualsTheClosedForm_AtEveryLengthAndAlignment()
    {
        for (int n = 0; n <= (4 * System.Numerics.Vector<float>.Count) + 7; n++)
        {
            var ones = new float[n];
            var ramp = new float[n];
            for (int i = 0; i < n; i++)
            {
                ones[i] = 1f;
                ramp[i] = i + 1;
            }

            // sum(1 * 1) over n terms.
            float expectedOnes = n;
            Assert.Equal(expectedOnes, DuplicateDetector.ProjectionDot(ones, ones));

            // sum(i + 1) for i in [0, n) = n(n + 1) / 2 — exact in FP32 well past this range, and
            // position-sensitive, so a kernel that visited the right COUNT of elements in the wrong
            // ORDER of blocks still fails.
            float expectedRamp = n * (n + 1) / 2;
            Assert.Equal(expectedRamp, DuplicateDetector.ProjectionDot(ramp, ones));
        }
    }

    /// <summary>
    /// The same kernel on dense signed operands, against a double-precision reference.
    ///
    /// Exact equality is deliberately NOT asserted: vectorizing re-associates the additions, so the
    /// last bits differ and differ by SIMD width across hosts. What must hold is that the result
    /// tracks the true value to well inside the 0.10 of slack the projection gate is widened by,
    /// which is the reason re-association is safe on this path at all.
    /// </summary>
    [Fact]
    public void ProjectionDot_TracksADoublePrecisionReference_OnDenseSignedOperands()
    {
        uint state = 0x5EED_1234u;
        for (int n = 1; n <= (4 * System.Numerics.Vector<float>.Count) + 7; n++)
        {
            var a = new float[n];
            var b = new float[n];
            double reference = 0d;
            for (int i = 0; i < n; i++)
            {
                a[i] = NextSigned(ref state);
                b[i] = NextSigned(ref state);
                reference += (double)a[i] * b[i];
            }

            float actual = DuplicateDetector.ProjectionDot(a, b);
            Assert.True(Math.Abs(actual - reference) < 1e-3,
                $"length {n}: kernel returned {actual}, reference {reference}");
        }
    }

    /// <summary>
    /// The length check is what makes the unchecked loads inside the kernel provably in range, so it
    /// has to be a refusal and not a silent truncation to the shorter operand.
    /// </summary>
    [Fact]
    public void ProjectionDot_RefusesOperandsOfDifferentLengths()
        => Assert.Throws<ArgumentException>(
            () => { _ = DuplicateDetector.ProjectionDot(new float[8], new float[9]); });

    // ──────────────────────────────────────────────────────────────────────────────
    // 2. THE PAIR SET MUST NOT MOVE, AND THE COST MUST
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The invariance half. A lowering change is only a lowering change if the pairs it yields are
    /// the pairs the naive triangular walk yields, in the same order — so that is asserted as a
    /// SEQUENCE, not a set: the scan order is anchor-major then inner-index ascending, and a change
    /// that reordered the walk would still satisfy a set comparison.
    ///
    /// The fixture separates its five planted twin pairs (cosine above 0.9999) from every other pair
    /// (pseudo-random in 128 dimensions, so cosine concentrates near zero with a standard deviation
    /// under 0.09) by a margin far wider than the 0.10 the projection gate is widened by. That
    /// removes the ONE legitimate reason the spectral path may differ from the direct one —
    /// truncation recall loss — and leaves the kernel as the only thing under test.
    /// </summary>
    [Fact]
    public void SpectralScan_YieldsExactlyTheTriangularWalksPairs_InTheSameOrder()
    {
        var candidates = SpectralCandidates();
        const float threshold = 0.95f;

        var expected = new List<(string, string)>();
        for (int i = 0; i < candidates.Count; i++)
            for (int j = i + 1; j < candidates.Count; j++)
                if (Cosine(candidates, i, j) >= threshold)
                    expected.Add((candidates[i].Entry.Id, candidates[j].Entry.Id));

        // The fixture is only meaningful if it takes the spectral path at all, and if the twins are
        // the only thing in it that clears the threshold.
        Assert.True(candidates.Count >= DuplicateDetector.LowRankPivot,
            "the fixture must be above the pivot or this exercises the direct path");
        Assert.Equal(PlantedPairs, expected.Count);

        var actual = new DuplicateDetector()
            .StreamDuplicates(candidates, threshold, PairScanWindow.Full, CancellationToken.None)
            .Select(p => (p.IdA, p.IdB))
            .ToList();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The cost half — the property the seam was extended to carry.
    ///
    /// The spectral path exists to buy a 6x dimensional reduction: gate on a 64-dim dot so only a
    /// handful of pairs pay for the full-dimension FP32 cosine. Whether that bargain is actually
    /// being collected is invisible in the result, which is how a scalar, serially-dependent,
    /// interface-indexed inner loop sat under a SIMD confirmation step for four review rounds. The
    /// ratio below is the bargain, stated.
    ///
    /// It is also the assertion that catches a mis-lowered kernel from the other side: an
    /// over-counting stride inflates every projection dot, every pair clears the widened gate, and
    /// ConfirmationDots converges on ProjectionDots while the yielded pairs stay perfectly correct.
    /// </summary>
    [Fact]
    public void TheProjectionGate_ConfirmsOnlyAHandfulOfThePairsItExamines()
    {
        var candidates = SpectralCandidates();
        int n = candidates.Count;
        long allPairs = (long)n * (n - 1) / 2;

        ProjectionScanProbe probe = default;
        var yielded = new DuplicateDetector()
            .StreamDuplicates(candidates, 0.95f, PairScanWindow.Full, CancellationToken.None,
                p => probe = p)
            .ToList();

        // Every candidate is kept (uniform dimension, non-zero norm), so a full window examines the
        // entire triangle and the count is exact rather than approximate.
        Assert.Equal(allPairs, probe.ProjectionDots);
        Assert.Equal(PlantedPairs, yielded.Count);

        // Nothing but the planted twins should survive a gate set 0.10 below a 0.95 threshold: the
        // distractors are pseudo-random, and in the 64-dimensional projection space their pairwise
        // cosine concentrates around zero with a standard deviation near 0.125, so 0.85 sits about
        // seven deviations out. The bound below leaves two orders of magnitude of slack over that.
        Assert.True(probe.ConfirmationDots >= PlantedPairs,
            $"the gate dropped a true duplicate: {probe.ConfirmationDots} confirmations for {PlantedPairs} planted pairs");
        Assert.True(probe.ConfirmationDots * 20 < probe.ProjectionDots,
            $"the prefilter stopped filtering: {probe.ConfirmationDots} of {probe.ProjectionDots} pairs " +
            "paid for a full-dimension cosine");
    }

    /// <summary>
    /// The probe reports the work ACTUALLY done, including when the consumer walks away.
    ///
    /// <see cref="DuplicateDetector.FindDuplicates"/> is exactly that consumer: it stops at its
    /// output bound and disposes the enumerator mid-scan, which is the whole reason the streaming
    /// form exists. A probe that only fired on natural completion would report nothing for the one
    /// caller whose cost is most tightly bounded — and would report nothing at all for the
    /// cancellation path.
    /// </summary>
    [Fact]
    public void TheProjectionProbe_ReportsPartialWorkWhenTheStreamIsAbandoned()
    {
        var candidates = SpectralCandidates();
        int n = candidates.Count;
        long allPairs = (long)n * (n - 1) / 2;

        ProjectionScanProbe probe = default;
        int taken = 0;
        foreach (var _ in new DuplicateDetector().StreamDuplicates(
                     candidates, 0.95f, PairScanWindow.Full, CancellationToken.None, p => probe = p))
        {
            taken++;
            break;
        }

        Assert.Equal(1, taken);
        // The planted twins sit at adjacent indices from zero, so the first pair the walk examines
        // is the first pair it yields: the scan stops after one comparison, not after the triangle.
        Assert.Equal(1L, probe.ProjectionDots);
        Assert.Equal(1L, probe.ConfirmationDots);
        Assert.True(probe.ProjectionDots < allPairs,
            "abandoning after one pair must not have walked the whole triangle");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 3. THE CLUSTER SNAPSHOT MUST BE FROZEN, FOR EVERY MUTATOR
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>UpdateCluster</c> — the mutator the finding named first. Adding a member used to call
    /// <c>MemberIds.Add</c> on the very list a captured snapshot points at.
    /// </summary>
    [Fact]
    public void ACapturedSnapshot_SurvivesUpdateClusterEditingTheClusterUnderIt()
        => AssertCapturedSnapshotSurvives(m =>
            m.UpdateCluster("c1", addIds: new[] { "d" }, removeIds: null, label: null, tenantId: Tenant));

    /// <summary>
    /// <c>UpdateCluster</c> in the REMOVING direction. Add and remove take different branches, and
    /// only one of them was in the finding's reproduction.
    /// </summary>
    [Fact]
    public void ACapturedSnapshot_SurvivesUpdateClusterRemovingAMemberUnderIt()
        => AssertCapturedSnapshotSurvives(m =>
            m.UpdateCluster("c1", addIds: null, removeIds: new[] { "b" }, label: null, tenantId: Tenant));

    /// <summary>
    /// <c>RemoveEntryFromAllClusters</c> — the cascade delete reached from <c>delete_memory</c> and
    /// <c>purge_debates</c>, and the scenario in the finding: the debounce elapses quietly, the
    /// serializer starts walking, and a delete lands on the same cluster.
    /// </summary>
    [Fact]
    public void ACapturedSnapshot_SurvivesRemoveEntryFromAllClustersUnderIt()
        => AssertCapturedSnapshotSurvives(m => m.RemoveEntryFromAllClusters("b", tenantId: Tenant));

    /// <summary>
    /// <c>TransferMembership</c> — the merge path, and the third in-place mutator. It both removes
    /// and adds, so it bumps the list version twice.
    /// </summary>
    [Fact]
    public void ACapturedSnapshot_SurvivesTransferMembershipUnderIt()
        => AssertCapturedSnapshotSurvives(m => m.TransferMembership("b", "d", tenantId: Tenant));

    /// <summary>
    /// The mutators the finding did NOT name, swept for the same pattern.
    ///
    /// <c>StoreSummary</c> writes <c>SummaryEntryId</c> and the centroid phases write
    /// <c>Centroid</c>; both are single reference assignments, so neither can tear and neither ever
    /// threw inside the serializer. That is exactly why they are worth pinning: they were writing
    /// through into an already-captured snapshot silently, and a snapshot that acquires a summary id
    /// and a centroid it was never meant to carry is a half-applied edit written out as though it
    /// were whole. A save armed before the edit and fired after it would persist a cluster state
    /// that never existed at any single instant.
    /// </summary>
    [Fact]
    public void ACapturedSnapshot_DoesNotAcquireLabelSummaryOrCentroidWrittenAfterIt()
    {
        _clusters.CreateCluster("c1", Ns, new[] { "a", "b" }, "before", tenantId: Tenant);

        // The FIRST provider armed by CreateCluster — captured inside the write lock that stored the
        // cluster, before the centroid was computed outside the lock and applied in a second lock
        // section. That ordering is what makes the centroid observable here at all.
        var atCreation = ClusterC1(_store.ClusterProviders[0]());
        Assert.Null(atCreation.Centroid);
        Assert.Null(atCreation.SummaryEntryId);
        Assert.Equal("before", atCreation.Label);
        Assert.Equal(new[] { "a", "b" }, atCreation.MemberIds);

        _clusters.StoreSummary("c1", "a summary", new[] { 1f, 1f, 1f }, tenantId: Tenant);
        _clusters.UpdateCluster("c1", addIds: null, removeIds: null, label: "after", tenantId: Tenant);

        Assert.Null(atCreation.Centroid);
        Assert.Null(atCreation.SummaryEntryId);
        Assert.Equal("before", atCreation.Label);
        Assert.Equal(new[] { "a", "b" }, atCreation.MemberIds);

        // The control: the edits really did land, so the assertions above are about isolation and
        // not about the mutators quietly doing nothing.
        var latest = ClusterC1(_store.ClusterProviders[^1]());
        Assert.NotNull(latest.Centroid);
        Assert.Equal("summary:c1", latest.SummaryEntryId);
        Assert.Equal("after", latest.Label);
    }

    /// <summary>
    /// The interference shape all four mutator tests share.
    ///
    /// No threads and no timing. The consumer is suspended inside its own <c>foreach</c> over the
    /// captured member list — the position <c>JsonSerializer.Serialize</c> occupies when the
    /// debounce timer fires on a pool thread — the mutator runs to completion, and the consumer
    /// resumes. Before the fix the resumed <c>MoveNext</c> threw
    /// <c>InvalidOperationException: Collection was modified</c>, the storage provider caught and
    /// logged it, and nothing rescheduled the write.
    /// </summary>
    private void AssertCapturedSnapshotSurvives(Action<ClusterManager> interfere)
    {
        _clusters.CreateCluster("c1", Ns, new[] { "a", "b", "c" }, "before", tenantId: Tenant);

        var captured = ClusterC1(_store.ClusterProviders[^1]());
        var before = captured.MemberIds.ToList();
        Assert.Equal(new[] { "a", "b", "c" }, before);

        var walk = captured.MemberIds.GetEnumerator();
        Assert.True(walk.MoveNext());
        var walked = new List<string> { walk.Current };

        interfere(_clusters);

        while (walk.MoveNext()) walked.Add(walk.Current);
        walk.Dispose();

        Assert.Equal(before, walked);
        Assert.Equal(before, captured.MemberIds);

        // The control: the mutator really did change the manager's live state, so the snapshot's
        // stability is isolation rather than a no-op.
        var afterwards = ClusterC1(_store.ClusterProviders[^1]());
        Assert.NotEqual(before, afterwards.MemberIds);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Fixtures
    // ──────────────────────────────────────────────────────────────────────────────

    private const int PlantedPairs = 5;

    /// <summary>The one cluster these tests create, as the snapshot in question sees it.</summary>
    private static SemanticCluster ClusterC1(List<SemanticCluster> snapshot)
        => snapshot.Single(c => c.ClusterId == "c1");

    /// <summary>
    /// 300 candidates — comfortably above <see cref="DuplicateDetector.LowRankPivot"/>, so the
    /// spectral path is the one under test — in 128 full-rank dimensions, of which five adjacent
    /// pairs are near-identical twins and the rest are mutually near-orthogonal.
    ///
    /// FULL RANK, not a low-rank slice, and that is deliberate. A rank-deficient fixture would make
    /// the projection lossless and the comparison trivially exact, but it would also ask the
    /// randomized eigensolver for 64 components of a matrix that has fewer, which is a numerically
    /// degenerate request and a fixture that tests the solver's null-space handling rather than this
    /// class's kernel. Separating the twins from everything else by a margin far wider than the
    /// gate's slack achieves the same isolation without the degeneracy.
    ///
    /// The five twin pairs occupy indices 0..9, so the first pair the triangular walk examines is
    /// also the first one it yields — which is what lets the abandonment test assert an exact
    /// comparison count instead of a range.
    ///
    /// The generator is a local LCG rather than <c>Random</c> because the fixture must be identical
    /// across the three target frameworks this suite builds for; the assertions here are an exact
    /// pair SEQUENCE and an exact comparison COUNT.
    /// </summary>
    private static List<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> SpectralCandidates()
    {
        const int count = DuplicateDetector.LowRankPivot + 44;   // 300
        const int dim = 128;

        uint state = 0xC0FF_EE01u;
        var candidates = new List<(CognitiveEntry, float, QuantizedVector?)>(count);

        float[] Draw()
        {
            var v = new float[dim];
            for (int k = 0; k < dim; k++) v[k] = NextSigned(ref state);
            return v;
        }

        for (int p = 0; p < PlantedPairs; p++)
        {
            var basis = Draw();
            var twin = (float[])basis.Clone();
            for (int k = 0; k < dim; k++) twin[k] += NextSigned(ref state) * 0.01f;
            Add(basis);
            Add(twin);
        }
        while (candidates.Count < count) Add(Draw());

        return candidates;

        void Add(float[] v)
        {
            int i = candidates.Count;
            candidates.Add((new CognitiveEntry($"sp-{i:D4}", v, Ns, $"spectral {i}", tenantId: Tenant),
                VectorMath.Norm(v), null));
        }
    }

    private static float Cosine(
        IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> c, int i, int j)
        => VectorMath.Dot(c[i].Entry.Vector, c[j].Entry.Vector) / (c[i].Norm * c[j].Norm);

    /// <summary>A deterministic LCG draw in [-1, 1), identical on every runtime.</summary>
    private static float NextSigned(ref uint state)
    {
        state = unchecked((state * 1664525u) + 1013904223u);
        return ((state >> 8) * (1f / 8388608f)) - 1f;
    }

    /// <summary>
    /// A storage provider that keeps every cluster-save provider it is handed instead of arming a
    /// timer, so the test invokes them at the instant it chooses.
    ///
    /// This is the production hand-off exactly: <see cref="ClusterManager"/> captures its snapshot
    /// under the write lock and passes a closure; the real provider invokes that closure later, from
    /// a debounce callback on a thread-pool thread, and serializes whatever it returns. Everything
    /// else delegates to a real <see cref="PersistenceManager"/> so loads and index writes behave
    /// normally.
    /// </summary>
    private sealed class CapturingClusterStore : IStorageProvider
    {
        private readonly IStorageProvider _inner;

        public CapturingClusterStore(IStorageProvider inner) => _inner = inner;

        /// <summary>Every save provider armed so far, oldest first.</summary>
        public List<Func<List<SemanticCluster>>> ClusterProviders { get; } = new();

        public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider)
            => ClusterProviders.Add(dataProvider);

        public bool SupportsIncrementalWrites => _inner.SupportsIncrementalWrites;
        public NamespaceData LoadNamespace(string ns) => _inner.LoadNamespace(ns);
        public IReadOnlyList<string> GetPersistedNamespaces() => _inner.GetPersistedNamespaces();
        public void ScheduleSave(string ns, Func<NamespaceData> p) => _inner.ScheduleSave(ns, p);
        public void SaveNamespaceSync(string ns, NamespaceData data) => _inner.SaveNamespaceSync(ns, data);
        public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) => _inner.ScheduleUpsertEntry(ns, entry);
        public void ScheduleDeleteEntry(string ns, string entryId) => _inner.ScheduleDeleteEntry(ns, entryId);
        public List<GraphEdge> LoadGlobalEdges() => _inner.LoadGlobalEdges();
        public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> p) => _inner.ScheduleSaveGlobalEdges(p);
        public List<SemanticCluster> LoadClusters() => _inner.LoadClusters();
        public List<CollapseRecord> LoadCollapseHistory() => _inner.LoadCollapseHistory();
        public void ScheduleSaveCollapseHistory(Func<List<CollapseRecord>> p) => _inner.ScheduleSaveCollapseHistory(p);
        public Dictionary<string, DecayConfig> LoadDecayConfigs() => _inner.LoadDecayConfigs();
        public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> p) => _inner.ScheduleSaveDecayConfigs(p);
        public HnswSnapshot? LoadHnswSnapshot(string ns) => _inner.LoadHnswSnapshot(ns);
        public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot) => _inner.SaveHnswSnapshotSync(ns, snapshot);
        public void DeleteHnswSnapshot(string ns) => _inner.DeleteHnswSnapshot(ns);
        public Task DeleteNamespaceAsync(string ns) => _inner.DeleteNamespaceAsync(ns);
        public void Flush() => _inner.Flush();

        // The test owns the inner provider's lifetime and disposes it directly.
        public void Dispose() { }
    }
}
