using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Executes `mby ls`, `mby build`, and `mby pkg export` inside the Active Workspace.</summary>
public sealed class WorkspaceActionsHandler(
    ManagedAppsHandler managedApps,
    ManageLibraryHandler libraryHandler,
    IMbyCliClient mbyClient,
    ISettingsStore settingsStore)
{
    public async Task<Result<IReadOnlyList<WorkspaceSummary>>> ListMbyWorkspacesAsync(CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var wsDir = settings.ActiveWorkspace?.WorkspacePath;
        var mbyPath = await managedApps.ResolveExecutableAsync("mby", wsDir, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(mbyPath))
        {
            return Result<IReadOnlyList<WorkspaceSummary>>.Failure(WrapperErrors.CliNotFound("mby"));
        }

        return await mbyClient.ListWorkspacesAsync(mbyPath, wsDir, ct).ConfigureAwait(false);
    }

    public async Task<Result<(string SlotId, string Url, WebAppLibraryEntry Entry)>> BuildAndOpenAsync(
        string workspaceId,
        string? outputDir = null,
        CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var wsDir = settings.ActiveWorkspace?.WorkspacePath;
        var mbyPath = await managedApps.ResolveExecutableAsync("mby", wsDir, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(mbyPath))
        {
            return Result<(string, string, WebAppLibraryEntry)>.Failure(WrapperErrors.CliNotFound("mby"));
        }

        var safeWsFolder = $"{Path.GetFileName(workspaceId)}_web";
        var baseOut = !string.IsNullOrWhiteSpace(outputDir)
            ? outputDir!
            : !string.IsNullOrWhiteSpace(settings.DefaultBuildOutputDir)
                ? Path.Combine(settings.DefaultBuildOutputDir!, safeWsFolder)
                : !string.IsNullOrWhiteSpace(wsDir)
                    ? Path.Combine(wsDir, safeWsFolder)
                    : Path.Combine(Path.GetTempPath(), "MeloberryBuilds", safeWsFolder);

        var buildRes = await mbyClient.BuildWorkspaceAsync(mbyPath, workspaceId, baseOut, wsDir, ct).ConfigureAwait(false);
        if (buildRes.IsFailure)
        {
            return Result<(string, string, WebAppLibraryEntry)>.Failure(buildRes.Error!);
        }

        return await libraryHandler.OpenWebAppAsync(buildRes.Value, ct).ConfigureAwait(false);
    }

    public async Task<Result<string>> ExportPackageAsync(
        string workspaceId,
        string target,
        string outputPath,
        CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var wsDir = settings.ActiveWorkspace?.WorkspacePath;
        var mbyPath = await managedApps.ResolveExecutableAsync("mby", wsDir, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(mbyPath))
        {
            return Result<string>.Failure(WrapperErrors.CliNotFound("mby"));
        }

        return await mbyClient.ExportPackageAsync(mbyPath, workspaceId, target, outputPath, wsDir, ct).ConfigureAwait(false);
    }
}
