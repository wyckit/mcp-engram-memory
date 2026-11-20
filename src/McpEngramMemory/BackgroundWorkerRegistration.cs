using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory;

/// <summary>
/// Profile-gated and environment-configurable registration for Engram background workers.
///
/// Profile defaults (when <c>MEMORY_BACKGROUND_WORKERS=auto</c> or unset):
/// <list type="bullet">
///   <item><c>minimal</c>: <c>embedding_warmup</c> and <c>decay</c> only (no accretion, diffusion warmup, consolidation, or auto-link).</item>
///   <item><c>standard</c> / <c>full</c>: all 6 workers, with <c>DiffusionKernelWarmupService.WarmOnlyLoadedPartitions = true</c> unless <c>MEMORY_DIFFUSION_WARMUP_EAGER_LOAD=1</c> is set.</item>
/// </list>
/// </summary>
internal static class BackgroundWorkerRegistration
{
    internal const string WorkerEmbeddingWarmup = "embedding_warmup";
    internal const string WorkerDecay = "decay";
    internal const string WorkerAccretion = "accretion";
    internal const string WorkerDiffusionWarmup = "diffusion_warmup";
    internal const string WorkerConsolidation = "consolidation";
    internal const string WorkerAutoLink = "auto_link";

    internal static readonly IReadOnlyList<string> AllWorkerNames =
    [
        WorkerEmbeddingWarmup,
        WorkerDecay,
        WorkerAccretion,
        WorkerDiffusionWarmup,
        WorkerConsolidation,
        WorkerAutoLink
    ];

