using System.Diagnostics;
using System.Net;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Common;

namespace Meloberry.Wrapper.Infrastructure.Server.Endpoints;

/// <summary>Handles the 4 lean `/api/live/*` endpoints synchronizing CLI mode and the WebView2 UI.</summary>
public sealed class LiveControlEndpoints(
    ISettingsStore settingsStore,
    IManagedAppsStore appsStore,
    IWebAppMountRegistry mountRegistry,
    LiveDraftHandler liveDraft,
    ManageLibraryHandler manageLibrary,
    FinalizeWorkspaceHandler finalizeHandler,
    SseEventHub sseHub)
{
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
            case ("/api/live/draft", "GET"):
            {
                var wsPath = req.QueryString["workspacePath"];
                var res = await liveDraft.GetDraftAsync(wsPath, ct).ConfigureAwait(false);
                if (res.IsFailure)
                {
                    await HttpEnvelope.WriteErrorAsync(resp, res.Error!, ct).ConfigureAwait(false);
                    return true;
                }

                await HttpEnvelope.WriteJsonAsync(resp, 200, new
                {
                    workspacePath = res.Value.WorkspacePath,
                    draft = res.Value.Draft
                }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/live/draft", "PUT"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<LiveDraftSaveApiRequest>(req, ct).ConfigureAwait(false);
                if (body?.Draft is null)
                {
                    await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid draft payload.", ct).ConfigureAwait(false);
                    return true;
                }

                var res = await liveDraft.ReplaceDraftAsync(
                    body.WorkspacePath,
                    body.Draft,
                    body.Source ?? "ui",
                    body.Summary ?? "Updated draft",
                    ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/live/mutate", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<LiveMutateApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null || string.IsNullOrWhiteSpace(body.Op))
                {
                    await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid mutation payload.", ct).ConfigureAwait(false);
                    return true;
                }

                var mut = new LiveDraftMutationRequest(
                    Op: body.Op,
                    Source: body.Source,
                    TargetId: body.TargetId,
                    Container: body.Container,
                    Title: body.Title,
                    Description: body.Description,
                    AuthoringFormat: body.AuthoringFormat,
                    WorkflowMode: body.WorkflowMode,
                    AllowPlaceholdersOnFinalize: body.AllowPlaceholdersOnFinalize,
                    ComponentKind: body.ComponentKind,
                    Topic: body.Topic,
                    Subtype: body.Subtype,
                    Index: body.Index,
                    Count: body.Count,
                    ContentMode: body.ContentMode,
                    RawText: body.RawText,
                    MarkdownFilePath: body.MarkdownFilePath,
                    Notes: body.Notes,
                    ImagePath: body.ImagePath,
                    Caption: body.Caption,
                    AltText: body.AltText,
                    Modes: body.Modes);

                var res = await liveDraft.MutateAsync(body.WorkspacePath, mut, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/live/finalize", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<LiveFinalizeApiRequest>(req, ct).ConfigureAwait(false)
                    ?? new LiveFinalizeApiRequest();
                var activityId = $"fin_{Guid.NewGuid():N}"[..14];
                var sw = Stopwatch.StartNew();
                sseHub.Broadcast("agent-activity", new
                {
                    id = activityId,
                    state = "running",
                    message = "Finalizing & building WebApp",
                    detail = body.OutputDir,
                    level = "info",
                    timestampUtc = DateTimeOffset.UtcNow
                });

                var res = await finalizeHandler.FinalizeAsync(new Domain.Models.FinalizeRequest(
                    body.WorkspacePath,
                    body.AllowPlaceholders ?? false,
                    body.OutputDir,
                    body.VerifyStaticOnly), ct).ConfigureAwait(false);
                sw.Stop();

                if (res.IsFailure)
                {
                    sseHub.Broadcast("agent-activity", new
                    {
                        id = activityId,
                        state = "error",
                        message = "Finalize failed",
                        detail = res.Error!.Description,
                        level = "err",
                        durationMs = sw.ElapsedMilliseconds,
                        timestampUtc = DateTimeOffset.UtcNow
                    });
                    await HttpEnvelope.WriteErrorAsync(resp, res.Error!, ct).ConfigureAwait(false);
                    return true;
                }

                var fin = res.Value;
                var viewerUrl = $"{viewerBaseUrl.TrimEnd('/')}{fin.ViewerUrl}";
                sseHub.Broadcast("agent-activity", new
                {
                    id = activityId,
                    state = "done",
                    message = $"Built {fin.WorkspaceId}",
                    detail = fin.WebAppDirectory,
                    level = "info",
                    exitCode = 0,
                    durationMs = sw.ElapsedMilliseconds,
                    timestampUtc = DateTimeOffset.UtcNow
                });
                sseHub.Broadcast("webapp-built", new
                {
                    slotId = fin.SlotId,
                    url = viewerUrl,
                    workspaceId = fin.WorkspaceId,
                    webAppDirectory = fin.WebAppDirectory
                });
                await HttpEnvelope.WriteJsonAsync(resp, 200, fin with { ViewerUrl = viewerUrl }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/live/status", "GET"):
            {
                var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
                var wsPath = req.QueryString["workspacePath"];
                if (string.IsNullOrWhiteSpace(wsPath))
                {
                    wsPath = settings.ActiveWorkspace?.WorkspacePath;
                }

                var mby = await appsStore.ResolveToolExecutableAsync("mby", wsPath, settings.MbyExecutableOverride, ct).ConfigureAwait(false);
                var melomg = await appsStore.ResolveToolExecutableAsync("melomg", wsPath, settings.MelomgExecutableOverride, ct).ConfigureAwait(false);
                var draftRes = !string.IsNullOrWhiteSpace(wsPath)
                    ? await liveDraft.GetDraftAsync(wsPath, ct).ConfigureAwait(false)
                    : Result<(string WorkspacePath, Domain.Models.PromptDraft Draft)>.Failure(new Error("Workspace.None", "No active workspace.", ErrorType.NotFound));

                string? mountedWebDir = null;
                if (!string.IsNullOrWhiteSpace(mountRegistry.ActiveSlotId))
                {
                    mountRegistry.TryResolve(mountRegistry.ActiveSlotId, out mountedWebDir);
                }

                await HttpEnvelope.WriteJsonAsync(resp, 200, new
                {
                    guiRunning = true,
                    pid = Environment.ProcessId,
                    activeWorkspace = wsPath,
                    shimPresent = !string.IsNullOrWhiteSpace(wsPath) && File.Exists(Path.Combine(wsPath, Workspace.WorkspaceShimWriter.ShimFileName)),
                    resolvedTools = new { mby, melomg },
                    viewer = new { activeSlotId = mountRegistry.ActiveSlotId, webAppDirectory = mountedWebDir },
                    draft = draftRes.IsSuccess ? draftRes.Value.Draft : null
                }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/live/ui", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<LiveUiApiRequest>(req, ct).ConfigureAwait(false);
                var action = (body?.Action ?? string.Empty).Trim().ToLowerInvariant();
                if (action == "view" && !string.IsNullOrWhiteSpace(body?.View))
                {
                    sseHub.Broadcast("view-requested", new { view = body.View });
                    await HttpEnvelope.WriteJsonAsync(resp, 200, new { ok = true, view = body.View }, ct).ConfigureAwait(false);
                    return true;
                }

                if (action == "viewer-open" && !string.IsNullOrWhiteSpace(body?.Path))
                {
                    var openRes = await manageLibrary.OpenWebAppAsync(body.Path, ct).ConfigureAwait(false);
                    if (openRes.IsFailure)
                    {
                        await HttpEnvelope.WriteErrorAsync(resp, openRes.Error!, ct).ConfigureAwait(false);
                        return true;
                    }

                    var (slotId, relUrl, entry) = openRes.Value;
                    var url = $"{viewerBaseUrl.TrimEnd('/')}{relUrl}";
                    sseHub.Broadcast("webapp-built", new { slotId, url, entry });
                    await HttpEnvelope.WriteJsonAsync(resp, 200, new { slotId, url, entry }, ct).ConfigureAwait(false);
                    return true;
                }

                if (action == "activity" && !string.IsNullOrWhiteSpace(body?.Message))
                {
                    var resolvedState = !string.IsNullOrWhiteSpace(body.State)
                        ? body.State.Trim().ToLowerInvariant()
                        : (string.Equals(body.Level, "err", StringComparison.OrdinalIgnoreCase) ? "error" : "done");
                    sseHub.Broadcast("agent-activity", new
                    {
                        id = body.Id,
                        state = resolvedState,
                        message = body.Message,
                        detail = body.Detail,
                        level = body.Level ?? (resolvedState == "error" ? "err" : "info"),
                        exitCode = body.ExitCode,
                        durationMs = body.DurationMs,
                        timestampUtc = DateTimeOffset.UtcNow
                    });
                    await HttpEnvelope.WriteJsonAsync(resp, 200, new { ok = true }, ct).ConfigureAwait(false);
                    return true;
                }

                await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid /api/live/ui action or missing parameter.", ct).ConfigureAwait(false);
                return true;
            }

            default:
                return false;
        }
    }
}
