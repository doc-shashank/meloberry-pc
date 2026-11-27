using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.Common;

namespace Meloberry.Wrapper.Infrastructure.Workspace;

/// <summary>
/// Plans and executes non-destructive Active Workspace copying:
/// executables -&gt; root, files -&gt; root, folders -&gt; &lt;workspace&gt;/&lt;FolderName&gt;/...
/// </summary>
public sealed class WorkspaceInitializer(
    IWrapperDataPaths dataPaths,
    IWebAppAnalysisStore analysisStore,
    IWorkspaceShimWriter? shimWriter = null) : IWorkspaceInitializer
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

        var (fullWs, operations, skippedIdentical, conflicts, _) = planRes.Value;
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

        var (fullWs, operations, skippedIdentical, _, identicalSkippedRelativePaths) = planRes.Value;
        string? wsMbyPath = null;
        string? wsMelomgPath = null;
        var copiedApps = new List<string>();
        var copiedFiles = new List<string>();
        var copiedFolderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copiedRelativePaths = new List<string>(identicalSkippedRelativePaths);
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

                AtomicFileOps.CopyFile(op.SourcePath, op.DestinationPath, ".mbytmp");

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

        var shimWritten = false;
        string? workspaceShimPath = null;
        if (shimWriter is not null)
        {
            var shimRes = await shimWriter.WriteShimAsync(fullWs, overwriteExisting: true, ct).ConfigureAwait(false);
            if (shimRes.IsFailure)
            {
                return Result<WorkspaceInitResult>.Failure(shimRes.Error!);
            }

            shimWritten = true;
            workspaceShimPath = shimRes.Value;
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
            CopiedRelativePaths: copiedRelativePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ShimWritten: shimWritten,
            WorkspaceShimPath: workspaceShimPath));
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

    private async Task<Result<(string FullWs, List<PlannedFileCopy> Ops, int SkippedIdentical, List<WorkspaceCopyConflict> Conflicts, List<string> IdenticalSkippedRelativePaths)>> BuildOperationsAsync(
        string workspacePath,
        IReadOnlyList<string> selectedAppFileNames,
        IReadOnlyList<StagedCopyItem> stagedItems,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>, List<string>)>.Failure(
                WrapperErrors.InvalidWorkspaceDir(workspacePath));
        }

        string fullWs;
        try
        {
            fullWs = Path.GetFullPath(workspacePath.Trim());
        }
        catch (Exception ex)
        {
            return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>, List<string>)>.Failure(
                WrapperErrors.InvalidWorkspaceDir($"{workspacePath} ({ex.Message})"));
        }

        var ops = new List<PlannedFileCopy>();
        var plannedByDest = new Dictionary<string, PlannedFileCopy>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<WorkspaceCopyConflict>();
        var identicalSkippedRelativePaths = new List<string>();
        var skippedIdentical = 0;

        void RecordIdenticalSkip(int count, string relTarget)
        {
            skippedIdentical += count;
            identicalSkippedRelativePaths.Add(relTarget);
        }

        foreach (var appName in selectedAppFileNames.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var safeName = Path.GetFileName(appName);
            // mby and melomg execute centrally from apps\ via mby-pc.bat; never copy them into workspaces.
            if (ManagedAppsStore.IsMbyFileName(safeName) || ManagedAppsStore.IsMelomgFileName(safeName))
            {
                continue;
            }

            var srcExe = Path.Combine(dataPaths.AppsDirectory, safeName);
            if (!File.Exists(srcExe))
            {
                return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>, List<string>)>.Failure(
                    WrapperErrors.CliNotFound(safeName));
            }

            var destExe = Path.Combine(fullWs, safeName);
            await ClassifyCopyAsync(srcExe, destExe, safeName, StagedCopyKind.File, true, null, ops, plannedByDest, conflicts, ct, RecordIdenticalSkip).ConfigureAwait(false);
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
                return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>, List<string>)>.Failure(
                    WrapperErrors.InvalidPath(item.SourcePath));
            }

            if (item.Kind == StagedCopyKind.File)
            {
                if (!File.Exists(fullSrc))
                {
                    return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>, List<string>)>.Failure(
                        WrapperErrors.InvalidPath($"Staged file does not exist: {fullSrc}"));
                }

                var rel = Path.GetFileName(fullSrc);
                var dest = Path.Combine(fullWs, rel);
                await ClassifyCopyAsync(fullSrc, dest, rel, StagedCopyKind.File, false, null, ops, plannedByDest, conflicts, ct, RecordIdenticalSkip).ConfigureAwait(false);
            }
            else
            {
                if (!Directory.Exists(fullSrc))
                {
                    return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>, List<string>)>.Failure(
                        WrapperErrors.InvalidPath($"Staged folder does not exist: {fullSrc}"));
                }

                var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullSrc));
                if (string.IsNullOrWhiteSpace(folderName))
                {
                    return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>, List<string>)>.Failure(
                        WrapperErrors.InvalidPath($"Cannot stage a drive root directory: '{fullSrc}'."));
                }

                var destFolderRoot = Path.Combine(fullWs, folderName);
                if (IsSameOrDescendant(fullSrc, destFolderRoot) || IsSameOrDescendant(fullSrc, fullWs))
                {
                    return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>, List<string>)>.Failure(
                        WrapperErrors.RecursiveCopyBlocked(fullSrc, fullWs));
                }

                try
                {
                    var enumOpts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                    foreach (var srcFile in Directory.EnumerateFiles(fullSrc, "*", enumOpts))
                    {
                        var relInsideFolder = Path.GetRelativePath(fullSrc, srcFile);
                        var relInWs = Path.Combine(folderName, relInsideFolder).Replace('\\', '/');
                        var destFile = Path.Combine(destFolderRoot, relInsideFolder);
                        await ClassifyCopyAsync(srcFile, destFile, relInWs, StagedCopyKind.Folder, false, folderName, ops, plannedByDest, conflicts, ct, RecordIdenticalSkip).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>, List<string>)>.Failure(
                        WrapperErrors.IoFailure($"Failed to enumerate staged folder '{fullSrc}': {ex.Message}"));
                }
            }
        }

        return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>, List<string>)>.Success(
            (fullWs, ops, skippedIdentical, conflicts, identicalSkippedRelativePaths));
    }

    private static async Task ClassifyCopyAsync(
        string src,
        string dest,
        string relTarget,
        StagedCopyKind kind,
        bool isExe,
        string? rootFolderName,
        List<PlannedFileCopy> ops,
        Dictionary<string, PlannedFileCopy> plannedByDest,
        List<WorkspaceCopyConflict> conflicts,
        CancellationToken ct,
        Action<int, string> addIdenticalSkip)
    {
        var fullSrc = Path.GetFullPath(src);
        var fullDest = Path.GetFullPath(dest);
        if (string.Equals(fullSrc, fullDest, StringComparison.OrdinalIgnoreCase))
        {
            addIdenticalSkip(1, relTarget);
            return;
        }

        if (plannedByDest.TryGetValue(fullDest, out var priorOp))
        {
            if (await FileIdentity.AreIdenticalAsync(fullSrc, priorOp.SourcePath, ct).ConfigureAwait(false))
            {
                addIdenticalSkip(1, relTarget);
                return;
            }

            conflicts.Add(new WorkspaceCopyConflict(fullSrc, fullDest, relTarget, kind));
        }
        else if (File.Exists(fullDest))
        {
            if (await FileIdentity.AreIdenticalAsync(fullSrc, fullDest, ct).ConfigureAwait(false))
            {
                addIdenticalSkip(1, relTarget);
                return;
            }

            conflicts.Add(new WorkspaceCopyConflict(fullSrc, fullDest, relTarget, kind));
        }

        var planned = new PlannedFileCopy(fullSrc, fullDest, relTarget, kind, isExe, rootFolderName);
        plannedByDest[fullDest] = planned;
        ops.Add(planned);
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