    private static readonly Dictionary<string, string> WorkerNameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["embedding_warmup"] = WorkerEmbeddingWarmup,
        ["embedding-warmup"] = WorkerEmbeddingWarmup,
        ["embedding"] = WorkerEmbeddingWarmup,
        ["decay"] = WorkerDecay,
        ["accretion"] = WorkerAccretion,
        ["diffusion_warmup"] = WorkerDiffusionWarmup,
        ["diffusion-warmup"] = WorkerDiffusionWarmup,
        ["diffusion"] = WorkerDiffusionWarmup,
        ["consolidation"] = WorkerConsolidation,
        ["auto_link"] = WorkerAutoLink,
        ["auto-link"] = WorkerAutoLink,
        ["autolink"] = WorkerAutoLink
    };

    private static readonly Dictionary<string, string> WorkerEnableEnvVars = new(StringComparer.Ordinal)
    {
        [WorkerEmbeddingWarmup] = "MEMORY_ENABLE_EMBEDDING_WARMUP",
        [WorkerDecay] = "MEMORY_ENABLE_DECAY",
        [WorkerAccretion] = "MEMORY_ENABLE_ACCRETION",
        [WorkerDiffusionWarmup] = "MEMORY_ENABLE_DIFFUSION_WARMUP",
        [WorkerConsolidation] = "MEMORY_ENABLE_CONSOLIDATION",
        [WorkerAutoLink] = "MEMORY_ENABLE_AUTOLINK"
    };

    internal sealed record BackgroundWorkerOptions(
        IReadOnlySet<string> EnabledWorkers,
        TimeSpan DecayInterval,
        TimeSpan AccretionInterval,
        TimeSpan ConsolidationDelay,
        TimeSpan ConsolidationInterval,
        TimeSpan AutoLinkDelay,
        TimeSpan AutoLinkInterval,
        TimeSpan DiffusionWarmupDelay,
        TimeSpan DiffusionWarmupInterval,
        bool DiffusionWarmupEagerLoad,
        IReadOnlyList<string> Warnings);

    internal static BackgroundWorkerOptions ResolveOptions(
        string normalizedProfile,
        Func<string, string?> getEnv)
    {
        var warnings = new List<string>();
        var enabled = ResolveEnabledWorkers(normalizedProfile, getEnv, warnings);

        var decayInterval = ParsePositiveTimeSpan(
            getEnv, "MEMORY_DECAY_INTERVAL_MINUTES", TimeSpan.FromMinutes, DecayBackgroundService.DefaultInterval, warnings);
        var accretionInterval = ParsePositiveTimeSpan(
            getEnv, "MEMORY_ACCRETION_INTERVAL_MINUTES", TimeSpan.FromMinutes, AccretionBackgroundService.DefaultInterval, warnings);
        var consolidationDelay = ParseNonNegativeTimeSpan(
            getEnv, "MEMORY_CONSOLIDATION_DELAY_MINUTES", TimeSpan.FromMinutes, ConsolidationBackgroundService.DefaultStartupDelay, warnings);
        var consolidationInterval = ParsePositiveTimeSpan(
            getEnv, "MEMORY_CONSOLIDATION_INTERVAL_HOURS", TimeSpan.FromHours, ConsolidationBackgroundService.DefaultInterval, warnings);
        var autoLinkDelay = ParseNonNegativeTimeSpan(
            getEnv, "MEMORY_AUTOLINK_DELAY_MINUTES", TimeSpan.FromMinutes, AutoLinkBackgroundService.DefaultStartupDelay, warnings);
        var autoLinkInterval = ParsePositiveTimeSpan(
            getEnv, "MEMORY_AUTOLINK_INTERVAL_HOURS", TimeSpan.FromHours, AutoLinkBackgroundService.DefaultInterval, warnings);
        var diffusionDelay = ParseNonNegativeTimeSpan(
            getEnv, "MEMORY_DIFFUSION_WARMUP_DELAY_SECONDS", TimeSpan.FromSeconds, DiffusionKernelWarmupService.DefaultStartupDelay, warnings);
        var diffusionInterval = ParsePositiveTimeSpan(
            getEnv, "MEMORY_DIFFUSION_WARMUP_INTERVAL_MINUTES", TimeSpan.FromMinutes, DiffusionKernelWarmupService.DefaultRefreshInterval, warnings);
        bool diffusionEagerLoad = ParseBool(
            getEnv("MEMORY_DIFFUSION_WARMUP_EAGER_LOAD"), "MEMORY_DIFFUSION_WARMUP_EAGER_LOAD", defaultValue: false, warnings);

        return new BackgroundWorkerOptions(
            enabled,
            decayInterval,
            accretionInterval,
            consolidationDelay,
            consolidationInterval,
            autoLinkDelay,
            autoLinkInterval,
            diffusionDelay,
            diffusionInterval,
            diffusionEagerLoad,
            warnings);
    }

    internal static IServiceCollection AddConfiguredBackgroundWorkers(
        this IServiceCollection services,
        string normalizedProfile,
        Func<string, string?> getEnv,
        TextWriter? warningWriter = null)
    {
        var options = ResolveOptions(normalizedProfile, getEnv);
        foreach (var warning in options.Warnings)
            warningWriter?.WriteLine(warning);

        if (options.EnabledWorkers.Contains(WorkerEmbeddingWarmup))
            services.AddHostedService<EmbeddingWarmupService>();

        if (options.EnabledWorkers.Contains(WorkerDecay))
        {
            services.AddHostedService(sp => new DecayBackgroundService(
                sp.GetRequiredService<LifecycleEngine>(),
                sp.GetRequiredService<ILogger<DecayBackgroundService>>(),
                sp.GetService<IBackgroundWorkerStatusTracker>())
            {
                Interval = options.DecayInterval
            });
        }

        if (options.EnabledWorkers.Contains(WorkerAccretion))
        {
            services.AddHostedService(sp => new AccretionBackgroundService(
                sp.GetRequiredService<AccretionScanner>(),
                sp.GetRequiredService<CognitiveIndex>(),
                sp.GetRequiredService<ClusterManager>(),
                sp.GetRequiredService<IEmbeddingService>(),
                sp.GetRequiredService<ILogger<AccretionBackgroundService>>(),
                sp.GetService<IBackgroundWorkerStatusTracker>())
            {
                Interval = options.AccretionInterval
            });
        }

        if (options.EnabledWorkers.Contains(WorkerDiffusionWarmup))
        {
            services.AddHostedService(sp => new DiffusionKernelWarmupService(
                sp.GetRequiredService<MemoryDiffusionKernel>(),
                sp.GetRequiredService<CognitiveIndex>(),
                sp.GetService<ILogger<DiffusionKernelWarmupService>>())
            {
                StartupDelay = options.DiffusionWarmupDelay,
                RefreshInterval = options.DiffusionWarmupInterval,
                WarmOnlyLoadedPartitions = !options.DiffusionWarmupEagerLoad
            });
        }

        if (options.EnabledWorkers.Contains(WorkerConsolidation))
        {
            services.AddHostedService(sp => new ConsolidationBackgroundService(
                sp.GetRequiredService<LifecycleEngine>(),
                sp.GetService<ILogger<ConsolidationBackgroundService>>(),
                sp.GetService<IBackgroundWorkerStatusTracker>())
            {
                StartupDelay = options.ConsolidationDelay,
                Interval = options.ConsolidationInterval
            });
        }

        if (options.EnabledWorkers.Contains(WorkerAutoLink))
        {
            services.AddHostedService(sp => new AutoLinkBackgroundService(
                sp.GetRequiredService<AutoLinkScanner>(),
                sp.GetRequiredService<CognitiveIndex>(),
                sp.GetRequiredService<LifecycleEngine>(),
                sp.GetService<ILogger<AutoLinkBackgroundService>>(),
                sp.GetService<IBackgroundWorkerStatusTracker>())
            {
                StartupDelay = options.AutoLinkDelay,
                Interval = options.AutoLinkInterval
            });
        }

        return services;
    }

    internal static int? ParseOptionalPositiveInt(
        Func<string, string?> getEnv,
        string envVar,
        TextWriter? warningWriter = null)
    {
        var raw = getEnv(envVar);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (int.TryParse(raw.Trim(), out int parsed) && parsed > 0)
            return parsed;

        warningWriter?.WriteLine(
            $"Warning: Ignoring invalid {envVar}='{raw.Trim()}' (expected a positive integer). Using default.");
        return null;
    }

    internal static TimeSpan ParseAuditReadFlushInterval(
        Func<string, string?> getEnv,
        TextWriter? warningWriter = null)
    {
        const int DefaultAuditReadFlushMs = 500;
        var raw = getEnv("MEMORY_AUDIT_READ_FLUSH_MS");
        if (string.IsNullOrWhiteSpace(raw))
            return TimeSpan.FromMilliseconds(DefaultAuditReadFlushMs);

        if (int.TryParse(raw.Trim(), out int ms) && ms >= 0)
            return TimeSpan.FromMilliseconds(ms);

        warningWriter?.WriteLine(
            $"Warning: Ignoring invalid MEMORY_AUDIT_READ_FLUSH_MS='{raw.Trim()}' (expected integer >= 0). Using default {DefaultAuditReadFlushMs}ms.");
        return TimeSpan.FromMilliseconds(DefaultAuditReadFlushMs);
    }

    private static HashSet<string> ResolveEnabledWorkers(
        string normalizedProfile,
        Func<string, string?> getEnv,
        List<string> warnings)
    {
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        var mode = getEnv("MEMORY_BACKGROUND_WORKERS")?.Trim();

        if (string.IsNullOrEmpty(mode) || mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (normalizedProfile is "standard" or "full")
            {
                foreach (var w in AllWorkerNames) enabled.Add(w);
            }
            else
            {
                enabled.Add(WorkerEmbeddingWarmup);
                enabled.Add(WorkerDecay);
            }
        }
        else if (mode.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var w in AllWorkerNames) enabled.Add(w);
        }
        else if (mode.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            // Explicitly empty.
        }
        else
        {
            foreach (var token in mode.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (WorkerNameAliases.TryGetValue(token, out var canonical))
                    enabled.Add(canonical);
                else
                    warnings.Add(
                        $"Warning: Unrecognized worker '{token}' in MEMORY_BACKGROUND_WORKERS. Valid names: {string.Join(", ", AllWorkerNames)}.");
            }
        }

        // Per-worker overrides win over the base mode.
        foreach (var (worker, envVar) in WorkerEnableEnvVars)
        {
            var raw = getEnv(envVar);
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            bool flag = ParseBool(raw, envVar, defaultValue: enabled.Contains(worker), warnings);
            if (flag) enabled.Add(worker);
            else enabled.Remove(worker);
        }

        return enabled;
    }

    private static bool ParseBool(string? raw, string envVar, bool defaultValue, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        var trimmed = raw.Trim();
        if (trimmed is "1" || trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase))
            return true;
        if (trimmed is "0" || trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("no", StringComparison.OrdinalIgnoreCase))
            return false;

        warnings.Add($"Warning: Ignoring invalid {envVar}='{trimmed}' (expected true/false or 1/0). Using default {defaultValue}.");
        return defaultValue;
    }

    // Safely below Task.Delay / Timer's 32-bit signed millisecond ceiling (~24.85 days) so huge
    // env var values never throw OverflowException or ArgumentOutOfRangeException at startup.
    private static readonly TimeSpan MaxScheduleTimeSpan = TimeSpan.FromDays(24);

    private static TimeSpan ParsePositiveTimeSpan(
        Func<string, string?> getEnv,
        string envVar,
        Func<double, TimeSpan> unitFactory,
        TimeSpan defaultValue,
        List<string> warnings)
    {
        var raw = getEnv(envVar);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (double.TryParse(raw.Trim(), System.Globalization.CultureInfo.InvariantCulture, out double value)
            && value > 0
            && double.IsFinite(value))
        {
            try
            {
                var parsed = unitFactory(value);
                if (parsed > TimeSpan.Zero && parsed <= MaxScheduleTimeSpan)
                    return parsed;
            }
            catch (OverflowException)
            {
                // Handled below via warning + default fallback.
            }
        }

        warnings.Add($"Warning: Ignoring invalid {envVar}='{raw.Trim()}' (expected a positive number <= 24 days). Using default {defaultValue}.");
        return defaultValue;
    }

    private static TimeSpan ParseNonNegativeTimeSpan(
        Func<string, string?> getEnv,
        string envVar,
        Func<double, TimeSpan> unitFactory,
        TimeSpan defaultValue,
        List<string> warnings)
    {
        var raw = getEnv(envVar);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (double.TryParse(raw.Trim(), System.Globalization.CultureInfo.InvariantCulture, out double value)
            && value >= 0
            && double.IsFinite(value))
        {
            try
            {
                var parsed = unitFactory(value);
                if (parsed >= TimeSpan.Zero && parsed <= MaxScheduleTimeSpan)
                    return parsed;
            }
            catch (OverflowException)
            {
                // Handled below via warning + default fallback.
            }
        }

        warnings.Add($"Warning: Ignoring invalid {envVar}='{raw.Trim()}' (expected a non-negative number <= 24 days). Using default {defaultValue}.");
        return defaultValue;
    }
}
