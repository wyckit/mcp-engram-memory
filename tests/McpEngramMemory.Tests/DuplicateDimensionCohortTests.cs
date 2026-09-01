using System.Reflection;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Retrieval;

namespace McpEngramMemory.Tests;

/// <summary>
/// Regressions for the spectral path's handling of mixed embedding dimensions. Only same-length
/// vectors are comparable, so the pair space factors along length-cohort lines — and every cohort
/// large enough to pay for a subspace must get its own projection: leaving a large cohort
/// unprojected silently reintroduces the quadratic full-dimension cost the subspace exists to
/// avoid, and every skipped comparison is still attested to the resume cursor, so a row that is
/// neither projected nor directly resolved is a pair no future sweep will ever revisit.
/// </summary>
public sealed class DuplicateDimensionCohortTests
{
    private static (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized) Candidate(string id, float[] v)
        => (new CognitiveEntry(id, v, "dims", $"entry {id}"), VectorMath.Norm(v), null);

    private static float[] RandomVector(Random rng, int dim)
    {
        var v = new float[dim];
        for (int k = 0; k < dim; k++) v[k] = (float)((rng.NextDouble() * 2) - 1);
        return v;
    }

    private static bool IsPair((string IdA, string IdB, float Similarity) p, string x, string y)
        => (p.IdA == x && p.IdB == y) || (p.IdA == y && p.IdB == x);

    [Fact]
    public void SpectralPath_ProjectsEveryLargeCohort_AndFindsPairsInBoth()
    {
        var rng = new Random(20260830);
        var candidates = new List<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>();

        // Two large cohorts — the post-migration shape. Each is over the pivot, so EACH must be
        // projected: any single-cohort selection (first-seen or dominant) leaves the other side
        // paying direct full-dimension rows for its entire triangle.
        var oldDup = RandomVector(rng, 32);
        for (int i = 0; i < 260; i++)
            candidates.Add(Candidate($"old-{i:D4}", i is 10 or 11 ? (float[])oldDup.Clone() : RandomVector(rng, 32)));

        var newDup = RandomVector(rng, 64);
        for (int i = 0; i < 300; i++)
            candidates.Add(Candidate($"new-{i:D4}", i is 20 or 21 ? (float[])newDup.Clone() : RandomVector(rng, 64)));

        long projectionDots = -1;
        long confirmationDots = -1;
        var found = new DuplicateDetector()
            .StreamDuplicates(candidates, 0.999f, PairScanWindow.Full, CancellationToken.None,
                onProjectionProbe: p => (projectionDots, confirmationDots) = (p.ProjectionDots, p.ConfirmationDots))
            .ToList();

        // Both planted duplicate pairs surface, whichever cohort they live in.
        Assert.Contains(found, p => IsPair(p, "old-0010", "old-0011"));
        Assert.Contains(found, p => IsPair(p, "new-0020", "new-0021"));

        // BOTH cohorts ran in projection space: C(260,2) + C(300,2) = 33,670 + 44,850 = 78,520
        // projection dots. Any single projected cohort tops out at 44,850, so the floor below
        // separates the behaviors while tolerating the odd zero-norm projection row.
        Assert.InRange(projectionDots, 70_000, 78_520);

        // And the expensive side stayed suppressed: full-dimension cosines are the planted
        // duplicates plus whatever random pairs leak through the widened gate — not a cohort's
        // whole triangle. The probe now counts unprojected fallback dots too, so a quadratic
        // regression on either cohort lands squarely on this assertion.
        Assert.InRange(confirmationDots, 1, 2_000);
    }

    [Fact]
    public void SpectralPath_ZeroProjectionSurvivor_StillComparedAgainstEarlierAnchors()
    {
        // The production SVD cannot be steered onto an exactly-zero projection deterministically,
        // so the private cohort walk is driven directly with a forced-zero projection row. The
        // arrangement: "zero-proj" duplicates "first" in full dimension while its projection is
        // zero — its own direct row covers only LATER candidates, so the (first, zero-proj)
        // pair belongs to the first anchor's row and exists nowhere else.
        var candidates = new List<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>
        {
            Candidate("first", new[] { 1f, 0f, 0f, 0f }),
            Candidate("zero-proj", new[] { 1f, 0f, 0f, 0f }),
            Candidate("third", new[] { 0f, 1f, 0f, 0f }),
        };
        var cohortOf = new[] { 0, 0, 0 };
        var posInCohort = new[] { 0, 1, 2 };
        var cohortMembers = new List<int[]> { new[] { 0, 1, 2 } };
        var cohortProjections = new List<float[][]?>
        {
            new[]
            {
                new[] { 1f, 0f },
                new[] { 0f, 0f },
                new[] { 0f, 1f },
            },
        };
        var cohortProjNorms = new List<float[]?> { new[] { 1f, 0f, 1f } };

        var method = typeof(DuplicateDetector).GetMethod(
            "StreamCohortSurvivors", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var stream = (IEnumerable<(string IdA, string IdB, float Similarity)>)method!.Invoke(null, new object?[]
        {
            candidates, cohortOf, posInCohort, cohortMembers, cohortProjections, cohortProjNorms,
            0.9f, PairScanWindow.Full, CancellationToken.None, null, null,
        })!;
        var found = stream.ToList();

        Assert.Contains(found, p => IsPair(p, "first", "zero-proj"));
        Assert.DoesNotContain(found, p => IsPair(p, "first", "third"));
    }
}
