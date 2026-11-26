using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Snapshot of managed apps and workspace-present executables for a workspace.</summary>
internal sealed record WorkspaceAppsSnapshot(
    List<string> WorkspaceApps,
    HashSet<string> InstalledNames,
    List<string> InstalledInWorkspace);

/// <summary>Shared helper for workspace executable rescans and ActiveWorkspaceState synchronization.</summary>
internal static class WorkspaceStateSync
{
    public static async Task<WorkspaceAppsSnapshot> ScanAppsSnapshotAsync(
        IManagedAppsStore appsStore,
        string workspacePath,
        CancellationToken ct)
    {
        var scanned = await appsStore.ScanAppsAsync(workspacePath, ct).ConfigureAwait(false);
        var workspaceApps = scanned
            .Where(a => a.IsPresentInWorkspace)
            .Select(a => a.WorkspaceFileName ?? a.FileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var installedNames = scanned
            .Where(a => a.IsInstalled)
            .Select(a => a.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installedInWorkspace = scanned
            .Where(a => a.IsInstalled && a.IsPresentInWorkspace)
            .Select(a => a.FileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WorkspaceAppsSnapshot(workspaceApps, installedNames, installedInWorkspace);
    }

    public static List<string> PrependRecentWorkspace(
        IReadOnlyList<string>? existing,
        string fullPath,
        int maxCount = 10) =>
        (existing ?? [])
            .Where(p => !PathNormalization.AreSameDirectory(p, fullPath))
            .Prepend(fullPath)
            .Take(maxCount)
            .ToList();

    public static async Task<Result<ActiveWorkspaceState?>> SyncActiveWorkspaceIfMatchingAsync(
        ISettingsStore settingsStore,
        IManagedAppsStore appsStore,
        WrapperSettings loadedSettings,
        string targetWorkspacePath,
        Func<ActiveWorkspaceState, WorkspaceAppsSnapshot, ActiveWorkspaceState> updater,
        CancellationToken ct)
    {
        if (loadedSettings.ActiveWorkspace is null ||
            !PathNormalization.AreSameDirectory(loadedSettings.ActiveWorkspace.WorkspacePath, targetWorkspacePath))
        {
            return Result<ActiveWorkspaceState?>.Success(loadedSettings.ActiveWorkspace);
        }

        var snapshot = await ScanAppsSnapshotAsync(appsStore, targetWorkspacePath, ct).ConfigureAwait(false);
        ActiveWorkspaceState? updatedWs = null;

        var updateRes = await settingsStore.UpdateAsync(current =>
        {
            if (current.ActiveWorkspace is null ||
                !PathNormalization.AreSameDirectory(current.ActiveWorkspace.WorkspacePath, targetWorkspacePath))
            {
                updatedWs = current.ActiveWorkspace;
                return current;
            }

            updatedWs = updater(current.ActiveWorkspace, snapshot);
            return current with { ActiveWorkspace = updatedWs };
        }, ct).ConfigureAwait(false);

        return updateRes.IsSuccess
            ? Result<ActiveWorkspaceState?>.Success(updatedWs)
            : Result<ActiveWorkspaceState?>.Failure(updateRes.Error!);
    }
}
