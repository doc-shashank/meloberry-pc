using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Infrastructure.Server.Endpoints;

/// <summary>Handles bootstrap, viewer open, self-update, native dialogs, settings, external URLs, and `/api/analysis`.</summary>
public sealed partial class SystemEndpoints(
    IWrapperMetadataProvider metadataProvider,
    IWrapperDataPaths dataPaths,
    ISettingsStore settingsStore,
    INativeDialogService dialogs,
    IExternalUrlLauncher externalLauncher,
    IWorkspaceFileWatcher fileWatcher,
    ManagedAppsHandler managedApps,
    WorkspaceMaintenanceHandler workspaceMaintenance,
    ReadyWorkspaceHandler readyWorkspace,
    ManageLibraryHandler libraryHandler,
    CaptureAnalysisHandler analysisHandler,
    SelfUpdateHandler selfUpdate,
    SettingsAndPresetsHandler settingsPresets)
{
    [GeneratedRegex(@"/viewer/([A-Za-z0-9]+)/", RegexOptions.CultureInvariant)]
    private static partial Regex ViewerRefererRegex();

    public async Task<bool> TryHandleAsync(
        HttpListenerRequest req,
        HttpListenerResponse resp,
        string method,
        string path,
        string viewerBaseUrl,
        CancellationToken ct)
    {
        switch (path, method)
        {
            case ("/api/bootstrap", "GET"):
            {
                var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
                var apps = await managedApps.ListAppsAsync(ct).ConfigureAwait(false);
                var resolvedTools = await managedApps.ResolveToolsAsync(settings.ActiveWorkspace?.WorkspacePath, ct).ConfigureAwait(false);
                var meta = metadataProvider.GetMetadata();
                AgentsMdStatus? agentsStatus = null;
                IReadOnlyList<OutdatedWorkspaceTool> outdatedTools = [];
                if (!string.IsNullOrWhiteSpace(settings.ActiveWorkspace?.WorkspacePath))
                {
                    fileWatcher.UpdateActiveWorkspace(settings.ActiveWorkspace.WorkspacePath);
                    var st = await readyWorkspace.GetStatusAsync(settings.ActiveWorkspace.WorkspacePath, targetDirectoryOverride: null, ct: ct).ConfigureAwait(false);
                    if (st.IsSuccess)
                    {
                        agentsStatus = st.Value;
                    }

                    outdatedTools = await workspaceMaintenance.DetectOutdatedToolsAsync(settings.ActiveWorkspace.WorkspacePath, ct).ConfigureAwait(false);
                }

                await HttpEnvelope.WriteJsonAsync(resp, 200, new
                {
                    metadata = meta,
                    appsDirectory = dataPaths.AppsDirectory,
                    viewerOrigin = viewerBaseUrl.TrimEnd('/'),
                    settings,
                    apps,
                    resolvedTools,
                    paperThemeFallbackCss = StaticAssetHost.PaperThemeFallbackCss,
                    agentsMdStatus = agentsStatus,
                    outdatedTools,
                    ecosystemRepos = EcosystemCatalog.Repositories,
                    builtInPresets = BuiltInPresets.All
                }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/viewer/open", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<PathRequest>(req, ct).ConfigureAwait(false);
                var folder = body?.Path;
                if (string.IsNullOrWhiteSpace(folder))
                {
                    var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
                    folder = await dialogs.PickFolderAsync("Select Compiled Meloberry WebApp Folder", settings.ActiveWorkspace?.WorkspacePath, ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(folder))
                    {
                        await HttpEnvelope.WriteJsonAsync(resp, 200, new { cancelled = true }, ct).ConfigureAwait(false);
                        return true;
                    }
                }

                var res = await libraryHandler.OpenWebAppAsync(folder, ct).ConfigureAwait(false);
                if (res.IsFailure)
                {
                    await HttpEnvelope.WriteErrorAsync(resp, res.Error!, ct).ConfigureAwait(false);
                    return true;
                }

                await HttpEnvelope.WriteJsonAsync(resp, 200, new
                {
                    slotId = res.Value.SlotId,
                    url = $"{viewerBaseUrl.TrimEnd('/')}{res.Value.Url}",
                    entry = res.Value.Entry
                }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/self-update/check", "POST"):
            {
                var res = await selfUpdate.CheckAsync(ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/self-update/install", "POST"):
            {
                var res = await selfUpdate.DownloadAndInstallAsync(ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/self-update/status", "GET"):
            {
                await HttpEnvelope.WriteJsonAsync(resp, 200, new { status = selfUpdate.GetStatus() }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/dialogs/pick-folder", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<DialogFolderRequest>(req, ct).ConfigureAwait(false);
                var folder = await dialogs.PickFolderAsync(body?.Title ?? "Select Folder", body?.InitialDir, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteJsonAsync(resp, 200, new { path = folder, cancelled = string.IsNullOrWhiteSpace(folder) }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/dialogs/pick-files", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<DialogFilesRequest>(req, ct).ConfigureAwait(false);
                var files = await dialogs.PickFilesAsync(
                    body?.Title ?? "Select Files",
                    body?.FilterLabel ?? "All Study Files (*.pdf;*.md;*.txt;*.json;*.png;*.jpg)",
                    body?.FilterPattern ?? "*.*",
                    body?.AllowMultiple ?? true,
                    body?.InitialDir,
                    ct).ConfigureAwait(false);
                await HttpEnvelope.WriteJsonAsync(resp, 200, new { paths = files, cancelled = files.Count == 0 }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/shell/open-url", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<UrlRequest>(req, ct).ConfigureAwait(false);
                var res = externalLauncher.OpenAllowlistedUrl(body?.Url ?? string.Empty);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/settings", "GET"):
            {
                var settings = await settingsPresets.GetSettingsAsync(ct).ConfigureAwait(false);
                await HttpEnvelope.WriteJsonAsync(resp, 200, settings, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/settings", "PUT"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<UpdateSettingsApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null)
                {
                    await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid settings payload.", ct).ConfigureAwait(false);
                    return true;
                }

                var res = await settingsPresets.UpdateSettingsAsync(
                    body.ThemeMode,
                    body.MbyExecutableOverride,
                    body.MelomgExecutableOverride,
                    body.DefaultBuildOutputDir,
                    body.SelfUpdateRepoOwner,
                    body.SelfUpdateRepoName,
                    body.OverrideWebAppTheme,
                    ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/settings/override-theme", "PUT"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<OverrideThemeApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null)
                {
                    await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid override-theme payload.", ct).ConfigureAwait(false);
                    return true;
                }

                var res = await settingsPresets.UpdateOverrideWebAppThemeAsync(body.OverrideWebAppTheme, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            default:
                return false;
        }
    }

    public async Task HandleAnalysisPostAsync(HttpListenerRequest req, HttpListenerResponse resp, int viewerPort, CancellationToken ct)
    {
        var origin = req.Headers["Origin"];
        var referer = req.Headers["Referer"];
        var isOriginValid = false;

        if (!string.IsNullOrWhiteSpace(origin))
        {
            isOriginValid = Uri.TryCreate(origin, UriKind.Absolute, out var originUri) &&
                string.Equals(originUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(originUri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(originUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) &&
                originUri.Port == viewerPort;
        }
        else if (!string.IsNullOrWhiteSpace(referer))
        {
            isOriginValid = Uri.TryCreate(referer, UriKind.Absolute, out var refUri) &&
                string.Equals(refUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(refUri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(refUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) &&
                refUri.Port == viewerPort &&
                refUri.AbsolutePath.StartsWith("/viewer/", StringComparison.OrdinalIgnoreCase);
        }

        if (!isOriginValid)
        {
            await HttpEnvelope.WriteJsonErrorAsync(resp, 403, "Security.InvalidOrigin", "Cross-origin or untrusted /api/analysis denied.", ct).ConfigureAwait(false);
            return;
        }

        string? slotId = null;
        if (!string.IsNullOrWhiteSpace(referer))
        {
            var match = ViewerRefererRegex().Match(referer);
            if (match.Success)
            {
                slotId = match.Groups[1].Value;
            }
        }

        var (tooLarge, rawBody) = await HttpEnvelope.ReadBoundedUtf8BodyAsync(req, HttpEnvelope.MaxBodyBytes, ct).ConfigureAwait(false);
        if (tooLarge)
        {
            await HttpEnvelope.WriteJsonErrorAsync(resp, 413, "Request.PayloadTooLarge", "Analysis payload exceeds 10 MB limit.", ct).ConfigureAwait(false);
            return;
        }

        string? reportJson = null;
        string? markdown = null;

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            if (doc.RootElement.TryGetProperty("report", out var rep))
            {
                reportJson = JsonSerializer.Serialize(rep, new JsonSerializerOptions { WriteIndented = true });
            }

            if (doc.RootElement.TryGetProperty("markdown", out var md) && md.ValueKind == JsonValueKind.String)
            {
                markdown = md.GetString();
            }
        }
        catch
        {
            reportJson = rawBody;
        }

        var res = await analysisHandler.CaptureAsync(slotId, reportJson, markdown, ct).ConfigureAwait(false);
        await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
    }
}
