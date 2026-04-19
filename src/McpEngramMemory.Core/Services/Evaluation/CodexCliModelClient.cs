using System.Diagnostics;

namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// Drives the OpenAI Codex CLI (`codex exec`) so MRCR runs charge against the user's
/// ChatGPT subscription instead of the OpenAI API. Prompts are piped over stdin and the
/// clean final message is captured via <c>-o &lt;tempfile&gt;</c> to avoid parsing the
/// CLI's header+footer metadata.
///
/// Requires the `codex` CLI (npm package `@openai/codex-cli`) on PATH. ChatGPT-subscription
/// accounts only support `gpt-5.4` and `gpt-5.4-mini`; `gpt-5.4-codex` and `o3` require API
/// access.
/// </summary>
public sealed class CodexCliModelClient : IAgentOutcomeModelClient
{
    public const string DefaultExecutable = "codex";

    private readonly string _executable;
    private readonly TimeSpan _timeout;

    public CodexCliModelClient(string? executable = null, TimeSpan? timeout = null)
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
        string outFile = Path.Combine(Path.GetTempPath(), $"codex_mrcr_{Guid.NewGuid():N}.txt");

        try
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
            psi.ArgumentList.Add("exec");
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model);
            psi.ArgumentList.Add("--sandbox");
            psi.ArgumentList.Add("read-only");
            psi.ArgumentList.Add("--skip-git-repo-check");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outFile);

            using var process = Process.Start(psi);
            if (process is null)
                throw new InvalidOperationException($"Failed to start '{_executable}'. Is the Codex CLI installed and on PATH?");

            await process.StandardInput.WriteAsync(prompt.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            process.StandardInput.Close();

            // Drain stdout/stderr so the process isn't blocked on full buffers, but we
            // don't actually need their contents — the clean answer lands in outFile.
            var drainOut = DrainAsync(process.StandardOutput, ct);
            var drainErr = DrainAsync(process.StandardError, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"codex CLI exceeded {_timeout.TotalSeconds:F0}s timeout.");
            }

            await Task.WhenAll(drainOut, drainErr);

            if (process.ExitCode != 0)
            {
                var err = (await drainErr).Trim();
                throw new InvalidOperationException(
                    $"codex CLI exited with code {process.ExitCode}. stderr: {(err.Length > 0 ? err : "(empty)")}");
            }

            return File.Exists(outFile) ? (await File.ReadAllTextAsync(outFile, ct)).Trim() : null;
        }
        finally
        {
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        char[] chunk = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(chunk, ct)) > 0)
            sb.Append(chunk, 0, read);
        return sb.ToString();
    }

    public void Dispose() { }
}
