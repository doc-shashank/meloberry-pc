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

        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var recent = (settings.RecentWorkspaces ?? [])
            .Where(p => !PathNormalization.AreSameDirectory(p, fullPath))
            .Prepend(fullPath)
            .Take(10)
            .ToList();

        var isSameWorkspace = PathNormalization.AreSameDirectory(settings.ActiveWorkspace?.WorkspacePath, fullPath);
        var scanned = await appsStore.ScanAppsAsync(fullPath, ct).ConfigureAwait(false);
        var existingAppsInWs = scanned
            .Where(a => a.IsPresentInWorkspace)
            .Select(a => a.WorkspaceFileName ?? a.FileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var installedNames = scanned
            .Where(a => a.IsInstalled)
            .Select(a => a.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installedInWs = scanned
            .Where(a => a.IsInstalled && a.IsPresentInWorkspace)
            .Select(a => a.FileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var selectedApps = isSameWorkspace && settings.ActiveWorkspace?.SelectedAppFileNames is { } prevSelected
            ? prevSelected.Where(n => installedNames.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : installedInWs;

        var state = new ActiveWorkspaceState(
            WorkspacePath: fullPath,
            IsInitialized: isSameWorkspace && settings.ActiveWorkspace!.IsInitialized,
            LastInitializedUtc: isSameWorkspace ? settings.ActiveWorkspace?.LastInitializedUtc : null,
            AgentsMdExportedUtc: isSameWorkspace ? settings.ActiveWorkspace?.AgentsMdExportedUtc : null,
            SelectedAppFileNames: selectedApps,
            StagedItems: isSameWorkspace ? settings.ActiveWorkspace?.StagedItems : [],
            LastCopiedRelativePaths: isSameWorkspace ? settings.ActiveWorkspace?.LastCopiedRelativePaths : [],
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
        var fullPath = PathNormalization.NormalizeDirectoryPath(result.WorkspacePath);
        var recent = (settings.RecentWorkspaces ?? [])
            .Where(p => !PathNormalization.AreSameDirectory(p, fullPath))
            .Prepend(fullPath)
            .Take(10)
            .ToList();

        var existingAgentsMd = File.Exists(Path.Combine(fullPath, "AGENTS.md"));
        var prevAgentsUtc = PathNormalization.AreSameDirectory(settings.ActiveWorkspace?.WorkspacePath, fullPath) && existingAgentsMd
            ? settings.ActiveWorkspace?.AgentsMdExportedUtc
            : null;

        var scanned = await appsStore.ScanAppsAsync(fullPath, ct).ConfigureAwait(false);
        var workspaceApps = scanned
            .Where(a => a.IsPresentInWorkspace)
            .Select(a => a.WorkspaceFileName ?? a.FileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var installedNames = scanned
            .Where(a => a.IsInstalled)
            .Select(a => a.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var state = new ActiveWorkspaceState(
            WorkspacePath: fullPath,
            IsInitialized: true,
            LastInitializedUtc: DateTimeOffset.UtcNow,
            AgentsMdExportedUtc: prevAgentsUtc,
            SelectedAppFileNames: selectedAppFileNames.Where(n => installedNames.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
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
}
