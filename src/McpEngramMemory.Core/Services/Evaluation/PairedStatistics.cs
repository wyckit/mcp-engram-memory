using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// Pure statistical primitives for the benchmark regression gate: a one-sided paired
/// Student t-test over per-query metric deltas, the Student-t CDF (via the regularized
/// incomplete beta function), and Holm-Bonferroni step-down multiple-comparison control.
/// No I/O, no logging — deterministic math only.
/// </summary>
public static class PairedStatistics
{
    /// <summary>
    /// One-sided paired t-test on per-query deltas (candidate − baseline).
    /// H0: mean delta &gt;= 0 (no regression); H1: mean delta &lt; 0 (regression).
    /// Returns the lower-tail p-value: small p means the drop is statistically significant.
    /// </summary>
    /// <param name="deltas">Per-query deltas, candidate minus baseline. Must contain at least 2 entries.</param>
    /// <exception cref="ArgumentException">Thrown when fewer than 2 deltas are supplied (callers fall back to the legacy point comparison).</exception>
    public static PairedTTestResult OneSidedPairedTTest(IReadOnlyList<double> deltas)
    {
        if (deltas is null || deltas.Count < 2)
            throw new ArgumentException("Paired t-test requires at least 2 deltas.", nameof(deltas));

        int n = deltas.Count;
        double df = n - 1;

        // Degenerate case: zero variance (all deltas identical — checked bitwise so the
        // mean's floating-point roundoff cannot manufacture a spurious tiny sd). The t
        // statistic is +/-infinity (or 0 when the common delta is exactly 0), and the
        // one-sided p-value is decided by the sign of the mean.
        bool allEqual = true;
        for (int i = 1; i < n && allEqual; i++) allEqual = deltas[i] == deltas[0];
        if (allEqual)
        {
            double common = deltas[0];
            if (common < 0.0) return new PairedTTestResult(n, common, 0.0, double.NegativeInfinity, df, 0.0);
            if (common > 0.0) return new PairedTTestResult(n, common, 0.0, double.PositiveInfinity, df, 1.0);
            return new PairedTTestResult(n, 0.0, 0.0, 0.0, df, 1.0);
        }

        double mean = 0.0;
        for (int i = 0; i < n; i++) mean += deltas[i];
        mean /= n;

        double sumSq = 0.0;
        for (int i = 0; i < n; i++)
        {
            double d = deltas[i] - mean;
            sumSq += d * d;
        }
        double sd = Math.Sqrt(sumSq / (n - 1));

        if (sd == 0.0)
        {
            // Non-identical deltas can still collapse to zero variance at double
            // precision; resolve by the sign of the mean as above.
            if (mean < 0.0) return new PairedTTestResult(n, mean, 0.0, double.NegativeInfinity, df, 0.0);
            if (mean > 0.0) return new PairedTTestResult(n, mean, 0.0, double.PositiveInfinity, df, 1.0);
            return new PairedTTestResult(n, 0.0, 0.0, 0.0, df, 1.0);
        }

        double t = mean / (sd / Math.Sqrt(n));
        double p = StudentTCdf(t, df);
        return new PairedTTestResult(n, mean, sd, t, df, p);
    }

    /// <summary>
    /// Cumulative distribution function of Student's t distribution: P(T &lt;= t) with
    /// <paramref name="df"/> degrees of freedom, computed via the regularized incomplete
    /// beta function I_x(df/2, 1/2) with x = df/(df + t^2).
    /// </summary>
    public static double StudentTCdf(double t, double df)
    {
        if (df <= 0) throw new ArgumentOutOfRangeException(nameof(df), "Degrees of freedom must be positive.");
        if (double.IsNegativeInfinity(t)) return 0.0;
        if (double.IsPositiveInfinity(t)) return 1.0;

        double x = df / (df + t * t);
        double ix = RegularizedIncompleteBeta(df / 2.0, 0.5, x);
        return t <= 0 ? ix / 2.0 : 1.0 - ix / 2.0;
    }

