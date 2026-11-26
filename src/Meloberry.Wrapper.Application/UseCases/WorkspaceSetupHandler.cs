using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Orchestrates Active Workspace selection, copy pre-flight planning, and non-destructive initialization.</summary>
public sealed class WorkspaceSetupHandler(
    IWorkspaceInitializer initializer,
    IManagedAppsStore appsStore,
    ISettingsStore settingsStore)
{
    public async Task<Result<ActiveWorkspaceState>> SelectWorkspaceAsync(string workspacePath, CancellationToken ct = default)
    {
        if (!PathNormalization.TryNormalizeDirectoryPath(workspacePath, out var fullPath))
        {
            return Result<ActiveWorkspaceState>.Failure(WrapperErrors.InvalidWorkspaceDir(workspacePath));
        }

        var snapshot = await WorkspaceStateSync.ScanAppsSnapshotAsync(appsStore, fullPath, ct).ConfigureAwait(false);

        ActiveWorkspaceState? resultingState = null;
        var updateRes = await settingsStore.UpdateAsync(current =>
        {
            var recent = WorkspaceStateSync.PrependRecentWorkspace(current.RecentWorkspaces, fullPath);

            var isSameWorkspace = PathNormalization.AreSameDirectory(current.ActiveWorkspace?.WorkspacePath, fullPath);
            var selectedApps = isSameWorkspace && current.ActiveWorkspace?.SelectedAppFileNames is { } prevSelected
                ? prevSelected.Where(n => snapshot.InstalledNames.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : snapshot.InstalledInWorkspace;

            var state = new ActiveWorkspaceState(
                WorkspacePath: fullPath,
                IsInitialized: isSameWorkspace && current.ActiveWorkspace!.IsInitialized,
                LastInitializedUtc: isSameWorkspace ? current.ActiveWorkspace?.LastInitializedUtc : null,
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

            var state = new ActiveWorkspaceState(
                WorkspacePath: fullPath,
                IsInitialized: true,
                LastInitializedUtc: DateTimeOffset.UtcNow,
                AgentsMdExportedUtc: prevAgentsUtc,
                SelectedAppFileNames: selectedAppFileNames.Where(n => snapshot.InstalledNames.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StagedItems: stagedItems.ToList(),
                LastCopiedRelativePaths: result.CopiedRelativePaths,
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
}
