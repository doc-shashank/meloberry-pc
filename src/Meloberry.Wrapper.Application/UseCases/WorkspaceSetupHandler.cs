using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Orchestrates Active Workspace selection, copy pre-flight planning, and non-destructive initialization.</summary>
public sealed class WorkspaceSetupHandler(
    IWorkspaceInitializer initializer,
    IManagedAppsStore appsStore,
    ISettingsStore settingsStore,
    IWorkspaceDirectoryBrowser directoryBrowser,
    IWorkspaceShimWriter? shimWriter = null,
    IWorkspaceFileWatcher? fileWatcher = null)
{
    public async Task<Result<WorkspaceDirectoryListing>> ListDirectoryAsync(string? path, CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var activeWs = settings.ActiveWorkspace?.WorkspacePath;
        if (!PathNormalization.TryNormalizeDirectoryPath(activeWs, out var normalizedRoot))
        {
            return Result<WorkspaceDirectoryListing>.Failure(WrapperErrors.InvalidWorkspaceDir(string.Empty));
        }

        var target = string.IsNullOrWhiteSpace(path) ? normalizedRoot : path;
        if (!PathNormalization.IsSameOrDescendantDirectory(normalizedRoot, target))
        {
            return Result<WorkspaceDirectoryListing>.Failure(
                WrapperErrors.InvalidWorkspaceDir("Cannot browse outside the Active Workspace."));
        }

        var res = await directoryBrowser.ListDirectoryAsync(target, ct).ConfigureAwait(false);
        if (res.IsSuccess && PathNormalization.AreSameDirectory(res.Value.CurrentPath, normalizedRoot))
        {
            return Result<WorkspaceDirectoryListing>.Success(res.Value with { ParentPath = null });
        }

        return res;
    }
    public async Task<Result<ActiveWorkspaceState>> SelectWorkspaceAsync(string workspacePath, CancellationToken ct = default)
    {
        if (!PathNormalization.TryNormalizeDirectoryPath(workspacePath, out var fullPath))
        {
            return Result<ActiveWorkspaceState>.Failure(WrapperErrors.InvalidWorkspaceDir(workspacePath));
        }

        var priorSettings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var previousWs = priorSettings.ActiveWorkspace?.WorkspacePath;
        await SwapActiveWorkspaceShimAsync(previousWs, fullPath, writeIntoTarget: true, ct).ConfigureAwait(false);

        var snapshot = await WorkspaceStateSync.ScanAppsSnapshotAsync(appsStore, fullPath, ct).ConfigureAwait(false);

        ActiveWorkspaceState? resultingState = null;
        var updateRes = await settingsStore.UpdateAsync(current =>
        {
            var recent = WorkspaceStateSync.PrependRecentWorkspace(current.RecentWorkspaces, fullPath);

            var isSameWorkspace = PathNormalization.AreSameDirectory(current.ActiveWorkspace?.WorkspacePath, fullPath);
            var presentSet = snapshot.InstalledInWorkspace.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedApps = isSameWorkspace && current.ActiveWorkspace?.SelectedAppFileNames is { } prevSelected
                ? prevSelected
                    .Where(n => snapshot.InstalledNames.Contains(n) && !presentSet.Contains(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];

            var state = new ActiveWorkspaceState(
                WorkspacePath: fullPath,
                IsInitialized: true,
                LastInitializedUtc: isSameWorkspace
                    ? (current.ActiveWorkspace?.LastInitializedUtc ?? DateTimeOffset.UtcNow)
                    : DateTimeOffset.UtcNow,
                AgentsMdExportedUtc: isSameWorkspace ? current.ActiveWorkspace?.AgentsMdExportedUtc : null,
                SelectedAppFileNames: selectedApps,
                StagedItems: isSameWorkspace ? current.ActiveWorkspace?.StagedItems : [],
                LastCopiedRelativePaths: isSameWorkspace ? current.ActiveWorkspace?.LastCopiedRelativePaths : [],
                WorkspaceApps: snapshot.WorkspaceApps,
                LastAgentsMdPath: isSameWorkspace ? current.ActiveWorkspace?.LastAgentsMdPath : null,
                LastReadyWorkspaceArtifacts: isSameWorkspace ? current.ActiveWorkspace?.LastReadyWorkspaceArtifacts : null);

            resultingState = state;
            return current with
            {
                ActiveWorkspace = state,
                RecentWorkspaces = recent
            };
        }, ct).ConfigureAwait(false);

        return updateRes.IsSuccess
            ? Result<ActiveWorkspaceState>.Success(resultingState!)
            : Result<ActiveWorkspaceState>.Failure(updateRes.Error!);
    }

    public async Task<Result<WorkspaceInitPlan>> PlanInitializationAsync(
        string workspacePath,
        IReadOnlyList<string> selectedAppFileNames,
        IReadOnlyList<StagedCopyItem> stagedItems,
        CancellationToken ct = default)
    {
        return await initializer.PlanAsync(workspacePath, selectedAppFileNames, stagedItems, ct).ConfigureAwait(false);
    }

    public async Task<Result<(WorkspaceInitResult InitResult, ActiveWorkspaceState State)>> InitializeWorkspaceAsync(
        string workspacePath,
        IReadOnlyList<string> selectedAppFileNames,
        IReadOnlyList<StagedCopyItem> stagedItems,
        ConflictPolicy conflictPolicy,
        CancellationToken ct = default)
    {
        var priorSettings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var previousWs = priorSettings.ActiveWorkspace?.WorkspacePath;

        var initRes = await initializer.InitializeAsync(
            workspacePath,
            selectedAppFileNames,
            stagedItems,
            conflictPolicy,
            ct).ConfigureAwait(false);

        if (initRes.IsFailure)
        {
            return Result<(WorkspaceInitResult, ActiveWorkspaceState)>.Failure(initRes.Error!);
        }

        var result = initRes.Value;
        var fullPath = PathNormalization.NormalizeDirectoryPath(result.WorkspacePath);
        await SwapActiveWorkspaceShimAsync(previousWs, fullPath, writeIntoTarget: false, ct).ConfigureAwait(false);
        var existingAgentsMd = File.Exists(Path.Combine(fullPath, "AGENTS.md"));
        var snapshot = await WorkspaceStateSync.ScanAppsSnapshotAsync(appsStore, fullPath, ct).ConfigureAwait(false);

        ActiveWorkspaceState? resultingState = null;
        var updateRes = await settingsStore.UpdateAsync(current =>
        {
            var recent = WorkspaceStateSync.PrependRecentWorkspace(current.RecentWorkspaces, fullPath);

            var isSameWorkspace = PathNormalization.AreSameDirectory(current.ActiveWorkspace?.WorkspacePath, fullPath);
            var prevAgentsUtc = isSameWorkspace && existingAgentsMd
                ? current.ActiveWorkspace?.AgentsMdExportedUtc
                : null;

            var mergedCopied = (isSameWorkspace ? current.ActiveWorkspace?.LastCopiedRelativePaths : null) is { Count: > 0 } prevCopied
                ? prevCopied.Concat(result.CopiedRelativePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : result.CopiedRelativePaths;

            var state = new ActiveWorkspaceState(
                WorkspacePath: fullPath,
                IsInitialized: true,
                LastInitializedUtc: DateTimeOffset.UtcNow,
                AgentsMdExportedUtc: prevAgentsUtc,
                SelectedAppFileNames: [],
                StagedItems: [],
                LastCopiedRelativePaths: mergedCopied,
                WorkspaceApps: snapshot.WorkspaceApps,
                LastAgentsMdPath: isSameWorkspace ? current.ActiveWorkspace?.LastAgentsMdPath : null,
                LastReadyWorkspaceArtifacts: isSameWorkspace ? current.ActiveWorkspace?.LastReadyWorkspaceArtifacts : null);

            resultingState = state;
            return current with
            {
                ActiveWorkspace = state,
                RecentWorkspaces = recent
            };
        }, ct).ConfigureAwait(false);

        return updateRes.IsSuccess
            ? Result<(WorkspaceInitResult, ActiveWorkspaceState)>.Success((result, resultingState!))
            : Result<(WorkspaceInitResult, ActiveWorkspaceState)>.Failure(updateRes.Error!);
    }

    private async Task SwapActiveWorkspaceShimAsync(
        string? previousWorkspacePath,
        string targetWorkspacePath,
        bool writeIntoTarget,
        CancellationToken ct)
    {
        if (shimWriter is null)
        {
            return;
        }

        using var _ = fileWatcher?.SuppressScope("workspace", 1200);
        if (!string.IsNullOrWhiteSpace(previousWorkspacePath) &&
            !PathNormalization.AreSameDirectory(previousWorkspacePath, targetWorkspacePath))
        {
            await shimWriter.RemoveShimIfManagedAsync(previousWorkspacePath, ct).ConfigureAwait(false);
        }

        if (writeIntoTarget)
        {
            await shimWriter.WriteShimAsync(targetWorkspacePath, overwriteExisting: true, ct).ConfigureAwait(false);
        }
    }
}
