using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace McpEngramMemory;

internal static class ServerNativeRuntime
{
    // ONNX's name-only Windows probe can select System32's incompatible runtime
    // when a deeply nested global-tool path exceeds LoadLibrary's path limit.
    // Bind the server to its shipped binary before any ONNX static initialization.
    internal static void Configure()
    {
        if (!OperatingSystem.IsWindows()) return;
        RequireReviewedTelemetryVersion(typeof(InferenceSession).Assembly.GetName().Version);
        var path = ResolveWindowsLibrary(AppContext.BaseDirectory, RuntimeInformation.ProcessArchitecture);
        var handle = new Lazy<IntPtr>(() => NativeLibrary.Load(ExtendedWindowsPath(path)));
        NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, (name, _, _) =>
            name == "onnxruntime" ? handle.Value : IntPtr.Zero);
        var sqlite = ResolveWindowsLibrary(AppContext.BaseDirectory, RuntimeInformation.ProcessArchitecture, "e_sqlite3.dll");
        var sqliteHandle = new Lazy<IntPtr>(() => NativeLibrary.Load(ExtendedWindowsPath(sqlite)));
        NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly, (name, _, _) =>
            name == "e_sqlite3" ? sqliteHandle.Value : IntPtr.Zero);
    }

    internal static void RequireReviewedTelemetryVersion(Version? version)
    {
        // A dependency upgrade must requalify the private Windows suppression signal
        // before shipping. Reading assembly metadata does not initialize native ORT.
        if (version != new Version(1, 29, 0, 0))
            throw new NotSupportedException("ONNX telemetry suppression requires the reviewed 1.29.0 runtime. Requalify the telemetry policy before upgrading ONNX.");
    }

    internal static string ResolveWindowsLibrary(string directory, Architecture architecture, string library = "onnxruntime.dll")
    {
        var rid = architecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException($"No packaged ONNX runtime for Windows {architecture}.")
        };
        var path = Path.Combine(directory, "runtimes", rid, "native", library);
        if (File.Exists(path)) return Path.GetFullPath(path);
        // A RID-specific publish flattens the selected native assets.
        path = Path.Combine(directory, library);
        if (File.Exists(path)) return Path.GetFullPath(path);
        throw new FileNotFoundException($"The packaged native runtime '{library}' is missing. Reinstall Engram; the server will not load a system copy.", path);
    }

    internal static string ExtendedWindowsPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
}
