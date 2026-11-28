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
    ISettingsStore settingsStore,
    IManagedAppsStore? appsStore = null,
    IMbyCliClient? mbyClient = null)
{
    public async Task<Result<(string SlotId, string Url, WebAppLibraryEntry Entry)>> OpenWebAppAsync(string folderPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return Result<(string, string, WebAppLibraryEntry)>.Failure(WrapperErrors.InvalidWebAppDir(folderPath));
        }

        if (!PathNormalization.TryNormalizeDirectoryPath(folderPath, out var fullDir))
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

        var name = Path.GetFileName(fullDir);
        var entry = new WebAppLibraryEntry(
            Path: fullDir,
            Name: string.IsNullOrWhiteSpace(name) ? fullDir : name,
            LastOpenedUtc: DateTimeOffset.UtcNow,
            ReportCount: analysisStore.CountReports(fullDir));

        await settingsStore.UpdateAsync(current =>
        {
            var updatedRecents = (current.RecentWebApps ?? [])
                .Where(r => !PathNormalization.AreSameDirectory(r.Path, fullDir))
                .Prepend(entry)
                .Take(20)
                .ToList();
            return current with { RecentWebApps = updatedRecents };
        }, ct).ConfigureAwait(false);

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

        if (appsStore is not null && mbyClient is not null)
        {
            var mbyExe = await appsStore.ResolveToolExecutableAsync("mby", wsPath, settings.MbyExecutableOverride, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(mbyExe))
            {
                var verRes = await mbyClient.GetVersionInfoAsync(mbyExe, ct).ConfigureAwait(false);
                if (verRes.IsSuccess && verRes.Value.IsCompatible)
                {
                    var cliRes = await mbyClient.ListWebAppsAsync(mbyExe, wsPath, ct).ConfigureAwait(false);
                    if (cliRes.IsSuccess)
                    {
                        return cliRes.Value;
                    }
                }
            }
        }

        return await workspaceInitializer.DiscoverWebAppsAsync(wsPath, ct).ConfigureAwait(false);
    }
}
