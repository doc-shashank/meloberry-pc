using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Handles recursive Active Workspace executable resets and outdated CLI updates against apps\.</summary>
public sealed class WorkspaceMaintenanceHandler(
    IWorkspaceMaintenanceService maintenance,
    IManagedAppsStore appsStore,
    ISettingsStore settingsStore)
{
    public async Task<Result<WorkspaceResetPlan>> PlanResetAsync(
        string? workspacePathOverride,
        WorkspaceResetScope scope,
        CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var wsPath = !string.IsNullOrWhiteSpace(workspacePathOverride)
            ? workspacePathOverride
            : settings.ActiveWorkspace?.WorkspacePath;
        if (string.IsNullOrWhiteSpace(wsPath))
        {
            return Result<WorkspaceResetPlan>.Failure(WrapperErrors.InvalidWorkspaceDir("No Active Workspace selected."));
        }

        var tracked = settings.ActiveWorkspace is not null && PathNormalization.AreSameDirectory(settings.ActiveWorkspace.WorkspacePath, wsPath)
            ? settings.ActiveWorkspace.LastReadyWorkspaceArtifacts
            : null;

        return await maintenance.PlanResetAsync(wsPath, scope, tracked, ct).ConfigureAwait(false);
    }

    public async Task<Result<(WorkspaceResetResult ResetResult, ActiveWorkspaceState? UpdatedWorkspace)>> ExecuteResetAsync(
        string? workspacePathOverride,
        WorkspaceResetScope scope,
        CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var wsPath = !string.IsNullOrWhiteSpace(workspacePathOverride)
            ? workspacePathOverride
            : settings.ActiveWorkspace?.WorkspacePath;

        if (string.IsNullOrWhiteSpace(wsPath))
        {
            return Result<(WorkspaceResetResult, ActiveWorkspaceState?)>.Failure(
                WrapperErrors.InvalidWorkspaceDir("No Active Workspace selected."));
        }

        var tracked = settings.ActiveWorkspace is not null && PathNormalization.AreSameDirectory(settings.ActiveWorkspace.WorkspacePath, wsPath)
            ? settings.ActiveWorkspace.LastReadyWorkspaceArtifacts
            : null;

        var res = await maintenance.ExecuteResetAsync(wsPath, scope, tracked, ct).ConfigureAwait(false);
        if (res.IsFailure)
        {
            return Result<(WorkspaceResetResult, ActiveWorkspaceState?)>.Failure(res.Error!);
        }

        var clearReadyState = scope == WorkspaceResetScope.UndoReadyWorkspace || res.Value.RemovedAgentsMd;
        var syncRes = await WorkspaceStateSync.SyncActiveWorkspaceIfMatchingAsync(
            settingsStore,
            appsStore,
            settings,
            wsPath,
            (activeWs, snapshot) => activeWs with
            {
                AgentsMdExportedUtc = clearReadyState ? null : activeWs.AgentsMdExportedUtc,
                LastAgentsMdPath = clearReadyState ? null : activeWs.LastAgentsMdPath,
                LastReadyWorkspaceArtifacts = clearReadyState ? null : activeWs.LastReadyWorkspaceArtifacts,
                WorkspaceApps = snapshot.WorkspaceApps,
                SelectedAppFileNames = snapshot.InstalledInWorkspace
            },
            ct).ConfigureAwait(false);

        return syncRes.IsSuccess
            ? Result<(WorkspaceResetResult, ActiveWorkspaceState?)>.Success((res.Value, syncRes.Value))
            : Result<(WorkspaceResetResult, ActiveWorkspaceState?)>.Failure(syncRes.Error!);
    }

    public async Task<IReadOnlyList<OutdatedWorkspaceTool>> DetectOutdatedToolsAsync(
        string? workspacePathOverride,
        CancellationToken ct = default)
    {
        var wsPath = await ResolveWorkspacePathAsync(workspacePathOverride, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(wsPath) || !Directory.Exists(wsPath))
        {
            return [];
        }

        var res = await maintenance.DetectOutdatedToolsAsync(wsPath, ct).ConfigureAwait(false);
        return res.IsSuccess ? res.Value : [];
    }

    public async Task<Result<(WorkspaceToolUpdateResult UpdateResult, ActiveWorkspaceState? UpdatedWorkspace)>> UpdateOutdatedToolsAsync(
        string? workspacePathOverride,
        CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var wsPath = !string.IsNullOrWhiteSpace(workspacePathOverride)
            ? workspacePathOverride
            : settings.ActiveWorkspace?.WorkspacePath;

        if (string.IsNullOrWhiteSpace(wsPath))
        {
            return Result<(WorkspaceToolUpdateResult, ActiveWorkspaceState?)>.Failure(
                WrapperErrors.InvalidWorkspaceDir("No Active Workspace selected."));
        }

        var res = await maintenance.UpdateOutdatedToolsAsync(wsPath, ct).ConfigureAwait(false);
        if (res.IsFailure)
        {
            return Result<(WorkspaceToolUpdateResult, ActiveWorkspaceState?)>.Failure(res.Error!);
        }

        var syncRes = await WorkspaceStateSync.SyncActiveWorkspaceIfMatchingAsync(
            settingsStore,
            appsStore,
            settings,
            wsPath,
            (activeWs, snapshot) => activeWs with
            {
                WorkspaceApps = snapshot.WorkspaceApps,
                SelectedAppFileNames = snapshot.InstalledInWorkspace
            },
            ct).ConfigureAwait(false);

        return syncRes.IsSuccess
            ? Result<(WorkspaceToolUpdateResult, ActiveWorkspaceState?)>.Success((res.Value, syncRes.Value))
            : Result<(WorkspaceToolUpdateResult, ActiveWorkspaceState?)>.Failure(syncRes.Error!);
    }

    private async Task<string?> ResolveWorkspacePathAsync(string? overridePath, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        return settings.ActiveWorkspace?.WorkspacePath;
    }
}
