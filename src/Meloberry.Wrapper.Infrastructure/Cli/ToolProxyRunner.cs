using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;

namespace Meloberry.Wrapper.Infrastructure.Cli;

/// <summary>
/// Executes central `mby` and `melomg` binaries from `%LOCALAPPDATA%\Meloberry\apps\` inside the caller's CWD,
/// streaming stdio transparently and notifying any running GUI on activity or `mby build` completion.
/// </summary>
public sealed class ToolProxyRunner(
    IManagedAppsStore appsStore,
    ISettingsStore settingsStore,
    LiveGuiIpcClient liveGui)
{
    public async Task<int> RunAsync(
        string toolKey,
        string? workspaceDir,
        IReadOnlyList<string> args,
        IWrapperConsole console,
        bool jsonMode,
        CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var overridePath = string.Equals(toolKey, "melomg", StringComparison.OrdinalIgnoreCase)
            ? settings.MelomgExecutableOverride
            : settings.MbyExecutableOverride;
        var effectiveWs = !string.IsNullOrWhiteSpace(workspaceDir)
            ? workspaceDir
            : settings.ActiveWorkspace?.WorkspacePath;

        var exePath = await appsStore.ResolveToolExecutableAsync(toolKey, effectiveWs, overridePath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(exePath))
        {
            await WriteNotFoundAsync(console, jsonMode, toolKey).ConfigureAwait(false);
            return 3;
        }

        var subVerb = args.FirstOrDefault(a => !a.StartsWith('-'));
        var activityId = $"tool_{Guid.NewGuid():N}"[..14];
        var cmdLabel = string.IsNullOrWhiteSpace(subVerb) ? toolKey : $"{toolKey} {subVerb}";
        var detailSummary = args.Count > 0 ? string.Join(' ', args.Take(6)) : null;
        var sw = Stopwatch.StartNew();
        if (liveGui.IsGuiAlive())
        {
            await liveGui.TryPostUiAsync(
                "activity",
                message: $"Running {cmdLabel}",
                level: "info",
                id: activityId,
                state: "running",
                detail: detailSummary,
                ct: ct).ConfigureAwait(false);
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = console.IsInputRedirected,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
        {
            sw.Stop();
            if (liveGui.IsGuiAlive())
            {
                await liveGui.TryPostUiAsync(
                    "activity",
                    message: $"Failed to start {cmdLabel}",
                    level: "err",
                    id: activityId,
                    state: "error",
                    detail: exePath,
                    exitCode: 1,
                    durationMs: sw.ElapsedMilliseconds,
                    ct: ct).ConfigureAwait(false);
            }
            await console.Error.WriteLineAsync($"Failed to launch '{exePath}'.").ConfigureAwait(false);
            return 1;
        }

        var stdinTask = Task.CompletedTask;
        if (console.IsInputRedirected)
        {
            stdinTask = Task.Run(async () =>
            {
                var inputText = await console.In.ReadToEndAsync(ct).ConfigureAwait(false);
                await proc.StandardInput.WriteAsync(inputText.AsMemory(), ct).ConfigureAwait(false);
                await proc.StandardInput.FlushAsync(ct).ConfigureAwait(false);
                proc.StandardInput.Close();
            }, ct);
        }

        var teedStdout = new StringBuilder();
        var stdoutTask = PumpStreamAsync(proc.StandardOutput, console.Out, teedStdout, ct);
        var stderrTask = PumpStreamAsync(proc.StandardError, console.Error, null, ct);

        await Task.WhenAll(stdinTask, stdoutTask, stderrTask).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        sw.Stop();

        if (liveGui.IsGuiAlive())
        {
            var succeeded = proc.ExitCode == 0;
            await liveGui.TryPostUiAsync(
                "activity",
                message: succeeded ? $"Completed {cmdLabel}" : $"Failed {cmdLabel}",
                level: succeeded ? "info" : "err",
                id: activityId,
                state: succeeded ? "done" : "error",
                detail: detailSummary,
                exitCode: proc.ExitCode,
                durationMs: sw.ElapsedMilliseconds,
                ct: ct).ConfigureAwait(false);
        }

        if (proc.ExitCode == 0 &&
            string.Equals(toolKey, "mby", StringComparison.OrdinalIgnoreCase) &&
            liveGui.IsGuiAlive())
        {
            var builtWebDir = TryResolveBuiltWebAppDirectory(args, teedStdout.ToString(), Environment.CurrentDirectory);
            if (!string.IsNullOrWhiteSpace(builtWebDir) && Directory.Exists(builtWebDir))
            {
                await liveGui.TryPostUiAsync("viewer-open", path: builtWebDir, ct: ct).ConfigureAwait(false);
            }
        }

        return proc.ExitCode;
    }

    public static string? TryResolveBuiltWebAppDirectory(IReadOnlyList<string> args, string stdOut, string baseDir)
    {
        var verb = args.FirstOrDefault(a => !a.StartsWith('-'));
        var isBuildCommand = string.Equals(verb, "build", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(verb, "apply", StringComparison.OrdinalIgnoreCase) &&
             args.Any(a => string.Equals(a, "--build", StringComparison.OrdinalIgnoreCase)));
        if (!isBuildCommand)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(stdOut))
        {
            try
            {
                using var doc = JsonDocument.Parse(stdOut);
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("outputDirectory", out var outEl) &&
                    outEl.ValueKind == JsonValueKind.String)
                {
                    var rawOut = outEl.GetString();
                    if (!string.IsNullOrWhiteSpace(rawOut))
                    {
                        return Path.IsPathRooted(rawOut)
                            ? Path.GetFullPath(rawOut)
                            : Path.GetFullPath(rawOut, baseDir);
                    }
                }
            }
            catch
            {
                // Non-JSON stdout falls through to -o / --build argument inspection.
            }
        }

        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] is "-o" or "--out" or "--build")
            {
                var raw = args[i + 1];
                if (!string.IsNullOrWhiteSpace(raw) && !raw.StartsWith('-'))
                {
                    return Path.IsPathRooted(raw) ? Path.GetFullPath(raw) : Path.GetFullPath(raw, baseDir);
                }
            }
        }

        return null;
    }

    private static async Task PumpStreamAsync(StreamReader reader, TextWriter writer, StringBuilder? tee, CancellationToken ct)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).WaitAsync(ct).ConfigureAwait(false)) > 0)
        {
            await writer.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
            if (tee is not null && tee.Length < 256 * 1024)
            {
                tee.Append(buffer, 0, read);
            }
        }
    }

    private static async Task WriteNotFoundAsync(IWrapperConsole console, bool jsonMode, string toolKey)
    {
        if (jsonMode)
        {
            var errJson = JsonSerializer.Serialize(new
            {
                success = false,
                error = new
                {
                    code = "Cli.NotFound",
                    description = $"Executable for '{toolKey}' was not found in central apps\\ store. Install it in Meloberry PC Setup.",
                    type = "NotFound"
                }
            }, Server.HttpEnvelope.JsonOptions);
            await console.Error.WriteLineAsync(errJson).ConfigureAwait(false);
        }
        else
        {
            await console.Error.WriteLineAsync($"Error [Cli.NotFound]: Executable for '{toolKey}' was not found. Install it in Meloberry PC Setup.").ConfigureAwait(false);
        }
    }
}
