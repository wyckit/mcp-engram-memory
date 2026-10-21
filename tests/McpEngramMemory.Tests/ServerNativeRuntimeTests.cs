using System.Runtime.InteropServices;
using McpEngramMemory;

namespace McpEngramMemory.Tests;

public sealed class ServerNativeRuntimeTests
{
    [Fact]
    public void WindowsTelemetryWorkaround_RequiresTheReviewedDependency()
    {
        ServerNativeRuntime.RequireReviewedTelemetryVersion(typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly.GetName().Version);
        Assert.Throws<NotSupportedException>(() => ServerNativeRuntime.RequireReviewedTelemetryVersion(new Version(1, 30, 0, 0)));
        Assert.Throws<NotSupportedException>(() => ServerNativeRuntime.RequireReviewedTelemetryVersion(null));
    }

    [Theory]
    [InlineData(@"C:\tools\onnxruntime.dll", @"\\?\C:\tools\onnxruntime.dll")]
    [InlineData(@"\\server\share\onnxruntime.dll", @"\\?\UNC\server\share\onnxruntime.dll")]
    [InlineData(@"\\?\C:\tools\onnxruntime.dll", @"\\?\C:\tools\onnxruntime.dll")]
    public void ExtendedPath_PreservesLocalAndUncPaths(string path, string expected) =>
        Assert.Equal(expected, ServerNativeRuntime.ExtendedWindowsPath(path));

    [Fact]
    public void MissingNativeRuntime_DoesNotFallBackToSystemSearch()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Throws<FileNotFoundException>(() => ServerNativeRuntime.ResolveWindowsLibrary(root, Architecture.X64));
        Assert.Throws<PlatformNotSupportedException>(() => ServerNativeRuntime.ResolveWindowsLibrary(root, Architecture.X86));
    }
}
