using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Domain.Services;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>
/// Compiles the live workspace draft into modular Markdown + assets and runs transactional `mby batch -`
/// (`apply` -> `check` -> `build`) before mounting the WebApp into Step 5 Viewer.
/// </summary>
public sealed class FinalizeWorkspaceHandler(
    IManagedAppsStore appsStore,
    IWorkspaceDraftStore draftStore,
    IMbyCliClient mbyClient,
    ManageLibraryHandler manageLibrary,
    ISettingsStore settingsStore,
    IAtomicFileWriter? atomicWriter = null)
{
    private static readonly byte[] PlaceholderPngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
        0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00,
        0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x60, 0x00, 0x02, 0x00,
        0x00, 0x05, 0x00, 0x01, 0x7A, 0x5E, 0xAB, 0x3F, 0x00, 0x00, 0x00, 0x00,
        0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
    ];

    public async Task<Result<FinalizeResult>> FinalizeAsync(
        FinalizeRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var wsCandidate = !string.IsNullOrWhiteSpace(request.WorkspacePath)
            ? request.WorkspacePath
            : settings.ActiveWorkspace?.WorkspacePath;
        if (!PathNormalization.TryNormalizeDirectoryPath(wsCandidate, out var fullWs))
        {
            return Result<FinalizeResult>.Failure(WrapperErrors.InvalidWorkspaceDir(wsCandidate ?? string.Empty));
        }

        var mbyExe = await appsStore.ResolveToolExecutableAsync(
            "mby",
            fullWs,
            settings.MbyExecutableOverride,
            ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(mbyExe))
        {
            return Result<FinalizeResult>.Failure(WrapperErrors.CliNotFound("mby"));
        }

        var draft = await draftStore.LoadAsync(fullWs, ct).ConfigureAwait(false);
        var allowPlaceholders = request.AllowPlaceholders || draft.AllowPlaceholdersOnFinalize;

        var loadedMdRes = await LoadReferencedMarkdownFilesAsync(fullWs, draft, ct).ConfigureAwait(false);
        if (loadedMdRes.IsFailure)
        {
            return Result<FinalizeResult>.Failure(loadedMdRes.Error!);
        }

        var assembledRes = WorkspaceMarkdownAssembler.Assemble(draft, loadedMdRes.Value, allowPlaceholders);
        if (assembledRes.IsFailure)
        {
            return Result<FinalizeResult>.Failure(assembledRes.Error!);
        }

        var assembled = assembledRes.Value;
        if (assembled.MissingContentSpecs.Count > 0 && !allowPlaceholders)
        {
            var previewList = string.Join(", ", assembled.MissingContentSpecs.Take(5).Select(m => $"{m.ComponentTitle}:{m.Topic}"));
            var extra = assembled.MissingContentSpecs.Count > 5 ? $" (+{assembled.MissingContentSpecs.Count - 5} more)" : string.Empty;
            return Result<FinalizeResult>.Failure(WrapperErrors.InvalidPromptDraft(
                $"Cannot finalize: {assembled.MissingContentSpecs.Count} spec(s) have no content ({previewList}{extra}). Fill them in Organize or enable 'Allow Placeholders'."));
        }

        var buildSrcDir = Path.Combine(fullWs, ".meloberry", "build-src", assembled.Slug);
        try
        {
            if (Directory.Exists(buildSrcDir))
            {
                Directory.Delete(buildSrcDir, recursive: true);
            }
            Directory.CreateDirectory(buildSrcDir);

            foreach (var (relFile, content) in assembled.Files)
            {
                var targetPath = Path.Combine(buildSrcDir, relFile.Replace('/', Path.DirectorySeparatorChar));
                await WriteTextAsync(targetPath, content, ct).ConfigureAwait(false);
            }

            if (assembled.RequiresPlaceholderImage)
            {
                await WriteBytesAsync(Path.Combine(buildSrcDir, "assets", "placeholder.png"), PlaceholderPngBytes, ct).ConfigureAwait(false);
            }

            CopyReferencedAssets(fullWs, buildSrcDir, assembled.ReferencedAssetPaths);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<FinalizeResult>.Failure(WrapperErrors.IoFailure($"Failed preparing finalize build directory: {ex.Message}"));
        }

        var outDir = ResolveOutputDirectory(fullWs, assembled.Slug, request.OutputDir, settings.DefaultBuildOutputDir);
        var relBuildSrc = Path.GetRelativePath(fullWs, buildSrcDir).Replace('\\', '/');
        var payload = new object[]
        {
            new { op = "apply", file = relBuildSrc },
            new { op = "check", addr = "$0.id" },
            new
            {
                op = "build",
                addr = "$0.id",
                @out = outDir,
                verify = true,
                staticOnly = request.VerifyStaticOnly
            }
        };
        var batchJson = JsonSerializer.Serialize(payload);
        var batchRes = await mbyClient.RunBatchAsync(mbyExe, batchJson, fullWs, ct).ConfigureAwait(false);
        if (batchRes.IsFailure)
        {
            return Result<FinalizeResult>.Failure(batchRes.Error!);
        }

        var effectiveWebDir = !string.IsNullOrWhiteSpace(batchRes.Value.OutputDirectory)
            ? batchRes.Value.OutputDirectory
            : (Path.IsPathRooted(outDir) ? outDir : Path.GetFullPath(outDir, fullWs));

        var openRes = await manageLibrary.OpenWebAppAsync(effectiveWebDir, ct).ConfigureAwait(false);
        if (openRes.IsFailure)
        {
            return Result<FinalizeResult>.Failure(openRes.Error!);
        }

        var (slotId, relViewerUrl, _) = openRes.Value;
        return Result<FinalizeResult>.Success(new FinalizeResult(
            WorkspaceId: batchRes.Value.WorkspaceId ?? string.Empty,
            BuildSourceDirectory: buildSrcDir,
            WebAppDirectory: effectiveWebDir,
            SlotId: slotId,
            ViewerUrl: relViewerUrl,
            PlaceholderSpecsUsed: assembled.PlaceholderSpecsUsed));
    }

    private async Task WriteTextAsync(string path, string content, CancellationToken ct)
    {
        if (atomicWriter is not null)
        {
            await atomicWriter.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
    }

    private async Task WriteBytesAsync(string path, byte[] bytes, CancellationToken ct)
    {
        if (atomicWriter is not null)
        {
            await atomicWriter.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
    }

    private static async Task<Result<IReadOnlyDictionary<string, string>>> LoadReferencedMarkdownFilesAsync(
        string fullWs,
        PromptDraft draft,
        CancellationToken ct)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fullRoot = Path.TrimEndingDirectorySeparator(fullWs) + Path.DirectorySeparatorChar;

        foreach (var comp in draft.Components ?? [])
        {
            foreach (var inst in comp.OrderedItems ?? [])
            {
                if (inst.Content?.Mode != SpecContentMode.MarkdownFile ||
                    string.IsNullOrWhiteSpace(inst.Content.MarkdownFilePath))
                {
                    continue;
                }

                var key = inst.Content.MarkdownFilePath.Replace('\\', '/');
                if (dict.ContainsKey(key))
                {
                    continue;
                }

                var candidate = Path.GetFullPath(Path.Combine(fullWs, key.Replace('/', Path.DirectorySeparatorChar)));
                if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return Result<IReadOnlyDictionary<string, string>>.Failure(
                        WrapperErrors.InvalidPath($"Markdown file escapes Active Workspace: {key}"));
                }

                if (!File.Exists(candidate))
                {
                    return Result<IReadOnlyDictionary<string, string>>.Failure(
                        WrapperErrors.InvalidPath($"Attached Markdown file not found: {key}"));
                }

                dict[key] = await File.ReadAllTextAsync(candidate, ct).ConfigureAwait(false);
            }
        }

        return Result<IReadOnlyDictionary<string, string>>.Success(dict);
    }

    private static void CopyReferencedAssets(string fullWs, string buildSrcDir, IReadOnlyList<string> assetRelPaths)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(fullWs) + Path.DirectorySeparatorChar;
        foreach (var rel in assetRelPaths)
        {
            var srcCandidate = Path.GetFullPath(Path.Combine(fullWs, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!srcCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(srcCandidate))
            {
                continue;
            }

            var destPath = Path.Combine(buildSrcDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(srcCandidate, destPath, overwrite: true);

            var assetsDest = Path.Combine(buildSrcDir, "assets", Path.GetFileName(srcCandidate));
            if (!string.Equals(destPath, assetsDest, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(assetsDest)!);
                File.Copy(srcCandidate, assetsDest, overwrite: true);
            }
        }
    }

    private static string ResolveOutputDirectory(string fullWs, string slug, string? requestedOut, string? defaultBuildDir)
    {
        if (!string.IsNullOrWhiteSpace(requestedOut))
        {
            return Path.IsPathRooted(requestedOut)
                ? Path.GetFullPath(requestedOut)
                : Path.GetFullPath(requestedOut, fullWs);
        }

        if (!string.IsNullOrWhiteSpace(defaultBuildDir))
        {
            return Path.Combine(Path.GetFullPath(defaultBuildDir), $"{slug}_web");
        }

        return $"{slug}_web";
    }
}
