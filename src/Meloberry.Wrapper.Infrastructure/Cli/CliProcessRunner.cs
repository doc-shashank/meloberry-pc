using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Meloberry.Wrapper.Infrastructure.Cli;

internal static class CliProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string exePath,
        IReadOnlyList<string> args,
        string? workingDir,
        CancellationToken ct,
        TimeSpan? timeout = null,
        string? stdinText = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(workingDir) && Directory.Exists(workingDir))
        {
            psi.WorkingDirectory = workingDir;
        }

        psi.Environment["MBY_AGENT"] = "1";
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start())
            {
                return (-1, string.Empty, $"Failed to start '{exePath}'.");
            }

            if (stdinText is not null)
            {
                await proc.StandardInput.WriteAsync(stdinText.AsMemory(), ct).ConfigureAwait(false);
                await proc.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            }

            proc.StandardInput.Close();
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, $"Failed to start '{exePath}': {ex.Message}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);

        try
        {
            var outTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return (proc.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best-effort process tree termination.
            }

            var reason = ct.IsCancellationRequested
                ? "CLI command was cancelled."
                : $"CLI command timed out after {(int)effectiveTimeout.TotalSeconds}s.";
            return (-1, string.Empty, reason);
        }
    }

    public static string ExtractErrorMessage(string stdout, string stderr)
    {
        var raw = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Unknown CLI error.";
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var code = err.TryGetProperty("code", out var c) ? c.GetString() : null;
                var desc = err.TryGetProperty("description", out var d) ? d.GetString() : null;
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    return !string.IsNullOrWhiteSpace(code) ? $"[{code}] {desc}" : desc;
                }
            }
        }
        catch
        {
            // Fall back to raw text.
        }

        return raw;
    }
}
