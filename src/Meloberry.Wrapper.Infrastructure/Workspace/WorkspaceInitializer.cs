using System.Security.Cryptography;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Infrastructure.Workspace;

/// <summary>
/// Plans and executes non-destructive Active Workspace copying:
/// executables -&gt; root, files -&gt; root, folders -&gt; &lt;workspace&gt;/&lt;FolderName&gt;/...
/// </summary>
public sealed class WorkspaceInitializer(
    IWrapperDataPaths dataPaths,
    IWebAppAnalysisStore analysisStore) : IWorkspaceInitializer
{
    private sealed record PlannedFileCopy(
        string SourcePath,
        string DestinationPath,
        string RelativeTargetPath,
        StagedCopyKind Kind,
        bool IsExecutable,
        string? RootFolderName);

    public async Task<Result<WorkspaceInitPlan>> PlanAsync(
        string workspacePath,
        IReadOnlyList<string> selectedAppFileNames,
        IReadOnlyList<StagedCopyItem> stagedItems,
        CancellationToken ct = default)
    {
        var planRes = await BuildOperationsAsync(workspacePath, selectedAppFileNames, stagedItems, ct).ConfigureAwait(false);
        if (planRes.IsFailure)
        {
            return Result<WorkspaceInitPlan>.Failure(planRes.Error!);
        }

        var (fullWs, operations, skippedIdentical, conflicts) = planRes.Value;
        return Result<WorkspaceInitPlan>.Success(new WorkspaceInitPlan(
            WorkspacePath: fullWs,
            SelectedAppFileNames: selectedAppFileNames,
            StagedItems: stagedItems,
            PlannedCopiesCount: operations.Count,
            SkippedIdenticalCount: skippedIdentical,
            Conflicts: conflicts));
    }

    public async Task<Result<WorkspaceInitResult>> InitializeAsync(
        string workspacePath,
        IReadOnlyList<string> selectedAppFileNames,
        IReadOnlyList<StagedCopyItem> stagedItems,
        ConflictPolicy conflictPolicy,
        CancellationToken ct = default)
    {
        var planRes = await BuildOperationsAsync(workspacePath, selectedAppFileNames, stagedItems, ct).ConfigureAwait(false);
        if (planRes.IsFailure)
        {
            return Result<WorkspaceInitResult>.Failure(planRes.Error!);
        }

        var (fullWs, operations, skippedIdentical, _) = planRes.Value;
        string? wsMbyPath = null;
        string? wsMelomgPath = null;
        var copiedApps = new List<string>();
        var copiedFiles = new List<string>();
        var copiedFolderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copiedRelativePaths = new List<string>();
        var skipped = skippedIdentical;
        var overwritten = 0;

        try
        {
            Directory.CreateDirectory(fullWs);

            foreach (var op in operations)
            {
                ct.ThrowIfCancellationRequested();

                var destExists = File.Exists(op.DestinationPath);
                if (destExists && conflictPolicy == ConflictPolicy.SkipExisting)
                {
                    skipped++;
                    RecordWorkspaceToolPath(op.DestinationPath, ref wsMbyPath, ref wsMelomgPath);
                    copiedRelativePaths.Add(op.RelativeTargetPath);
                    continue;
                }

                if (destExists)
                {
                    overwritten++;
                }

                var destDir = Path.GetDirectoryName(op.DestinationPath);
                if (!string.IsNullOrWhiteSpace(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                var tmp = $"{op.DestinationPath}.{Guid.NewGuid():N}.mbytmp";
                try
                {
                    File.Copy(op.SourcePath, tmp, overwrite: true);
                    File.Move(tmp, op.DestinationPath, overwrite: true);
                }
                catch
                {
                    if (File.Exists(tmp))
                    {
                        try { File.Delete(tmp); } catch { }
                    }

                    throw;
                }

                copiedRelativePaths.Add(op.RelativeTargetPath);
                if (op.IsExecutable)
                {
                    copiedApps.Add(op.RelativeTargetPath);
                    RecordWorkspaceToolPath(op.DestinationPath, ref wsMbyPath, ref wsMelomgPath);
                }
                else if (op.Kind == StagedCopyKind.Folder && !string.IsNullOrWhiteSpace(op.RootFolderName))
                {
                    copiedFolderSet.Add(op.RootFolderName!);
                }
                else
                {
                    copiedFiles.Add(op.RelativeTargetPath);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<WorkspaceInitResult>.Failure(WrapperErrors.IoFailure($"Failed to copy workspace items: {ex.Message}"));
        }

        // Also detect already-identical workspace exes that were skipped above.
        foreach (var appName in selectedAppFileNames)
        {
            var candidate = Path.Combine(fullWs, Path.GetFileName(appName));
            if (File.Exists(candidate))
            {
                RecordWorkspaceToolPath(candidate, ref wsMbyPath, ref wsMelomgPath);
            }
        }

        return Result<WorkspaceInitResult>.Success(new WorkspaceInitResult(
            WorkspacePath: fullWs,
            CopiedApps: copiedApps.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            CopiedFiles: copiedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            CopiedFolders: copiedFolderSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            SkippedCount: skipped,
            OverwrittenCount: overwritten,
            WorkspaceMbyPath: wsMbyPath,
            WorkspaceMelomgPath: wsMelomgPath,
            CopiedRelativePaths: copiedRelativePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
    }

    public Task<IReadOnlyList<DiscoveredWebApp>> DiscoverWebAppsAsync(string workspacePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return Task.FromResult<IReadOnlyList<DiscoveredWebApp>>([]);
        }

        var found = new List<DiscoveredWebApp>();
        if (IsCompiledWebApp(workspacePath))
        {
            found.Add(new DiscoveredWebApp(
                Path.GetFileName(Path.TrimEndingDirectorySeparator(workspacePath)),
                Path.GetFullPath(workspacePath),
                analysisStore.CountReports(workspacePath)));
        }

        foreach (var subDir in Directory.GetDirectories(workspacePath, "*", SearchOption.TopDirectoryOnly))
        {
            var dirName = Path.GetFileName(subDir);
            if (dirName.StartsWith('.'))
            {
                continue;
            }

            if (IsCompiledWebApp(subDir))
            {
                found.Add(new DiscoveredWebApp(
                    dirName,
                    Path.GetFullPath(subDir),
                    analysisStore.CountReports(subDir)));
            }
        }

        return Task.FromResult<IReadOnlyList<DiscoveredWebApp>>(found);
    }

    private async Task<Result<(string FullWs, List<PlannedFileCopy> Ops, int SkippedIdentical, List<WorkspaceCopyConflict> Conflicts)>> BuildOperationsAsync(
        string workspacePath,
        IReadOnlyList<string> selectedAppFileNames,
        IReadOnlyList<StagedCopyItem> stagedItems,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>)>.Failure(
                WrapperErrors.InvalidWorkspaceDir(workspacePath));
        }

        string fullWs;
        try
        {
            fullWs = Path.GetFullPath(workspacePath.Trim());
        }
        catch (Exception ex)
        {
            return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>)>.Failure(
                WrapperErrors.InvalidWorkspaceDir($"{workspacePath} ({ex.Message})"));
        }

        var ops = new List<PlannedFileCopy>();
        var conflicts = new List<WorkspaceCopyConflict>();
        var skippedIdentical = 0;

        foreach (var appName in selectedAppFileNames.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var safeName = Path.GetFileName(appName);
            var srcExe = Path.Combine(dataPaths.AppsDirectory, safeName);
            if (!File.Exists(srcExe))
            {
                return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>)>.Failure(
                    WrapperErrors.CliNotFound(safeName));
            }

            var destExe = Path.Combine(fullWs, safeName);
            await ClassifyCopyAsync(srcExe, destExe, safeName, StagedCopyKind.File, true, null, ops, conflicts, ct, v => skippedIdentical += v).ConfigureAwait(false);
        }

        foreach (var item in stagedItems)
        {
            if (string.IsNullOrWhiteSpace(item.SourcePath))
            {
                continue;
            }

            string fullSrc;
            try
            {
                fullSrc = Path.GetFullPath(item.SourcePath.Trim());
            }
            catch
            {
                return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>)>.Failure(
                    WrapperErrors.InvalidPath(item.SourcePath));
            }
            if (item.Kind == StagedCopyKind.File)
            {
                if (!File.Exists(fullSrc))
                {
                    return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>)>.Failure(
                        WrapperErrors.InvalidPath($"Staged file does not exist: {fullSrc}"));
                }

                var rel = Path.GetFileName(fullSrc);
                var dest = Path.Combine(fullWs, rel);
                await ClassifyCopyAsync(fullSrc, dest, rel, StagedCopyKind.File, false, null, ops, conflicts, ct, v => skippedIdentical += v).ConfigureAwait(false);
            }
            else
            {
                if (!Directory.Exists(fullSrc))
                {
                    return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>)>.Failure(
                        WrapperErrors.InvalidPath($"Staged folder does not exist: {fullSrc}"));
                }

                var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullSrc));
                var destFolderRoot = Path.Combine(fullWs, folderName);

                if (IsSameOrDescendant(fullSrc, destFolderRoot) || IsSameOrDescendant(fullSrc, fullWs))
                {
                    return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>)>.Failure(
                        WrapperErrors.RecursiveCopyBlocked(fullSrc, fullWs));
                }

                foreach (var srcFile in Directory.GetFiles(fullSrc, "*", SearchOption.AllDirectories))
                {
                    var relInsideFolder = Path.GetRelativePath(fullSrc, srcFile);
                    var relInWs = Path.Combine(folderName, relInsideFolder).Replace('\\', '/');
                    var destFile = Path.Combine(destFolderRoot, relInsideFolder);
                    await ClassifyCopyAsync(srcFile, destFile, relInWs, StagedCopyKind.Folder, false, folderName, ops, conflicts, ct, v => skippedIdentical += v).ConfigureAwait(false);
                }
            }
        }

        return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>)>.Success((fullWs, ops, skippedIdentical, conflicts));
    }

    private static async Task ClassifyCopyAsync(
        string src,
        string dest,
        string relTarget,
        StagedCopyKind kind,
        bool isExe,
        string? rootFolderName,
        List<PlannedFileCopy> ops,
        List<WorkspaceCopyConflict> conflicts,
        CancellationToken ct,
        Action<int> addIdenticalSkip)
    {
        if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
        {
            addIdenticalSkip(1);
            return;
        }

        if (File.Exists(dest))
        {
            if (await AreFilesIdenticalAsync(src, dest, ct).ConfigureAwait(false))
            {
                addIdenticalSkip(1);
                return;
            }

            conflicts.Add(new WorkspaceCopyConflict(src, dest, relTarget, kind));
        }

        ops.Add(new PlannedFileCopy(src, dest, relTarget, kind, isExe, rootFolderName));
    }

    private static async Task<bool> AreFilesIdenticalAsync(string fileA, string fileB, CancellationToken ct)
    {
        var infoA = new FileInfo(fileA);
        var infoB = new FileInfo(fileB);
        if (infoA.Length != infoB.Length)
        {
            return false;
        }

        await using var streamA = File.OpenRead(fileA);
        await using var streamB = File.OpenRead(fileB);
        var hashA = await SHA256.HashDataAsync(streamA, ct).ConfigureAwait(false);
        var hashB = await SHA256.HashDataAsync(streamB, ct).ConfigureAwait(false);
        return hashA.AsSpan().SequenceEqual(hashB);
    }

    private static bool IsSameOrDescendant(string parentCandidate, string childCandidate)
    {
        var parentNorm = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentCandidate));
        var childNorm = Path.TrimEndingDirectorySeparator(Path.GetFullPath(childCandidate));
        if (string.Equals(parentNorm, childNorm, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return childNorm.StartsWith(parentNorm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void RecordWorkspaceToolPath(string fullPath, ref string? wsMbyPath, ref string? wsMelomgPath)
    {
        var name = Path.GetFileName(fullPath);
        if (ManagedAppsStore.IsMbyFileName(name))
        {
            wsMbyPath = fullPath;
        }
        else if (ManagedAppsStore.IsMelomgFileName(name))
        {
            wsMelomgPath = fullPath;
        }
    }

    private static bool IsCompiledWebApp(string dir) =>
        File.Exists(Path.Combine(dir, "index.html")) && File.Exists(Path.Combine(dir, "app.js"));
}
