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
        var wsPath = await ResolveWorkspacePathAsync(workspacePathOverride, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(wsPath))
        {
            return Result<WorkspaceResetPlan>.Failure(WrapperErrors.InvalidWorkspaceDir("No Active Workspace selected."));
        }

        return await maintenance.PlanResetAsync(wsPath, scope, ct).ConfigureAwait(false);
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

        var res = await maintenance.ExecuteResetAsync(wsPath, scope, ct).ConfigureAwait(false);
        if (res.IsFailure)
        {
            return Result<(WorkspaceResetResult, ActiveWorkspaceState?)>.Failure(res.Error!);
        }

        ActiveWorkspaceState? updatedWs = settings.ActiveWorkspace;
        if (updatedWs is not null && PathNormalization.AreSameDirectory(updatedWs.WorkspacePath, wsPath))
        {
            var scanned = await appsStore.ScanAppsAsync(updatedWs.WorkspacePath, ct).ConfigureAwait(false);
            var remainingApps = scanned.Where(a => a.IsPresentInWorkspace).Select(a => a.WorkspaceFileName ?? a.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var remainingInstalled = scanned.Where(a => a.IsInstalled && a.IsPresentInWorkspace).Select(a => a.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            updatedWs = updatedWs with
            {
                AgentsMdExportedUtc = res.Value.RemovedAgentsMd ? null : updatedWs.AgentsMdExportedUtc,
                WorkspaceApps = remainingApps,
                SelectedAppFileNames = remainingInstalled
            };
            await settingsStore.SaveAsync(settings with { ActiveWorkspace = updatedWs }, ct).ConfigureAwait(false);
        }

        return Result<(WorkspaceResetResult, ActiveWorkspaceState?)>.Success((res.Value, updatedWs));
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

        ActiveWorkspaceState? updatedWs = settings.ActiveWorkspace;
        if (updatedWs is not null && PathNormalization.AreSameDirectory(updatedWs.WorkspacePath, wsPath))
        {
            var scanned = await appsStore.ScanAppsAsync(updatedWs.WorkspacePath, ct).ConfigureAwait(false);
            var currentApps = scanned.Where(a => a.IsPresentInWorkspace).Select(a => a.WorkspaceFileName ?? a.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var currentInstalled = scanned.Where(a => a.IsInstalled && a.IsPresentInWorkspace).Select(a => a.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            updatedWs = updatedWs with
            {
                WorkspaceApps = currentApps,
                SelectedAppFileNames = currentInstalled
            };
            await settingsStore.SaveAsync(settings with { ActiveWorkspace = updatedWs }, ct).ConfigureAwait(false);
        }

        return Result<(WorkspaceToolUpdateResult, ActiveWorkspaceState?)>.Success((res.Value, updatedWs));
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
