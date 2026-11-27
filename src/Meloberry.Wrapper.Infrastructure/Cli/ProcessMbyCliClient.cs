using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Infrastructure.Cli;

/// <summary>Out-of-process runner for `mby` CLI.</summary>
public sealed class ProcessMbyCliClient : IMbyCliClient
{
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
        var res = await CliProcessRunner.RunAsync(exePath, ["spec", "--icons", "--json"], workingDir, ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            var errMsg = CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr);
            if (errMsg.Contains("--icons", StringComparison.OrdinalIgnoreCase) ||
                errMsg.Contains("UnsupportedOption", StringComparison.OrdinalIgnoreCase))
            {
                res = await CliProcessRunner.RunAsync(exePath, ["spec", "--json"], workingDir, ct).ConfigureAwait(false);
            }
        }

        if (res.ExitCode != 0)
        {
            return Result<SpecCatalogResult>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        return ParseSpecCatalogPayload(res.StdOut, res.ExitCode);
    }

    public static Result<SpecCatalogResult> ParseSpecCatalogPayload(string stdOut, int exitCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdOut);
            var data = doc.RootElement.GetProperty("data");
            var schemas = data.GetProperty("schemas");
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
                    ModeGroups: SpecCatalogDefaults.GetModeGroupsForTopic(topic),
                    DisplayName: GetNullableString(el, "displayName")));
            }

            var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (data.TryGetProperty("icons", out var iconsEl) && iconsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in iconsEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var svg = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(svg))
                        {
                            icons[prop.Name] = svg;
                        }
                    }
                }
            }

            return Result<SpecCatalogResult>.Success(new SpecCatalogResult(items, icons));
        }
        catch (Exception ex)
        {
            return Result<SpecCatalogResult>.Failure(
                WrapperErrors.CliExecutionFailed("mby", exitCode, $"Failed to parse `mby spec --json`: {ex.Message}"));
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

        try
        {
            using var doc = JsonDocument.Parse(res.StdOut);
            var data = doc.RootElement.GetProperty("data");
            var stepCount = data.TryGetProperty("stepCount", out var sc) && sc.TryGetInt32(out var count) ? count : 0;
            string? workspaceId = null;
            string? outputDir = null;

            if (data.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
            {
                foreach (var step in steps.EnumerateArray())
                {
                    if (!step.TryGetProperty("data", out var stepData) || stepData.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    workspaceId ??= GetNullableString(stepData, "targetId") ?? GetNullableString(stepData, "workspaceId");
                    var outCandidate = GetNullableString(stepData, "outputDirectory");
                    if (!string.IsNullOrWhiteSpace(outCandidate))
                    {
                        outputDir = !Path.IsPathRooted(outCandidate) && !string.IsNullOrWhiteSpace(workingDir)
                            ? Path.GetFullPath(outCandidate, workingDir)
                            : Path.GetFullPath(outCandidate);
                    }
                }
            }

            return Result<MbyBatchExecutionResult>.Success(new MbyBatchExecutionResult(workspaceId, outputDir, stepCount));
        }
        catch (Exception ex)
        {
            return Result<MbyBatchExecutionResult>.Failure(
                WrapperErrors.CliExecutionFailed("mby", res.ExitCode, $"Failed to parse `mby batch --json`: {ex.Message}"));
        }
    }

    private static string GetString(JsonElement el, string prop, string fallback = "") =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static string? GetNullableString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetNullableInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.TryGetInt32(out var val) ? val : null;
}
