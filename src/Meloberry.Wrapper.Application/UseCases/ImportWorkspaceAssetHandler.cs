using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Result returned after ensuring a picked/dropped asset resides inside the Active Workspace.</summary>
public sealed record ImportWorkspaceAssetResult(
    string RelativePath,
    string FullPath,
    bool Copied);

/// <summary>
/// Safely imports picked or dropped images (into <c>&lt;Workspace&gt;\assets\</c>) or Markdown files
/// (into <c>&lt;Workspace&gt;\notes\</c>) without overwriting existing filename collisions.
/// </summary>
public sealed class ImportWorkspaceAssetHandler(
    ISettingsStore settingsStore,
    IAtomicFileWriter atomicWriter,
    IManagedAppsStore? appsStore = null,
    IMbyCliClient? mbyClient = null)
{
    internal static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg" };

    internal static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".md", ".markdown" };

    public async Task<Result<ImportWorkspaceAssetResult>> ImportAsync(
        string? workspacePath,
        string sourcePath,
        string kind,
        CancellationToken ct = default)
    {
        var candidateWs = workspacePath;
        if (string.IsNullOrWhiteSpace(candidateWs))
        {
            var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
            candidateWs = settings.ActiveWorkspace?.WorkspacePath;
        }

        if (!PathNormalization.TryNormalizeDirectoryPath(candidateWs, out var fullWs))
        {
            return Result<ImportWorkspaceAssetResult>.Failure(
                WrapperErrors.InvalidWorkspaceDir(candidateWs ?? string.Empty));
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return Result<ImportWorkspaceAssetResult>.Failure(
                WrapperErrors.InvalidPath("Asset source path cannot be empty."));
        }

        var normalizedKind = (kind ?? "image").Trim().ToLowerInvariant();
        if (normalizedKind is not ("image" or "markdown"))
        {
            return Result<ImportWorkspaceAssetResult>.Failure(
                WrapperErrors.InvalidPath($"Unsupported asset import kind '{kind}'. Expected 'image' or 'markdown'."));
        }

        string fullSource;
        try
        {
            var trimmed = sourcePath.Trim().Replace('/', Path.DirectorySeparatorChar);
            fullSource = Path.IsPathRooted(trimmed)
                ? Path.GetFullPath(trimmed)
                : Path.GetFullPath(Path.Combine(fullWs, trimmed));
        }
        catch (Exception ex)
        {
            return Result<ImportWorkspaceAssetResult>.Failure(
                WrapperErrors.InvalidPath($"Invalid asset path: {ex.Message}"));
        }

        if (!File.Exists(fullSource))
        {
            return Result<ImportWorkspaceAssetResult>.Failure(
                WrapperErrors.InvalidPath($"Source file not found: {sourcePath}"));
        }

        var ext = Path.GetExtension(fullSource);
        var allowedExts = normalizedKind == "image" ? ImageExtensions : MarkdownExtensions;
        if (!allowedExts.Contains(ext))
        {
            var expectedLabel = normalizedKind == "image"
                ? "an image (.png, .jpg, .jpeg, .gif, .webp, .bmp, .svg)"
                : "a Markdown file (.md, .markdown)";
            return Result<ImportWorkspaceAssetResult>.Failure(
                WrapperErrors.InvalidPath($"File '{Path.GetFileName(fullSource)}' must be {expectedLabel}."));
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(fullWs) + Path.DirectorySeparatorChar;
        if (fullSource.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relExisting = Path.GetRelativePath(fullWs, fullSource).Replace('\\', '/');
            return Result<ImportWorkspaceAssetResult>.Success(
                new ImportWorkspaceAssetResult(relExisting, fullSource, Copied: false));
        }

        if (normalizedKind == "image" && appsStore is not null && mbyClient is not null)
        {
            var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
            var mbyExe = await appsStore.ResolveToolExecutableAsync("mby", fullWs, settings.MbyExecutableOverride, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(mbyExe))
            {
                var verRes = await mbyClient.GetVersionInfoAsync(mbyExe, ct).ConfigureAwait(false);
                if (verRes.IsSuccess && verRes.Value.IsCompatible)
                {
                    var addRes = await mbyClient.AddAssetAsync(mbyExe, workspaceAddr: null, targetDir: fullWs, fullSource, fullWs, ct).ConfigureAwait(false);
                    if (addRes.IsFailure)
                    {
                        return Result<ImportWorkspaceAssetResult>.Failure(addRes.Error!);
                    }

                    return Result<ImportWorkspaceAssetResult>.Success(
                        new ImportWorkspaceAssetResult(addRes.Value.RelativeAssetPath, addRes.Value.FullPath, Copied: !addRes.Value.AlreadyExisted));
                }
            }
        }

        var targetSubfolder = normalizedKind == "image" ? "assets" : "notes";
        var targetDir = Path.Combine(fullWs, targetSubfolder);
        var safeFileName = SanitizeFileName(Path.GetFileName(fullSource));
        var destPath = ResolveUniqueDestination(targetDir, safeFileName);

        try
        {
            atomicWriter.CopyFile(fullSource, destPath);
        }
        catch (Exception ex)
        {
            return Result<ImportWorkspaceAssetResult>.Failure(
                WrapperErrors.IoFailure($"Failed to import '{safeFileName}' into workspace: {ex.Message}"));
        }

        var relCopied = Path.GetRelativePath(fullWs, destPath).Replace('\\', '/');
        return Result<ImportWorkspaceAssetResult>.Success(
            new ImportWorkspaceAssetResult(relCopied, destPath, Copied: true));
    }

    private static string SanitizeFileName(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var chars = stem.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        var collapsed = new string(chars);
        while (collapsed.Contains("__", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("__", "_", StringComparison.Ordinal);
        }

        collapsed = collapsed.Trim('_', '.');
        if (string.IsNullOrWhiteSpace(collapsed))
        {
            collapsed = "asset";
        }

        return $"{collapsed}{ext}";
    }

    private static string ResolveUniqueDestination(string targetDir, string fileName)
    {
        var candidate = Path.Combine(targetDir, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var counter = 2;
        while (true)
        {
            candidate = Path.Combine(targetDir, $"{stem}_{counter}{ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            counter++;
        }
    }
}
