namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// Resolves a CLI executable name on Windows by probing PATH for common shim
/// extensions (.exe, .cmd, .bat). npm-installed CLIs like `codex` and `gemini` ship
/// extensionless bash shims plus a sibling `.cmd` shim;
/// <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>
/// on Windows will not automatically apply PATHEXT to an extensionless name, so we
/// have to find the .cmd ourselves.
///
/// On non-Windows platforms the input is returned unchanged.
/// </summary>
internal static class CliExecutableResolver
{
    public static string Resolve(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return executable;
        if (!OperatingSystem.IsWindows()) return executable;
        if (Path.HasExtension(executable) && File.Exists(executable)) return executable;
        if (Path.IsPathRooted(executable) && File.Exists(executable)) return executable;
        if (Path.HasExtension(executable)) return executable;

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv)) return executable;

        string[] extensions = { ".exe", ".cmd", ".bat" };
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim(), executable + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return executable;
    }
}
