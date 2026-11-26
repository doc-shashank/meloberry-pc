using System.Net;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;

namespace Meloberry.Wrapper.Infrastructure.Server.Endpoints;

/// <summary>Handles `/api/apps/*` routes for listing, downloading, updating, uninstalling, and importing CLI apps.</summary>
public sealed class AppsEndpoints(
    IWrapperDataPaths dataPaths,
    INativeDialogService dialogs,
    IWorkspaceFileWatcher fileWatcher,
    ManagedAppsHandler managedApps)
{
    public async Task<bool> TryHandleAsync(
        HttpListenerRequest req,
        HttpListenerResponse resp,
        string method,
        string path,
        CancellationToken ct)
    {
        switch (path, method)
        {
            case ("/api/apps", "GET"):
            {
                var apps = await managedApps.ListAppsAsync(ct).ConfigureAwait(false);
                var resolvedTools = await managedApps.ResolveToolsAsync(ct: ct).ConfigureAwait(false);
                await HttpEnvelope.WriteJsonAsync(resp, 200, new { appsDirectory = dataPaths.AppsDirectory, apps, resolvedTools }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/apps/download", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<ToolKeyRequest>(req, ct).ConfigureAwait(false);
                using var _ = fileWatcher.SuppressScope("apps");
                var res = await managedApps.DownloadToolAsync(body?.Tool ?? string.Empty, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/apps/download-status", "GET"):
            {
                var tool = req.QueryString["tool"] ?? "mby";
                var snap = managedApps.GetDownloadStatus(tool);
                await HttpEnvelope.WriteJsonAsync(resp, 200, new { status = snap }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/apps/check-update", "GET"):
            {
                var tool = req.QueryString["tool"] ?? string.Empty;
                var res = await managedApps.CheckToolUpdateAsync(tool, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/apps/uninstall", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<UninstallAppRequest>(req, ct).ConfigureAwait(false);
                using var _ = fileWatcher.SuppressScope("apps");
                var res = await managedApps.UninstallAppAsync(body?.IdOrFileName ?? string.Empty, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/apps/import", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<PathRequest>(req, ct).ConfigureAwait(false);
                var source = body?.Path;
                if (string.IsNullOrWhiteSpace(source))
                {
                    var picked = await dialogs.PickFilesAsync("Select Executable (.exe) to Import into apps\\", "Executable Files (*.exe)", "*.exe", false, null, ct).ConfigureAwait(false);
                    if (picked.Count == 0)
                    {
                        await HttpEnvelope.WriteJsonAsync(resp, 200, new { cancelled = true }, ct).ConfigureAwait(false);
                        return true;
                    }

                    source = picked[0];
                }

                using var _ = fileWatcher.SuppressScope("apps");
                var res = await managedApps.ImportLocalExecutableAsync(source, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            default:
                return false;
        }
    }
}
