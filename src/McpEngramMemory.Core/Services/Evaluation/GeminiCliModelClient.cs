using System.Diagnostics;
using System.Text;

namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// Drives the Google Gemini CLI (`gemini -p`) so MRCR runs charge against the user's
/// Gemini subscription instead of the Gemini API. Prompts are piped over stdin (with
/// <c>-p ""</c>) so 128K-token contexts don't hit shell-argument-length limits.
///
/// Requires the `gemini` CLI (npm package `@google/gemini-cli`) on PATH. Available
/// subscription models: <c>gemini-2.5-pro</c>, <c>gemini-2.5-flash</c>,
/// <c>gemini-2.5-flash-lite</c>.
/// </summary>
public sealed class GeminiCliModelClient : IAgentOutcomeModelClient
{
    public const string DefaultExecutable = "gemini";

    private readonly string _executable;
    private readonly TimeSpan _timeout;

    public GeminiCliModelClient(string? executable = null, TimeSpan? timeout = null)
    {
        var baseName = string.IsNullOrWhiteSpace(executable) ? DefaultExecutable : executable;
        _executable = CliExecutableResolver.Resolve(baseName);
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
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
        var psi = new ProcessStartInfo(_executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(model);
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(string.Empty);

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException($"Failed to start '{_executable}'. Is the Gemini CLI installed and on PATH?");

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
            throw new TimeoutException($"gemini CLI exceeded {_timeout.TotalSeconds:F0}s timeout.");
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        if (process.ExitCode != 0)
        {
            var err = stderrBuffer.ToString().Trim();
            throw new InvalidOperationException(
                $"gemini CLI exited with code {process.ExitCode}. stderr: {(err.Length > 0 ? err : "(empty)")}");
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
