using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Opens WebApp folders, discovers Active Workspace WebApps, and manages recent history.</summary>
public sealed class ManageLibraryHandler(
    IWebAppMountRegistry mountRegistry,
    IWebAppAnalysisStore analysisStore,
    IWorkspaceInitializer workspaceInitializer,
    ISettingsStore settingsStore)
{
    public async Task<Result<(string SlotId, string Url, WebAppLibraryEntry Entry)>> OpenWebAppAsync(string folderPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return Result<(string, string, WebAppLibraryEntry)>.Failure(WrapperErrors.InvalidWebAppDir(folderPath));
        }

        string fullDir;
        try
        {
            fullDir = Path.GetFullPath(folderPath.Trim());
        }
        catch
        {
            return Result<(string, string, WebAppLibraryEntry)>.Failure(WrapperErrors.InvalidWebAppDir(folderPath));
        }

        if (!Directory.Exists(fullDir) ||
            !File.Exists(Path.Combine(fullDir, "index.html")) ||
            !File.Exists(Path.Combine(fullDir, "app.js")))
        {
            return Result<(string, string, WebAppLibraryEntry)>.Failure(WrapperErrors.InvalidWebAppDir(fullDir));
        }

        var mountRes = mountRegistry.Mount(fullDir);
        if (mountRes.IsFailure)
        {
            return Result<(string, string, WebAppLibraryEntry)>.Failure(mountRes.Error!);
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullDir));
        var entry = new WebAppLibraryEntry(
            Path: fullDir,
            Name: string.IsNullOrWhiteSpace(name) ? fullDir : name,
            LastOpenedUtc: DateTimeOffset.UtcNow,
            ReportCount: analysisStore.CountReports(fullDir));

        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var updatedRecents = (settings.RecentWebApps ?? [])
            .Where(r => !string.Equals(r.Path, fullDir, StringComparison.OrdinalIgnoreCase))
            .Prepend(entry)
            .Take(20)
            .ToList();

        await settingsStore.SaveAsync(settings with { RecentWebApps = updatedRecents }, ct).ConfigureAwait(false);

        return Result<(string, string, WebAppLibraryEntry)>.Success((mountRes.Value, $"/viewer/{mountRes.Value}/index.html", entry));
    }

    public async Task<IReadOnlyList<DiscoveredWebApp>> DiscoverActiveWorkspaceWebAppsAsync(CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var wsPath = settings.ActiveWorkspace?.WorkspacePath;
        if (string.IsNullOrWhiteSpace(wsPath) || !Directory.Exists(wsPath))
        {
            return [];
        }

        return await workspaceInitializer.DiscoverWebAppsAsync(wsPath, ct).ConfigureAwait(false);
    }
}

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

        var baseOut = !string.IsNullOrWhiteSpace(outputDir)
            ? outputDir!
            : !string.IsNullOrWhiteSpace(settings.DefaultBuildOutputDir)
                ? settings.DefaultBuildOutputDir!
                : !string.IsNullOrWhiteSpace(wsDir)
                    ? Path.Combine(wsDir, $"{workspaceId}_web")
                    : Path.Combine(Path.GetTempPath(), "MeloberryBuilds", $"{workspaceId}_web");

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

/// <summary>Captures POST /api/analysis payloads emitted by mounted mby WebApps.</summary>
public sealed class CaptureAnalysisHandler(
    IWebAppMountRegistry mountRegistry,
    IWebAppAnalysisStore analysisStore)
{
    public async Task<Result<string>> CaptureAsync(string? slotId, string? reportJson, string? markdown, CancellationToken ct = default)
    {
        var targetSlot = !string.IsNullOrWhiteSpace(slotId) ? slotId : mountRegistry.ActiveSlotId;
        if (string.IsNullOrWhiteSpace(targetSlot) || !mountRegistry.TryResolve(targetSlot, out var webDir))
        {
            return Result<string>.Failure(WrapperErrors.InvalidWebAppDir("No active WebApp mounted."));
        }

        return await analysisStore.SaveAnalysisAsync(webDir, reportJson, markdown, ct).ConfigureAwait(false);
    }
}
