using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Infrastructure.Cli;

internal static class CliProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string exePath,
        IReadOnlyList<string> args,
        string? workingDir,
        CancellationToken ct,
        TimeSpan? timeout = null)
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

/// <summary>Out-of-process runner for `mby` CLI.</summary>
public sealed class ProcessMbyCliClient : IMbyCliClient
{
    public async Task<Result<IReadOnlyList<SpecTopicItem>>> GetSpecCatalogAsync(
        string exePath,
        string? workingDir,
        CancellationToken ct = default)
    {
        var res = await CliProcessRunner.RunAsync(exePath, ["spec", "--json"], workingDir, ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            return Result<IReadOnlyList<SpecTopicItem>>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        try
        {
            using var doc = JsonDocument.Parse(res.StdOut);
            var schemas = doc.RootElement.GetProperty("data").GetProperty("schemas");
            var items = new List<SpecTopicItem>();
            foreach (var el in schemas.EnumerateArray())
            {
                var topic = GetString(el, "topic");
                items.Add(new SpecTopicItem(
                    Topic: topic,
                    Scope: GetString(el, "scope", "content"),
                    Category: GetString(el, "category", "other"),
                    Summary: GetString(el, "summary"),
                    CompactSignature: GetString(el, "compactSignature"),
                    Command: GetString(el, "command"),
                    IsVirtual: false,
                    ModeGroups: SpecCatalogDefaults.GetModeGroupsForTopic(topic)));
            }

            return Result<IReadOnlyList<SpecTopicItem>>.Success(items);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<SpecTopicItem>>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, $"Failed to parse `mby spec --json`: {ex.Message}"));
        }
    }

    public async Task<Result<IReadOnlyList<AgentsMdSpecDetail>>> GetSpecDetailsAsync(
        string exePath,
        string? workingDir,
        IReadOnlyList<string> topics,
        CancellationToken ct = default)
    {
        var args = new List<string> { "spec" };
        args.AddRange(topics.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase));
        args.Add("--json");

        var res = await CliProcessRunner.RunAsync(exePath, args, workingDir, ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            return Result<IReadOnlyList<AgentsMdSpecDetail>>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        try
        {
            using var doc = JsonDocument.Parse(res.StdOut);
            var schemas = doc.RootElement.GetProperty("data").GetProperty("schemas");
            var list = new List<AgentsMdSpecDetail>();

            foreach (var el in schemas.EnumerateArray())
            {
                var fields = new List<AgentsMdSpecField>();
                if (el.TryGetProperty("fields", out var fieldsArr) && fieldsArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in fieldsArr.EnumerateArray())
                    {
                        var allowed = f.TryGetProperty("allowedValues", out var av) && av.ValueKind == JsonValueKind.Array
                            ? av.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(s => s.Length > 0).ToList()
                            : null;

                        fields.Add(new AgentsMdSpecField(
                            Name: GetString(f, "name"),
                            Type: GetString(f, "type"),
                            Required: f.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.True,
                            Description: GetString(f, "description"),
                            AllowedValues: allowed,
                            MaxLength: GetNullableInt(f, "maxLength"),
                            Pattern: GetNullableString(f, "pattern"),
                            MinValue: GetNullableInt(f, "minValue"),
                            MaxValue: GetNullableInt(f, "maxValue")));
                    }
                }

                string? exampleJson = null;
                if (el.TryGetProperty("example", out var exEl) && exEl.ValueKind != JsonValueKind.Undefined && exEl.ValueKind != JsonValueKind.Null)
                {
                    exampleJson = JsonSerializer.Serialize(exEl, new JsonSerializerOptions { WriteIndented = true });
                }

                list.Add(new AgentsMdSpecDetail(
                    Topic: GetString(el, "topic"),
                    Scope: GetString(el, "scope", "content"),
                    Category: GetString(el, "category", "other"),
                    Summary: GetString(el, "summary"),
                    CompactSignature: GetString(el, "compactSignature"),
                    Command: GetString(el, "command"),
                    DisallowUnmappedProperties: el.TryGetProperty("disallowUnmappedProperties", out var dup) && dup.ValueKind == JsonValueKind.True,
                    Fields: fields,
                    ExampleJson: exampleJson));
            }

            return Result<IReadOnlyList<AgentsMdSpecDetail>>.Success(list);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<AgentsMdSpecDetail>>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, $"Failed to parse `mby spec` details: {ex.Message}"));
        }
    }

    public async Task<Result<IReadOnlyList<WorkspaceSummary>>> ListWorkspacesAsync(
        string exePath,
        string? workingDir,
        CancellationToken ct = default)
    {
        var res = await CliProcessRunner.RunAsync(exePath, ["ls", "--all", "--json"], workingDir, ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            return Result<IReadOnlyList<WorkspaceSummary>>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        try
        {
            using var doc = JsonDocument.Parse(res.StdOut);
            var data = doc.RootElement.GetProperty("data");
            var list = new List<WorkspaceSummary>();
            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in data.EnumerateArray())
                {
                    list.Add(new WorkspaceSummary(
                        Id: GetString(el, "id"),
                        Kind: GetString(el, "kind"),
                        SubType: GetString(el, "subType"),
                        StorageDirectory: GetString(el, "storageDirectory"),
                        ItemCount: el.TryGetProperty("itemCount", out var ic) && ic.TryGetInt32(out var count) ? count : 0));
                }
            }

            return Result<IReadOnlyList<WorkspaceSummary>>.Success(list);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<WorkspaceSummary>>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, $"Failed to parse `mby ls --all --json`: {ex.Message}"));
        }
    }

    public async Task<Result<string>> BuildWorkspaceAsync(
        string exePath,
        string workspaceId,
        string outputDir,
        string? workingDir,
        CancellationToken ct = default)
    {
        var res = await CliProcessRunner.RunAsync(exePath, ["build", workspaceId, "-o", outputDir, "--json"], workingDir, ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            return Result<string>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        try
        {
            using var doc = JsonDocument.Parse(res.StdOut);
            var data = doc.RootElement.GetProperty("data");
            var outDir = GetNullableString(data, "outputDirectory") ?? outputDir;
            var resolved = !Path.IsPathRooted(outDir) && !string.IsNullOrWhiteSpace(workingDir)
                ? Path.GetFullPath(outDir, workingDir)
                : Path.GetFullPath(outDir);
            return Result<string>.Success(resolved);
        }
        catch
        {
            var resolvedFallback = !Path.IsPathRooted(outputDir) && !string.IsNullOrWhiteSpace(workingDir)
                ? Path.GetFullPath(outputDir, workingDir)
                : Path.GetFullPath(outputDir);
            return Result<string>.Success(resolvedFallback);
        }
    }

    public async Task<Result<string>> ExportPackageAsync(
        string exePath,
        string workspaceId,
        string target,
        string outputPath,
        string? workingDir,
        CancellationToken ct = default)
    {
        var args = new List<string> { "pkg", "export", workspaceId, target };
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            args.Add(outputPath);
        }
        args.Add("--json");

        var res = await CliProcessRunner.RunAsync(exePath, args, workingDir, ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            return Result<string>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        try
        {
            using var doc = JsonDocument.Parse(res.StdOut);
            var data = doc.RootElement.GetProperty("data");
            var pkgPath = GetNullableString(data, "packagePath") ?? GetNullableString(data, "outputPath") ?? outputPath;
            return Result<string>.Success(pkgPath);
        }
        catch
        {
            return Result<string>.Success(outputPath);
        }
    }

    public async Task<Result<string>> ApplySpecJsonAsync(
        string exePath,
        string specFilePath,
        string? workingDir,
        CancellationToken ct = default)
    {
        var res = await CliProcessRunner.RunAsync(exePath, ["apply", specFilePath, "--json"], workingDir, ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            return Result<string>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        try
        {
            using var doc = JsonDocument.Parse(res.StdOut);
            var data = doc.RootElement.GetProperty("data");
            var id = GetNullableString(data, "targetId")
                ?? GetNullableString(data, "id")
                ?? GetNullableString(data, "workspaceId")
                ?? GetNullableString(data, "sessionId")
                ?? GetNullableString(data, "setId")
                ?? string.Empty;
            return Result<string>.Success(id);
        }
        catch
        {
            return Result<string>.Success(string.Empty);
        }
    }

    private static string GetString(JsonElement el, string prop, string fallback = "") =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static string? GetNullableString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetNullableInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.TryGetInt32(out var val) ? val : null;
}

/// <summary>Out-of-process runner for `melomg` CLI.</summary>
public sealed class ProcessMelomgCliClient : IMelomgCliClient
{
    public async Task<Result<string>> GetSpecTextAsync(
        string exePath,
        string? workingDir,
        IReadOnlyList<string> topics,
        CancellationToken ct = default)
    {
        var args = new List<string> { "spec" };
        args.AddRange(topics.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase));
        args.Add("--json");

        var res = await CliProcessRunner.RunAsync(exePath, args, workingDir, ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            return Result<string>.Failure(
                WrapperErrors.CliExecutionFailed("melomg", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        try
        {
            using var doc = JsonDocument.Parse(res.StdOut);
            var specs = doc.RootElement.GetProperty("data").GetProperty("specs");
            var chunks = new List<string>();
            foreach (var spec in specs.EnumerateArray())
            {
                if (spec.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        chunks.Add(text.Trim());
                    }
                }
            }

            return Result<string>.Success(string.Join("\n\n", chunks));
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(
                WrapperErrors.CliExecutionFailed("melomg", res.ExitCode, $"Failed to parse `melomg spec --json`: {ex.Message}"));
        }
    }
}
