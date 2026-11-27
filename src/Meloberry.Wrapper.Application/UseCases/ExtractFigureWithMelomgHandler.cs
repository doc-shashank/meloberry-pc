using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Domain.Services;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Resolves and extracts PDF figures directly into `<Workspace>/assets/` via installed `melomg`.</summary>
public sealed class ExtractFigureWithMelomgHandler(
    IManagedAppsStore appsStore,
    IMelomgCliClient melomgClient,
    ISettingsStore settingsStore)
{
    public async Task<Result<ExtractFigureResult>> ExtractAsync(
        ExtractFigureRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var wsCandidate = !string.IsNullOrWhiteSpace(request.WorkspacePath)
            ? request.WorkspacePath
            : settings.ActiveWorkspace?.WorkspacePath;
        if (!PathNormalization.TryNormalizeDirectoryPath(wsCandidate, out var fullWs))
        {
            return Result<ExtractFigureResult>.Failure(WrapperErrors.InvalidWorkspaceDir(wsCandidate ?? string.Empty));
        }

        var selector = MelomgFigureQueryNormalizer.Normalize(request.Query);
        if (string.IsNullOrWhiteSpace(selector))
        {
            return Result<ExtractFigureResult>.Failure(
                WrapperErrors.InvalidPromptDraft("Enter a figure query such as 'Figure 1.1', 'f1', or 'p4'."));
        }

        var melomgExe = await appsStore.ResolveToolExecutableAsync(
            "melomg",
            fullWs,
            settings.MelomgExecutableOverride,
            ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(melomgExe))
        {
            return Result<ExtractFigureResult>.Failure(WrapperErrors.CliNotFound("melomg"));
        }

        var rawPdf = request.PdfPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawPdf))
        {
            return Result<ExtractFigureResult>.Failure(WrapperErrors.InvalidPath("PDF path or Document ID is required."));
        }

        var targetOrPdf = await ResolveMelomgTargetAsync(melomgExe, fullWs, rawPdf, ct).ConfigureAwait(false);
        if (targetOrPdf.IsFailure)
        {
            return Result<ExtractFigureResult>.Failure(targetOrPdf.Error!);
        }

        var assetsDir = Path.Combine(fullWs, "assets");
        Directory.CreateDirectory(assetsDir);

        var res = await melomgClient.ExtractFigureAsync(
            melomgExe,
            targetOrPdf.Value,
            selector,
            assetsDir,
            request.Quality,
            request.MaskAll,
            request.EmitMeta,
            retainDb: true,
            offline: true,
            workingDir: fullWs,
            ct).ConfigureAwait(false);

        if (res.IsFailure)
        {
            return Result<ExtractFigureResult>.Failure(res.Error!);
        }

        var artifact = res.Value.Artifacts.FirstOrDefault();
        if (artifact is null || string.IsNullOrWhiteSpace(artifact.OutputImagePath))
        {
            return Result<ExtractFigureResult>.Failure(
                WrapperErrors.CliExecutionFailed("melomg", 0, $"No extracted image artifact returned for selector '{selector}'."));
        }

        var fullImg = Path.IsPathRooted(artifact.OutputImagePath)
            ? Path.GetFullPath(artifact.OutputImagePath)
            : Path.GetFullPath(Path.Combine(fullWs, artifact.OutputImagePath));

        var relImg = PathNormalization.IsSameOrDescendantDirectory(fullWs, Path.GetDirectoryName(fullImg) ?? fullWs)
            ? Path.GetRelativePath(fullWs, fullImg).Replace('\\', '/')
            : artifact.OutputImagePath.Replace('\\', '/');

        string? relMeta = null;
        if (!string.IsNullOrWhiteSpace(artifact.MetadataPath))
        {
            var fullMeta = Path.GetFullPath(artifact.MetadataPath);
            if (PathNormalization.IsSameOrDescendantDirectory(fullWs, Path.GetDirectoryName(fullMeta) ?? fullWs))
            {
                relMeta = Path.GetRelativePath(fullWs, fullMeta).Replace('\\', '/');
            }
        }

        return Result<ExtractFigureResult>.Success(new ExtractFigureResult(
            DocumentId: res.Value.DocumentId,
            Selector: selector,
            RelativeImagePath: relImg,
            FullImagePath: fullImg,
            RelativeMetaPath: relMeta,
            Caption: artifact.Caption,
            PixelWidth: artifact.PixelWidth,
            PixelHeight: artifact.PixelHeight));
    }

    private async Task<Result<string>> ResolveMelomgTargetAsync(
        string melomgExe,
        string fullWs,
        string rawTargetOrPdf,
        CancellationToken ct)
    {
        if (rawTargetOrPdf.StartsWith("Doc_", StringComparison.OrdinalIgnoreCase))
        {
            return Result<string>.Success(rawTargetOrPdf);
        }

        var fullPdf = Path.IsPathRooted(rawTargetOrPdf)
            ? Path.GetFullPath(rawTargetOrPdf)
            : Path.GetFullPath(Path.Combine(fullWs, rawTargetOrPdf));

        if (!File.Exists(fullPdf))
        {
            return Result<string>.Failure(WrapperErrors.InvalidPath($"PDF file not found: {rawTargetOrPdf}"));
        }

        var pdfStem = Path.GetFileNameWithoutExtension(fullPdf);
        var docsRes = await melomgClient.ListDocumentsAsync(melomgExe, fullWs, ct).ConfigureAwait(false);
        if (docsRes.IsSuccess)
        {
            var matched = docsRes.Value.FirstOrDefault(d =>
                !string.IsNullOrWhiteSpace(d.DocumentId) &&
                !string.IsNullOrWhiteSpace(d.Title) &&
                (string.Equals(d.Title.Trim(), pdfStem, StringComparison.OrdinalIgnoreCase) ||
                 d.Title.Trim().StartsWith(pdfStem, StringComparison.OrdinalIgnoreCase)));
            if (matched is not null)
            {
                return Result<string>.Success(matched.DocumentId);
            }
        }

        return Result<string>.Success(fullPdf);
    }
}
