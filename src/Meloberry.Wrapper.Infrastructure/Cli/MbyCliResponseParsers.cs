using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Infrastructure.Cli;

/// <summary>Pure JSON response parsers for `mby-cli` `--json` envelopes.</summary>
public static class MbyCliResponseParsers
{
    public static Result<MbyCliVersionInfo> ParseVersionInfoPayload(string stdOut, int exitCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdOut);
            var data = doc.RootElement.GetProperty("data");
            var version = GetString(data, "version");
            var caps = ReadStringArray(data, "capabilities");
            return Result<MbyCliVersionInfo>.Success(MbyCliCompatibility.Evaluate(version, caps));
        }
        catch (Exception ex)
        {
            return Result<MbyCliVersionInfo>.Failure(
                WrapperErrors.CliExecutionFailed("mby", exitCode, $"Failed to parse `mby version --json`: {ex.Message}"));
        }
    }

    public static Result<SpecCatalogResult> ParseSpecCatalogPayload(string stdOut, int exitCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdOut);
            var data = doc.RootElement.GetProperty("data");
            var schemas = data.GetProperty("schemas");
            var items = new List<SpecTopicItem>();
            var schemaIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var el in schemas.EnumerateArray())
            {
                var topic = GetString(el, "topic");
                var modeGroups = ParseModeGroups(el);
                var supportsImage = el.TryGetProperty("supportsImage", out var si) && si.ValueKind == JsonValueKind.True;
                items.Add(new SpecTopicItem(
                    Topic: topic,
                    Scope: GetString(el, "scope", "content"),
                    Category: GetString(el, "category", "other"),
                    Summary: GetString(el, "summary"),
                    CompactSignature: GetString(el, "compactSignature"),
                    Command: GetString(el, "command"),
                    IsVirtual: false,
                    ModeGroups: modeGroups,
                    DisplayName: GetNullableString(el, "displayName"),
                    SupportsImage: supportsImage,
                    MarkdownHint: GetNullableString(el, "markdownHint"),
                    MarkdownStarter: GetNullableString(el, "markdownStarter")));

                if (el.TryGetProperty("icon", out var schemaIconEl) && schemaIconEl.ValueKind == JsonValueKind.String)
                {
                    var iconSvg = schemaIconEl.GetString();
                    var normTopic = NormalizeIconKey(topic);
                    if (!string.IsNullOrWhiteSpace(normTopic) && IsInlineSvg(iconSvg))
                    {
                        schemaIcons.TryAdd(normTopic, iconSvg!.Trim());
                    }
                }
            }

            var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (data.TryGetProperty("icons", out var iconsEl) && iconsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in iconsEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var svg = prop.Value.GetString();
                        var normKey = NormalizeIconKey(prop.Name);
                        if (!string.IsNullOrWhiteSpace(normKey) && IsInlineSvg(svg))
                        {
                            icons[normKey] = svg!.Trim();
                        }
                    }
                }
            }

            foreach (var kv in schemaIcons)
            {
                icons.TryAdd(kv.Key, kv.Value);
            }

            return Result<SpecCatalogResult>.Success(new SpecCatalogResult(items, icons, icons.Count > 0));

            static string NormalizeIconKey(string raw)
            {
                var k = raw.Trim().Trim('/');
                if (k.StartsWith("specs/", StringComparison.OrdinalIgnoreCase))
                {
                    k = k["specs/".Length..];
                }

                if (k.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    k = k[..^4];
                }

                return k.Trim();
            }

            static bool IsInlineSvg(string? value) =>
                !string.IsNullOrWhiteSpace(value) &&
                value.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            return Result<SpecCatalogResult>.Failure(
                WrapperErrors.CliExecutionFailed("mby", exitCode, $"Failed to parse `mby spec --json`: {ex.Message}"));
        }
    }

    public static Result<ScaffoldCliResult> ParseScaffoldPayload(string stdOut, int exitCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdOut);
            var data = doc.RootElement.GetProperty("data");
            Dictionary<string, string>? mdFiles = null;
            if (data.TryGetProperty("markdownFiles", out var mfEl) && mfEl.ValueKind == JsonValueKind.Object)
            {
                mdFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in mfEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        mdFiles[prop.Name] = prop.Value.GetString() ?? string.Empty;
                    }
                }
            }

            string? appliedId = null;
            if (data.TryGetProperty("applied", out var appEl) && appEl.ValueKind == JsonValueKind.Object)
            {
                appliedId = GetNullableString(appEl, "targetId") ?? GetNullableString(appEl, "id");
            }

            return Result<ScaffoldCliResult>.Success(new ScaffoldCliResult(
                Format: GetString(data, "format", "md"),
                Slug: GetString(data, "slug", "study_module"),
                RawJson: GetNullableString(data, "rawJson"),
                MarkdownFiles: mdFiles,
                WrittenPath: GetNullableString(data, "writtenPath"),
                RequiresPlaceholderImage: data.TryGetProperty("requiresPlaceholderImage", out var ph) && ph.ValueKind == JsonValueKind.True,
                PlaceholderSpecsUsed: ReadStringArray(data, "placeholderSpecsUsed"),
                Warnings: ReadStringArray(data, "warnings"),
                AppliedTargetId: appliedId));
        }
        catch (Exception ex)
        {
            return Result<ScaffoldCliResult>.Failure(
                WrapperErrors.CliExecutionFailed("mby", exitCode, $"Failed to parse `mby scaffold --json`: {ex.Message}"));
        }
    }

    public static Error? TryParseScaffoldMissingContentError(string stdOut, string stdErr)
    {
        foreach (var raw in new[] { stdOut, stdErr })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("error", out var err) || err.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var code = GetNullableString(err, "code");
                if (!string.Equals(code, "Scaffold.MissingContent", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (err.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                {
                    var labels = new List<string>();
                    foreach (var item in dataArr.EnumerateArray())
                    {
                        var compTitle = GetString(item, "componentTitle", "Component");
                        var topic = GetString(item, "topic", "spec");
                        labels.Add($"{compTitle}:{topic}");
                    }

                    if (labels.Count > 0)
                    {
                        var preview = string.Join(", ", labels.Take(5));
                        var extra = labels.Count > 5 ? $" (+{labels.Count - 5} more)" : string.Empty;
                        return WrapperErrors.InvalidPromptDraft(
                            $"Cannot finalize: {labels.Count} spec(s) have no content ({preview}{extra}). Fill them in Organize or enable 'Allow Placeholders'.");
                    }
                }

                var desc = GetNullableString(err, "description");
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    return WrapperErrors.InvalidPromptDraft(desc);
                }
            }
            catch
            {
                // Fall back to standard CLI error extraction.
            }
        }

        return null;
    }

    public static Result<AssetAddCliResult> ParseAssetAddPayload(string stdOut, int exitCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdOut);
            var data = doc.RootElement.GetProperty("data");
            return Result<AssetAddCliResult>.Success(new AssetAddCliResult(
                WorkspaceId: GetString(data, "workspaceId"),
                RelativeAssetPath: GetString(data, "relativeAssetPath"),
                FullPath: GetString(data, "fullPath"),
                AlreadyExisted: data.TryGetProperty("alreadyExisted", out var ae) && ae.ValueKind == JsonValueKind.True));
        }
        catch (Exception ex)
        {
            return Result<AssetAddCliResult>.Failure(
                WrapperErrors.CliExecutionFailed("mby", exitCode, $"Failed to parse `mby asset add --json`: {ex.Message}"));
        }
    }

    public static Result<PrimeAgentsMdCliResult> ParsePrimeAgentsMdPayload(string stdOut, int exitCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdOut);
            var data = doc.RootElement.GetProperty("data");
            var guide = GetString(data, "guide");
            var approx = data.TryGetProperty("approximateTokens", out var at) && at.TryGetInt32(out var tok) ? tok : TokenEstimator.EstimateTokens(guide);
            var chars = data.TryGetProperty("characterCount", out var cc) && cc.TryGetInt32(out var ch) ? ch : guide.Length;
            return Result<PrimeAgentsMdCliResult>.Success(new PrimeAgentsMdCliResult(
                Markdown: guide,
                ApproximateTokens: approx,
                CharacterCount: chars,
                IncludedTopics: ReadStringArray(data, "includedTopics"),
                WrittenPath: GetNullableString(data, "writtenPath")));
        }
        catch (Exception ex)
        {
            return Result<PrimeAgentsMdCliResult>.Failure(
                WrapperErrors.CliExecutionFailed("mby", exitCode, $"Failed to parse `mby prime --format agents-md --json`: {ex.Message}"));
        }
    }

    public static Result<IReadOnlyList<DiscoveredWebApp>> ParseDiscoveredWebAppsPayload(string stdOut, int exitCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdOut);
            var data = doc.RootElement.GetProperty("data");
            var list = new List<DiscoveredWebApp>();
            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in data.EnumerateArray())
                {
                    var name = GetString(el, "name");
                    var path = GetString(el, "path");
                    var reports = el.TryGetProperty("reportCount", out var rc) && rc.TryGetInt32(out var cnt) ? cnt : 0;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        list.Add(new DiscoveredWebApp(name, path, reports));
                    }
                }
            }

            return Result<IReadOnlyList<DiscoveredWebApp>>.Success(list);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<DiscoveredWebApp>>.Failure(
                WrapperErrors.CliExecutionFailed("mby", exitCode, $"Failed to parse `mby ls --web --json`: {ex.Message}"));
        }
    }

    public static Result<IngestAnalysisCliResult> ParseIngestAnalysisPayload(string stdOut, int exitCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdOut);
            var data = doc.RootElement.GetProperty("data");
            return Result<IngestAnalysisCliResult>.Success(new IngestAnalysisCliResult(
                JsonPath: GetString(data, "jsonPath"),
                MarkdownPath: GetString(data, "markdownPath")));
        }
        catch (Exception ex)
        {
            return Result<IngestAnalysisCliResult>.Failure(
                WrapperErrors.CliExecutionFailed("mby", exitCode, $"Failed to parse `mby analyze --ingest --json`: {ex.Message}"));
        }
    }

    public static Result<CloneEntityCliResult> ParseClonePayload(string stdOut, int exitCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdOut);
            var data = doc.RootElement.GetProperty("data");
            return Result<CloneEntityCliResult>.Success(new CloneEntityCliResult(
                NewId: GetString(data, "newId"),
                EntityKind: GetString(data, "entityKind"),
                TargetContainerId: GetString(data, "targetContainerId"),
                NewIndex: data.TryGetProperty("newIndex", out var idx) && idx.TryGetInt32(out var n) ? n : 1,
                NewRef: GetNullableString(data, "newRef")));
        }
        catch (Exception ex)
        {
            return Result<CloneEntityCliResult>.Failure(
                WrapperErrors.CliExecutionFailed("mby", exitCode, $"Failed to parse `mby clone --json`: {ex.Message}"));
        }
    }

    public static Result<MbyBatchExecutionResult> ParseBatchPayload(string stdOut, int exitCode, string? workingDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdOut);
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
                WrapperErrors.CliExecutionFailed("mby", exitCode, $"Failed to parse `mby batch --json`: {ex.Message}"));
        }
    }

    private static IReadOnlyList<SpecModeGroup>? ParseModeGroups(JsonElement schemaEl)
    {
        if (!schemaEl.TryGetProperty("modeGroups", out var groupsEl) || groupsEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var groups = new List<SpecModeGroup>();
        foreach (var g in groupsEl.EnumerateArray())
        {
            var opts = new List<SpecModeOption>();
            if (g.TryGetProperty("options", out var optsEl) && optsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in optsEl.EnumerateArray())
                {
                    opts.Add(new SpecModeOption(
                        Key: GetString(o, "key"),
                        Label: GetString(o, "label"),
                        IconKey: GetNullableString(o, "iconKey")));
                }
            }

            groups.Add(new SpecModeGroup(
                FieldName: GetString(g, "fieldName"),
                Label: GetString(g, "label"),
                MultiSelect: g.TryGetProperty("multiSelect", out var ms) && ms.ValueKind == JsonValueKind.True,
                Options: opts));
        }

        return groups;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return arr.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
    }

    internal static string GetString(JsonElement el, string prop, string fallback = "") =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    internal static string? GetNullableString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static int? GetNullableInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.TryGetInt32(out var val) ? val : null;
}