    /// <summary>
    /// Regularized incomplete beta function I_x(a, b), computed with the standard
    /// Numerical-Recipes approach: log-gamma prefactor plus a modified Lentz continued
    /// fraction, using the symmetry I_x(a,b) = 1 − I_{1−x}(b,a) for fast convergence.
    /// </summary>
    internal static double RegularizedIncompleteBeta(double a, double b, double x)
    {
        if (x <= 0.0) return 0.0;
        if (x >= 1.0) return 1.0;

        double lnPrefactor = LogGamma(a + b) - LogGamma(a) - LogGamma(b)
            + a * Math.Log(x) + b * Math.Log(1.0 - x);
        double prefactor = Math.Exp(lnPrefactor);

        if (x < (a + 1.0) / (a + b + 2.0))
            return prefactor * BetaContinuedFraction(a, b, x) / a;
        return 1.0 - prefactor * BetaContinuedFraction(b, a, 1.0 - x) / b;
    }

    /// <summary>
    /// Modified Lentz continued-fraction evaluation for the incomplete beta function
    /// (the classic "betacf" routine).
    /// </summary>
    private static double BetaContinuedFraction(double a, double b, double x)
    {
        const int MaxIterations = 300;
        const double Epsilon = 3.0e-14;
        const double TinyValue = 1.0e-30;

        double qab = a + b;
        double qap = a + 1.0;
        double qam = a - 1.0;
        double c = 1.0;
        double d = 1.0 - qab * x / qap;
        if (Math.Abs(d) < TinyValue) d = TinyValue;
        d = 1.0 / d;
        double h = d;

        for (int m = 1; m <= MaxIterations; m++)
        {
            int m2 = 2 * m;
            double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < TinyValue) d = TinyValue;
            c = 1.0 + aa / c;
            if (Math.Abs(c) < TinyValue) c = TinyValue;
            d = 1.0 / d;
            h *= d * c;

            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < TinyValue) d = TinyValue;
            c = 1.0 + aa / c;
            if (Math.Abs(c) < TinyValue) c = TinyValue;
            d = 1.0 / d;
            double del = d * c;
            h *= del;
            if (Math.Abs(del - 1.0) < Epsilon) break;
        }

        return h;
    }

    /// <summary>
    /// Natural log of the gamma function, Lanczos approximation (g = 7, 9 coefficients).
    /// </summary>
    internal static double LogGamma(double x)
    {
        // Lanczos g=7, n=9 coefficients.
        ReadOnlySpan<double> coefficients =
        [
            0.99999999999980993,
            676.5203681218851,
            -1259.1392167224028,
            771.32342877765313,
            -176.61502916214059,
            12.507343278686905,
            -0.13857109526572012,
            9.9843695780195716e-6,
            1.5056327351493116e-7
        ];

        if (x < 0.5)
        {
            // Reflection formula: Γ(x)Γ(1−x) = π / sin(πx).
            return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1.0 - x);
        }

        x -= 1.0;
        double sum = coefficients[0];
        for (int i = 1; i < coefficients.Length; i++)
            sum += coefficients[i] / (x + i);

        double t = x + 7.5; // g + 0.5
        return 0.5 * Math.Log(2.0 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(sum);
    }

    /// <summary>
    /// Holm-Bonferroni step-down correction. Sorts the p-values ascending, rejects
    /// p_(i) while p_(i) &lt;= alpha/(m−i) (0-based i), and stops all later rejections at
    /// the first failure. Returns rejection flags in the ORIGINAL input order.
    /// </summary>
    public static bool[] HolmBonferroni(IReadOnlyList<double> pValues, double alpha)
    {
        int m = pValues?.Count ?? 0;
        var rejected = new bool[m];
        if (m == 0) return rejected;

        var order = Enumerable.Range(0, m).OrderBy(i => pValues![i]).ToArray();
        for (int i = 0; i < m; i++)
        {
            double threshold = alpha / (m - i);
            if (pValues![order[i]] <= threshold)
                rejected[order[i]] = true;
            else
                break; // step-down: first failure blocks all larger p-values.
        }
        return rejected;
    }
}
