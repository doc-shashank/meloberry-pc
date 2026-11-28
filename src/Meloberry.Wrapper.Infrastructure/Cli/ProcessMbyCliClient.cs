using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Infrastructure.Cli;

/// <summary>Out-of-process runner for `mby` CLI.</summary>
public sealed partial class ProcessMbyCliClient : IMbyCliClient
{
    private readonly ConcurrentDictionary<string, (DateTime MtimeUtc, MbyCliVersionInfo Info)> _versionCache =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<Result<MbyCliVersionInfo>> GetVersionInfoAsync(string exePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return Result<MbyCliVersionInfo>.Failure(WrapperErrors.CliNotFound("mby"));
        }

        var fullPath = Path.GetFullPath(exePath);
        DateTime mtime = File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath) : DateTime.MinValue;
        if (mtime != DateTime.MinValue &&
            _versionCache.TryGetValue(fullPath, out var cached) &&
            cached.MtimeUtc == mtime)
        {
            return Result<MbyCliVersionInfo>.Success(cached.Info);
        }

        var res = await CliProcessRunner.RunAsync(fullPath, ["version", "--json"], null, ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            return Result<MbyCliVersionInfo>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        var parsed = MbyCliResponseParsers.ParseVersionInfoPayload(res.StdOut, res.ExitCode);
        if (parsed.IsSuccess && mtime != DateTime.MinValue)
        {
            _versionCache[fullPath] = (mtime, parsed.Value);
        }

        return parsed;
    }

    public async Task<Result<IReadOnlyList<SpecTopicItem>>> GetSpecCatalogAsync(
        string exePath,
        string? workingDir,
        CancellationToken ct = default)
    {
        var withIcons = await GetSpecCatalogWithIconsAsync(exePath, workingDir, ct).ConfigureAwait(false);
        return withIcons.IsSuccess
            ? Result<IReadOnlyList<SpecTopicItem>>.Success(withIcons.Value.Specs)
            : Result<IReadOnlyList<SpecTopicItem>>.Failure(withIcons.Error!);
    }

    public async Task<Result<SpecCatalogResult>> GetSpecCatalogWithIconsAsync(
        string exePath,
        string? workingDir,
        CancellationToken ct = default)
    {
        var res = await CliProcessRunner.RunAsync(exePath, ["spec", "--icons", "--templates", "--json"], workingDir, ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            var errMsg = CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr);
            if (errMsg.Contains("--templates", StringComparison.OrdinalIgnoreCase) ||
                errMsg.Contains("UnsupportedOption", StringComparison.OrdinalIgnoreCase))
            {
                res = await CliProcessRunner.RunAsync(exePath, ["spec", "--icons", "--json"], workingDir, ct).ConfigureAwait(false);
                if (res.ExitCode != 0)
                {
                    errMsg = CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr);
                    if (errMsg.Contains("--icons", StringComparison.OrdinalIgnoreCase) ||
                        errMsg.Contains("UnsupportedOption", StringComparison.OrdinalIgnoreCase))
                    {
                        res = await CliProcessRunner.RunAsync(exePath, ["spec", "--json"], workingDir, ct).ConfigureAwait(false);
                    }
                }
            }
        }

        if (res.ExitCode != 0)
        {
            return Result<SpecCatalogResult>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        return MbyCliResponseParsers.ParseSpecCatalogPayload(res.StdOut, res.ExitCode);
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
                            Name: MbyCliResponseParsers.GetString(f, "name"),
                            Type: MbyCliResponseParsers.GetString(f, "type"),
                            Required: f.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.True,
                            Description: MbyCliResponseParsers.GetString(f, "description"),
                            AllowedValues: allowed,
                            MaxLength: MbyCliResponseParsers.GetNullableInt(f, "maxLength"),
                            Pattern: MbyCliResponseParsers.GetNullableString(f, "pattern"),
                            MinValue: MbyCliResponseParsers.GetNullableInt(f, "minValue"),
                            MaxValue: MbyCliResponseParsers.GetNullableInt(f, "maxValue")));
                    }
                }

                string? exampleJson = null;
                if (el.TryGetProperty("example", out var exEl) && exEl.ValueKind != JsonValueKind.Undefined && exEl.ValueKind != JsonValueKind.Null)
                {
                    exampleJson = JsonSerializer.Serialize(exEl, new JsonSerializerOptions { WriteIndented = true });
                }

                list.Add(new AgentsMdSpecDetail(
                    Topic: MbyCliResponseParsers.GetString(el, "topic"),
                    Scope: MbyCliResponseParsers.GetString(el, "scope", "content"),
                    Category: MbyCliResponseParsers.GetString(el, "category", "other"),
                    Summary: MbyCliResponseParsers.GetString(el, "summary"),
                    CompactSignature: MbyCliResponseParsers.GetString(el, "compactSignature"),
                    Command: MbyCliResponseParsers.GetString(el, "command"),
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
                        Id: MbyCliResponseParsers.GetString(el, "id"),
                        Kind: MbyCliResponseParsers.GetString(el, "kind"),
                        SubType: MbyCliResponseParsers.GetString(el, "subType"),
                        StorageDirectory: MbyCliResponseParsers.GetString(el, "storageDirectory"),
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

    public async Task<Result<IReadOnlyList<DiscoveredWebApp>>> ListWebAppsAsync(
        string exePath,
        string directory,
        CancellationToken ct = default)
    {
        var res = await CliProcessRunner.RunAsync(exePath, ["ls", directory, "--web", "--json"], directory, ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            return Result<IReadOnlyList<DiscoveredWebApp>>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        return MbyCliResponseParsers.ParseDiscoveredWebAppsPayload(res.StdOut, res.ExitCode);
    }

    public Task<Result<string>> BuildWorkspaceAsync(
        string exePath,
        string workspaceId,
        string outputDir,
        string? workingDir,
        CancellationToken ct = default) =>
        BuildWorkspaceAsync(exePath, workspaceId, outputDir, workingDir, includeAllThemes: true, ct);

    public async Task<Result<string>> BuildWorkspaceAsync(
        string exePath,
        string workspaceId,
        string outputDir,
        string? workingDir,
        bool includeAllThemes,
        CancellationToken ct = default)
    {
        var args = new List<string> { "build", workspaceId, "-o", outputDir };
        if (includeAllThemes)
        {
            args.Add("--all-themes");
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
            var outDir = MbyCliResponseParsers.GetNullableString(data, "outputDirectory") ?? outputDir;
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
            var pkgPath = MbyCliResponseParsers.GetNullableString(data, "packagePath")
                ?? MbyCliResponseParsers.GetNullableString(data, "outputPath")
                ?? outputPath;
            return Result<string>.Success(pkgPath);
        }
        catch
        {
            return Result<string>.Success(outputPath);
        }
    }

    public Task<Result<string>> ApplySpecJsonAsync(
        string exePath,
        string specFilePath,
        string? workingDir,
        CancellationToken ct = default) =>
        RunApplyCoreAsync(exePath, ["apply", specFilePath, "--json"], workingDir, ct);

    public async Task<Result<MbyBatchExecutionResult>> RunBatchAsync(
        string exePath,
        string batchJson,
        string? workingDir,
        CancellationToken ct = default)
    {
        var res = await CliProcessRunner.RunAsync(
            exePath,
            ["batch", "-", "--json"],
            workingDir,
            ct,
            timeout: TimeSpan.FromMinutes(3),
            stdinText: batchJson).ConfigureAwait(false);

        if (res.ExitCode != 0)
        {
            return Result<MbyBatchExecutionResult>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        return MbyCliResponseParsers.ParseBatchPayload(res.StdOut, res.ExitCode, workingDir);
    }
}
