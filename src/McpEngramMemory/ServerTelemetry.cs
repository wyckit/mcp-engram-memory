using System.ComponentModel;
using System.Runtime.InteropServices;

namespace McpEngramMemory;

/// <summary>Executable policy. Must run before any native runtime or worker starts.</summary>
internal static class ServerTelemetry
{
    internal static void Configure()
    {
        SetProcessVariable("ORT_DISABLE_TELEMETRY", "1");
        if (OperatingSystem.IsWindows())
        {
            // ORT 1.29's Windows ETW provider ignores ORT_DISABLE_TELEMETRY. Its
            // private suppression signal skips provider registration entirely,
            // including ProcessInfo emitted before DisableTelemetryEvents is callable.
            // This compatibility workaround is version-guarded in ServerNativeRuntime
            // and tested with an active ETW collector. See docs/telemetry.md.
            SetProcessVariable("ORT_RUNNING_UNIT_TESTS", "1");
        }
    }

    private static void SetProcessVariable(string name, string value)
    {
        // On Unix, Environment.SetEnvironmentVariable only changes .NET's managed
        // cache. ONNX reads libc getenv, so set both before loading native code.
        if (!OperatingSystem.IsWindows() && SetEnv(name, value, 1) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot disable ONNX telemetry; startup stopped.");
        Environment.SetEnvironmentVariable(name, value);
    }

    [DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
    private static extern int SetEnv(string name, string value, int overwrite);
}
