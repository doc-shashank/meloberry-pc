using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Infrastructure.Cli;

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

    public async Task<Result<IReadOnlyList<MelomgDocumentSummary>>> ListDocumentsAsync(
        string exePath,
        string? workingDir,
        CancellationToken ct = default)
    {
        var args = new List<string> { "ls" };
        if (!string.IsNullOrWhiteSpace(workingDir))
        {
            args.Add(workingDir);
        }
        args.Add("--json");

        var res = await CliProcessRunner.RunAsync(exePath, args, workingDir, ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            return Result<IReadOnlyList<MelomgDocumentSummary>>.Failure(
                WrapperErrors.CliExecutionFailed("melomg", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        try
        {
            using var doc = JsonDocument.Parse(res.StdOut);
            var data = doc.RootElement.GetProperty("data");
            var list = new List<MelomgDocumentSummary>();
            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in data.EnumerateArray())
                {
                    list.Add(new MelomgDocumentSummary(
                        DocumentId: GetString(el, "documentId"),
                        Title: GetString(el, "title"),
                        PageCount: GetInt(el, "pageCount"),
                        FigureCount: GetInt(el, "figureCount"),
                        StorageDirectory: GetString(el, "storageDirectory")));
                }
            }

            return Result<IReadOnlyList<MelomgDocumentSummary>>.Success(list);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<MelomgDocumentSummary>>.Failure(
                WrapperErrors.CliExecutionFailed("melomg", res.ExitCode, $"Failed to parse `melomg ls --json`: {ex.Message}"));
        }
    }

    public async Task<Result<MelomgExtractResult>> ExtractFigureAsync(
        string exePath,
        string targetOrPdf,
        string selector,
        string outputDir,
        string? quality,
        bool maskAll,
        bool emitMeta,
        bool retainDb,
        bool offline,
        string? workingDir,
        CancellationToken ct = default)
    {
        var args = new List<string> { "extract", targetOrPdf, selector, "-o", outputDir };
        if (!string.IsNullOrWhiteSpace(quality))
        {
            args.Add("--quality");
            args.Add(quality.Trim());
        }

        if (maskAll) args.Add("--mask-all");
        if (emitMeta) args.Add("--meta");
        if (retainDb) args.Add("--retain-db");
        if (offline) args.Add("--offline");
        args.Add("--json");

        var res = await CliProcessRunner.RunAsync(exePath, args, workingDir, ct, timeout: TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            return Result<MelomgExtractResult>.Failure(
                WrapperErrors.CliExecutionFailed("melomg", res.ExitCode, CliProcessRunner.ExtractErrorMessage(res.StdOut, res.StdErr)));
        }

        try
        {
            using var doc = JsonDocument.Parse(res.StdOut);
            var data = doc.RootElement.GetProperty("data");
            var docId = GetString(data, "documentId");
            var outDir = GetString(data, "outputDirectory", outputDir);
            var artifacts = new List<MelomgExtractedArtifact>();

            if (data.TryGetProperty("artifacts", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var imgPath = GetString(el, "outputImagePath");
                    var metaCandidate = !string.IsNullOrWhiteSpace(imgPath) && emitMeta
                        ? Path.ChangeExtension(imgPath, null) + ".meta.json"
                        : null;
                    artifacts.Add(new MelomgExtractedArtifact(
                        FigureId: GetString(el, "figureId"),
                        FigureNumber: GetNullableString(el, "figureNumber"),
                        PageNumber: GetInt(el, "pageNumber"),
                        Caption: GetNullableString(el, "caption"),
                        OutputImagePath: imgPath,
                        MetadataPath: metaCandidate is not null && File.Exists(metaCandidate) ? metaCandidate : null,
                        PixelWidth: GetInt(el, "pixelWidth"),
                        PixelHeight: GetInt(el, "pixelHeight")));
                }
            }

            return Result<MelomgExtractResult>.Success(new MelomgExtractResult(docId, outDir, artifacts));
        }
        catch (Exception ex)
        {
            return Result<MelomgExtractResult>.Failure(
                WrapperErrors.CliExecutionFailed("melomg", res.ExitCode, $"Failed to parse `melomg extract --json`: {ex.Message}"));
        }
    }

    private static string GetString(JsonElement el, string prop, string fallback = "") =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static string? GetNullableString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.TryGetInt32(out var val) ? val : 0;
}
