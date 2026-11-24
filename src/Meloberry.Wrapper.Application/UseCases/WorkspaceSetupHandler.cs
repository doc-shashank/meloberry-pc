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
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return Result<ActiveWorkspaceState>.Failure(WrapperErrors.InvalidWorkspaceDir(workspacePath));
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(workspacePath.Trim());
        }
        catch (Exception ex)
        {
            return Result<ActiveWorkspaceState>.Failure(WrapperErrors.InvalidWorkspaceDir($"{workspacePath} ({ex.Message})"));
        }

        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var recent = (settings.RecentWorkspaces ?? [])
            .Where(p => !string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase))
            .Prepend(fullPath)
            .Take(10)
            .ToList();

        var existingAppsInWs = await ScanWorkspaceExecutablesAsync(fullPath, ct).ConfigureAwait(false);
        var state = new ActiveWorkspaceState(
            WorkspacePath: fullPath,
            IsInitialized: settings.ActiveWorkspace is not null
                && string.Equals(settings.ActiveWorkspace.WorkspacePath, fullPath, StringComparison.OrdinalIgnoreCase)
                && settings.ActiveWorkspace.IsInitialized,
            LastInitializedUtc: string.Equals(settings.ActiveWorkspace?.WorkspacePath, fullPath, StringComparison.OrdinalIgnoreCase)
                ? settings.ActiveWorkspace?.LastInitializedUtc
                : null,
            AgentsMdExportedUtc: string.Equals(settings.ActiveWorkspace?.WorkspacePath, fullPath, StringComparison.OrdinalIgnoreCase)
                ? settings.ActiveWorkspace?.AgentsMdExportedUtc
                : null,
            SelectedAppFileNames: settings.ActiveWorkspace?.SelectedAppFileNames ?? existingAppsInWs,
            StagedItems: string.Equals(settings.ActiveWorkspace?.WorkspacePath, fullPath, StringComparison.OrdinalIgnoreCase)
                ? settings.ActiveWorkspace?.StagedItems
                : [],
            LastCopiedRelativePaths: string.Equals(settings.ActiveWorkspace?.WorkspacePath, fullPath, StringComparison.OrdinalIgnoreCase)
                ? settings.ActiveWorkspace?.LastCopiedRelativePaths
                : [],
            WorkspaceApps: existingAppsInWs);

        var updated = settings with
        {
            ActiveWorkspace = state,
            RecentWorkspaces = recent
        };
        var saveRes = await settingsStore.SaveAsync(updated, ct).ConfigureAwait(false);
        return saveRes.IsSuccess
            ? Result<ActiveWorkspaceState>.Success(state)
            : Result<ActiveWorkspaceState>.Failure(saveRes.Error!);
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
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var fullPath = Path.GetFullPath(result.WorkspacePath);
        var recent = (settings.RecentWorkspaces ?? [])
            .Where(p => !string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase))
            .Prepend(fullPath)
            .Take(10)
            .ToList();

        var existingAgentsMd = File.Exists(Path.Combine(fullPath, "AGENTS.md"));
        var prevAgentsUtc = string.Equals(settings.ActiveWorkspace?.WorkspacePath, fullPath, StringComparison.OrdinalIgnoreCase) && existingAgentsMd
            ? settings.ActiveWorkspace?.AgentsMdExportedUtc
            : null;

        var workspaceApps = await ScanWorkspaceExecutablesAsync(fullPath, ct).ConfigureAwait(false);
        var state = new ActiveWorkspaceState(
            WorkspacePath: fullPath,
            IsInitialized: true,
            LastInitializedUtc: DateTimeOffset.UtcNow,
            AgentsMdExportedUtc: prevAgentsUtc,
            SelectedAppFileNames: selectedAppFileNames.ToList(),
            StagedItems: stagedItems.ToList(),
            LastCopiedRelativePaths: result.CopiedRelativePaths,
            WorkspaceApps: workspaceApps);

        var updated = settings with
        {
            ActiveWorkspace = state,
            RecentWorkspaces = recent
        };
        var saveRes = await settingsStore.SaveAsync(updated, ct).ConfigureAwait(false);
        return saveRes.IsSuccess
            ? Result<(WorkspaceInitResult, ActiveWorkspaceState)>.Success((result, state))
            : Result<(WorkspaceInitResult, ActiveWorkspaceState)>.Failure(saveRes.Error!);
    }

    private async Task<IReadOnlyList<string>> ScanWorkspaceExecutablesAsync(string workspacePath, CancellationToken ct)
    {
        var scanned = await appsStore.ScanAppsAsync(workspacePath, ct).ConfigureAwait(false);
        return scanned.Where(a => a.IsPresentInWorkspace).Select(a => a.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
