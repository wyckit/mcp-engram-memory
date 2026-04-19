using System.Diagnostics;
using System.Text;

namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// Drives the Claude Code CLI (`claude -p`) as a text-generation endpoint so benchmark runs
/// charge against the user's Claude subscription instead of the Anthropic API.
///
/// Requires the `claude` CLI to be on PATH. Prompts are piped via stdin to bypass shell
/// argument-length limits (MRCR prompts can exceed 100K characters).
/// </summary>
public sealed class ClaudeCliModelClient : IAgentOutcomeModelClient
{
    public const string DefaultExecutable = "claude";

    private readonly string _executable;
    private readonly TimeSpan _timeout;

    public ClaudeCliModelClient(string? executable = null, TimeSpan? timeout = null)
    {
        _executable = string.IsNullOrWhiteSpace(executable) ? DefaultExecutable : executable;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    public Task<bool> IsAvailableAsync(string model, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(_executable, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null) return Task.FromResult(false);

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return Task.FromResult(false);
            }

            return Task.FromResult(process.ExitCode == 0);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public async Task<string?> GenerateAsync(
        string model,
        string prompt,
        int maxTokens = 320,
        float temperature = 0.1f,
        CancellationToken ct = default)
    {
        var args = new List<string> { "-p", "--model", model };

        var psi = new ProcessStartInfo(_executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException($"Failed to start '{_executable}'. Is the Claude Code CLI installed and on PATH?");

        await process.StandardInput.WriteAsync(prompt.AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
        process.StandardInput.Close();

        var stdoutBuffer = new StringBuilder();
        var stderrBuffer = new StringBuilder();
        var stdoutTask = DrainAsync(process.StandardOutput, stdoutBuffer, ct);
        var stderrTask = DrainAsync(process.StandardError, stderrBuffer, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"claude CLI exceeded {_timeout.TotalSeconds:F0}s timeout.");
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        if (process.ExitCode != 0)
        {
            var err = stderrBuffer.ToString().Trim();
            throw new InvalidOperationException(
                $"claude CLI exited with code {process.ExitCode}. stderr: {(err.Length > 0 ? err : "(empty)")}");
        }

        return stdoutBuffer.ToString().Trim();
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder buffer, CancellationToken ct)
    {
        char[] chunk = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(chunk, ct)) > 0)
            buffer.Append(chunk, 0, read);
    }

    public void Dispose() { }
}
