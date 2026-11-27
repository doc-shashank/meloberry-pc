using System.Net;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Infrastructure.Server.Endpoints;

/// <summary>Handles `/api/workspace/*` and `/api/workspaces/*` routes for setup, maintenance, AGENTS.md, and builds.</summary>
public sealed class WorkspaceEndpoints(
    ISettingsStore settingsStore,
    INativeDialogService dialogs,
    IExternalUrlLauncher externalLauncher,
    IWorkspaceFileWatcher fileWatcher,
    WorkspaceSetupHandler workspaceSetup,
    WorkspaceMaintenanceHandler workspaceMaintenance,
    ReadyWorkspaceHandler readyWorkspace,
    ManageLibraryHandler libraryHandler,
    WorkspaceActionsHandler workspaceActions,
    ExtractFigureWithMelomgHandler extractFigure)
{
    private static readonly Dictionary<string, string> AssetMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml"
    };
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
            case ("/api/workspace/select", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<PathRequest>(req, ct).ConfigureAwait(false);
                var res = await workspaceSetup.SelectWorkspaceAsync(body?.Path ?? string.Empty, ct).ConfigureAwait(false);
                if (res.IsFailure)
                {
                    await HttpEnvelope.WriteErrorAsync(resp, res.Error!, ct).ConfigureAwait(false);
                    return true;
                }

                fileWatcher.UpdateActiveWorkspace(res.Value.WorkspacePath);
                var outdated = await workspaceMaintenance.DetectOutdatedToolsAsync(res.Value.WorkspacePath, ct).ConfigureAwait(false);
                var currentSettings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
                await HttpEnvelope.WriteJsonAsync(resp, 200, new
                {
                    state = res.Value,
                    outdatedTools = outdated,
                    recentWorkspaces = currentSettings.RecentWorkspaces ?? []
                }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/workspace/plan", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<WorkspaceInitApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null)
                {
                    await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid plan payload.", ct).ConfigureAwait(false);
                    return true;
                }

                var res = await workspaceSetup.PlanInitializationAsync(
                    body.WorkspacePath,
                    body.SelectedAppFileNames ?? [],
                    body.StagedItems ?? [],
                    ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/workspace/initialize", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<WorkspaceInitApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null)
                {
                    await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid initialize payload.", ct).ConfigureAwait(false);
                    return true;
                }

                using var _ = fileWatcher.SuppressScope("workspace");
                var res = await workspaceSetup.InitializeWorkspaceAsync(
                    body.WorkspacePath,
                    body.SelectedAppFileNames ?? [],
                    body.StagedItems ?? [],
                    body.ConflictPolicy,
                    ct).ConfigureAwait(false);

                if (res.IsFailure)
                {
                    await HttpEnvelope.WriteErrorAsync(resp, res.Error!, ct).ConfigureAwait(false);
                    return true;
                }

                fileWatcher.UpdateActiveWorkspace(res.Value.State.WorkspacePath);
                var currentSettings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
                await HttpEnvelope.WriteJsonAsync(resp, 200, new
                {
                    initResult = res.Value.InitResult,
                    activeWorkspace = res.Value.State,
                    recentWorkspaces = currentSettings.RecentWorkspaces ?? []
                }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/workspace/discover", "GET"):
            {
                var webApps = await libraryHandler.DiscoverActiveWorkspaceWebAppsAsync(ct).ConfigureAwait(false);
                var wsRes = await workspaceActions.ListMbyWorkspacesAsync(ct).ConfigureAwait(false);
                await HttpEnvelope.WriteJsonAsync(resp, 200, new
                {
                    webApps,
                    mbyWorkspaces = wsRes.IsSuccess ? wsRes.Value : [],
                    mbyError = wsRes.IsFailure ? wsRes.Error?.Description : null
                }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/workspace/tree", "GET"):
            {
                var targetPath = req.QueryString["path"];
                var res = await workspaceSetup.ListDirectoryAsync(targetPath, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/workspace/agents-md/status", "GET"):
            {
                var ws = req.QueryString["workspacePath"];
                var targetDir = req.QueryString["targetDirectory"];
                var res = await readyWorkspace.GetStatusAsync(ws, targetDir, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/workspace/subfolders", "GET"):
            {
                var ws = req.QueryString["workspacePath"];
                if (string.IsNullOrWhiteSpace(ws))
                {
                    var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
                    ws = settings.ActiveWorkspace?.WorkspacePath;
                }

                if (string.IsNullOrWhiteSpace(ws) || !PathNormalization.TryNormalizeDirectoryPath(ws, out var fullWs))
                {
                    await HttpEnvelope.WriteJsonAsync(resp, 200, new { hasSubfolders = false, subfolders = Array.Empty<object>() }, ct).ConfigureAwait(false);
                    return true;
                }

                var subfolders = Directory.Exists(fullWs)
                    ? new DirectoryInfo(fullWs)
                        .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                        .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0
                            && !d.Name.StartsWith(".", StringComparison.Ordinal))
                        .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(d => new { name = d.Name, fullPath = d.FullName })
                        .ToList<object>()
                    : [];

                await HttpEnvelope.WriteJsonAsync(resp, 200, new { hasSubfolders = subfolders.Count > 0, subfolders }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/workspace/ready", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<ReadyWorkspaceRequest>(req, ct).ConfigureAwait(false);
                if (body is null)
                {
                    await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid ready workspace request.", ct).ConfigureAwait(false);
                    return true;
                }

                using var _ = fileWatcher.SuppressScope("workspace");
                var res = await readyWorkspace.ReadyWorkspaceAsync(body, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/workspace/open-folder", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<PathRequest>(req, ct).ConfigureAwait(false);
                var target = body?.Path;
                if (string.IsNullOrWhiteSpace(target))
                {
                    var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
                    target = settings.ActiveWorkspace?.WorkspacePath;
                }

                var res = externalLauncher.OpenFolder(target ?? string.Empty);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/workspace/reset/plan", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<ResetWorkspaceApiRequest>(req, ct).ConfigureAwait(false);
                var res = await workspaceMaintenance.PlanResetAsync(body?.WorkspacePath, body?.Scope ?? WorkspaceResetScope.ExecutablesOnly, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/workspace/reset/execute", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<ResetWorkspaceApiRequest>(req, ct).ConfigureAwait(false);
                using var _ = fileWatcher.SuppressScope("workspace");
                var res = await workspaceMaintenance.ExecuteResetAsync(body?.WorkspacePath, body?.Scope ?? WorkspaceResetScope.ExecutablesOnly, ct).ConfigureAwait(false);
                if (res.IsFailure)
                {
                    await HttpEnvelope.WriteErrorAsync(resp, res.Error!, ct).ConfigureAwait(false);
                    return true;
                }

                await HttpEnvelope.WriteJsonAsync(resp, 200, new
                {
                    resetResult = res.Value.ResetResult,
                    activeWorkspace = res.Value.UpdatedWorkspace
                }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/workspace/outdated", "GET"):
            {
                var ws = req.QueryString["workspacePath"];
                var tools = await workspaceMaintenance.DetectOutdatedToolsAsync(ws, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteJsonAsync(resp, 200, new { outdatedTools = tools }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/workspace/update-tools", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<PathRequest>(req, ct).ConfigureAwait(false);
                using var _ = fileWatcher.SuppressScope("workspace");
                var res = await workspaceMaintenance.UpdateOutdatedToolsAsync(body?.Path, ct).ConfigureAwait(false);
                if (res.IsFailure)
                {
                    await HttpEnvelope.WriteErrorAsync(resp, res.Error!, ct).ConfigureAwait(false);
                    return true;
                }

                await HttpEnvelope.WriteJsonAsync(resp, 200, new
                {
                    updateResult = res.Value.UpdateResult,
                    activeWorkspace = res.Value.UpdatedWorkspace
                }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/workspaces/build", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<BuildWorkspaceApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null || string.IsNullOrWhiteSpace(body.WorkspaceId))
                {
                    await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "WorkspaceId is required.", ct).ConfigureAwait(false);
                    return true;
                }

                var res = await workspaceActions.BuildAndOpenAsync(body.WorkspaceId, body.OutputDir, ct).ConfigureAwait(false);
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

            case ("/api/workspaces/export", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<ExportPkgApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null || string.IsNullOrWhiteSpace(body.WorkspaceId))
                {
                    await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "WorkspaceId is required.", ct).ConfigureAwait(false);
                    return true;
                }

                var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
                var outPath = body.OutputPath;
                if (string.IsNullOrWhiteSpace(outPath))
                {
                    outPath = await dialogs.SaveFileAsync(
                        "Export .mbypkg Archive",
                        $"{body.WorkspaceId}.mbypkg",
                        "Meloberry Package (*.mbypkg)",
                        "mbypkg",
                        settings.ActiveWorkspace?.WorkspacePath,
                        ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(outPath))
                    {
                        await HttpEnvelope.WriteJsonAsync(resp, 200, new { cancelled = true }, ct).ConfigureAwait(false);
                        return true;
                    }
                }

                var res = await workspaceActions.ExportPackageAsync(body.WorkspaceId, body.Target ?? "android", outPath, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/melomg/extract-figure", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<ExtractFigureApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null || string.IsNullOrWhiteSpace(body.PdfPath) || string.IsNullOrWhiteSpace(body.Query))
                {
                    await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "PdfPath and Query are required.", ct).ConfigureAwait(false);
                    return true;
                }

                var res = await extractFigure.ExtractAsync(new ExtractFigureRequest(
                    body.WorkspacePath,
                    body.PdfPath,
                    body.Query,
                    body.Quality,
                    body.MaskAll,
                    body.EmitMeta), ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/workspace/asset", "GET"):
            {
                var relPath = req.QueryString["relPath"];
                var ws = req.QueryString["workspacePath"];
                if (string.IsNullOrWhiteSpace(ws))
                {
                    var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
                    ws = settings.ActiveWorkspace?.WorkspacePath;
                }

                if (string.IsNullOrWhiteSpace(relPath) ||
                    !PathNormalization.TryNormalizeDirectoryPath(ws, out var fullWs))
                {
                    resp.StatusCode = 404;
                    resp.Close();
                    return true;
                }

                string target;
                try
                {
                    var decoded = Uri.UnescapeDataString(relPath).Trim().Replace('/', Path.DirectorySeparatorChar);
                    var rootPrefix = Path.TrimEndingDirectorySeparator(fullWs) + Path.DirectorySeparatorChar;
                    target = Path.GetFullPath(Path.Combine(fullWs, decoded));
                    var ext = Path.GetExtension(target);
                    if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                        !AssetMimeTypes.TryGetValue(ext, out var mime) ||
                        !File.Exists(target))
                    {
                        resp.StatusCode = 404;
                        resp.Close();
                        return true;
                    }

                    resp.ContentType = mime;
                    resp.Headers["Cache-Control"] = "no-store";
                    resp.StatusCode = 200;
                    await using var fs = File.OpenRead(target);
                    await fs.CopyToAsync(resp.OutputStream, ct).ConfigureAwait(false);
                    resp.Close();
                    return true;
                }
                catch
                {
                    resp.StatusCode = 404;
                    resp.Close();
                    return true;
                }
            }

            default:
                return false;
        }
    }
}
