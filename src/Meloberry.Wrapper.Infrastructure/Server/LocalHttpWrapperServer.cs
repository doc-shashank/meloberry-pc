using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Infrastructure.Server;

/// <summary>
/// Pure BCL dual-port HttpListener host: ShellPort serves the embedded Shell UI, SSE, and token-protected REST APIs;
/// ViewerPort serves isolated third-party WebApps (/viewer/&lt;slotId&gt;/) and origin-checked POST /api/analysis.
/// </summary>
public sealed partial class LocalHttpWrapperServer : IAsyncDisposable, IDisposable
{
    private const int MaxBodyBytes = 10 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".woff2"] = "font/woff2",
        [".woff"] = "font/woff",
        [".ttf"] = "font/ttf"
    };

    [GeneratedRegex(@"/viewer/([A-Za-z0-9]+)/", RegexOptions.CultureInvariant)]
    private static partial Regex ViewerRefererRegex();

    private readonly IWrapperMetadataProvider _metadataProvider;
    private readonly IWrapperDataPaths _dataPaths;
    private readonly ISettingsStore _settingsStore;
    private readonly IWebAppMountRegistry _mountRegistry;
    private readonly INativeDialogService _dialogs;
    private readonly IExternalUrlLauncher _externalLauncher;
    private readonly ManagedAppsHandler _managedApps;
    private readonly WorkspaceSetupHandler _workspaceSetup;
    private readonly WorkspaceMaintenanceHandler _workspaceMaintenance;
    private readonly ReadyWorkspaceHandler _readyWorkspace;
    private readonly LoadSpecCatalogHandler _specCatalog;
    private readonly PromptHandlers _promptHandlers;
    private readonly SessionTemplateHandler _templateHandler;
    private readonly IWorkspaceFileWatcher _fileWatcher;
    private readonly ManageLibraryHandler _libraryHandler;
    private readonly WorkspaceActionsHandler _workspaceActions;
    private readonly CaptureAnalysisHandler _analysisHandler;
    private readonly SelfUpdateHandler _selfUpdate;
    private readonly SettingsAndPresetsHandler _settingsPresets;

    private readonly string _token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    private readonly ConcurrentDictionary<Guid, Channel<byte[]>> _sseClients = new();
    private HttpListener? _listener;
    private HttpListener? _viewerListener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private Task? _viewerAcceptLoop;
    private int _disposed;

    public LocalHttpWrapperServer(
        IWrapperMetadataProvider metadataProvider,
        IWrapperDataPaths dataPaths,
        ISettingsStore settingsStore,
        IWebAppMountRegistry mountRegistry,
        INativeDialogService dialogs,
        IExternalUrlLauncher externalLauncher,
        ManagedAppsHandler managedApps,
        WorkspaceSetupHandler workspaceSetup,
        WorkspaceMaintenanceHandler workspaceMaintenance,
        ReadyWorkspaceHandler readyWorkspace,
        LoadSpecCatalogHandler specCatalog,
        PromptHandlers promptHandlers,
        SessionTemplateHandler templateHandler,
        IWorkspaceFileWatcher fileWatcher,
        ManageLibraryHandler libraryHandler,
        WorkspaceActionsHandler workspaceActions,
        CaptureAnalysisHandler analysisHandler,
        SelfUpdateHandler selfUpdate,
        SettingsAndPresetsHandler settingsPresets)
    {
        _metadataProvider = metadataProvider;
        _dataPaths = dataPaths;
        _settingsStore = settingsStore;
        _mountRegistry = mountRegistry;
        _dialogs = dialogs;
        _externalLauncher = externalLauncher;
        _managedApps = managedApps;
        _workspaceSetup = workspaceSetup;
        _workspaceMaintenance = workspaceMaintenance;
        _readyWorkspace = readyWorkspace;
        _specCatalog = specCatalog;
        _promptHandlers = promptHandlers;
        _templateHandler = templateHandler;
        _fileWatcher = fileWatcher;
        _libraryHandler = libraryHandler;
        _workspaceActions = workspaceActions;
        _analysisHandler = analysisHandler;
        _selfUpdate = selfUpdate;
        _settingsPresets = settingsPresets;

        _fileWatcher.Changed += OnWorkspaceFileChanged;
    }

    public int Port { get; private set; }
    public int ViewerPort { get; private set; }
    public string BaseUrl => $"http://127.0.0.1:{Port}/";
    public string ViewerBaseUrl => $"http://127.0.0.1:{ViewerPort}/";
    public string AuthToken => _token;

    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        Port = FindFreeTcpPort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();

        ViewerPort = FindFreeTcpPort();
        _viewerListener = new HttpListener();
        _viewerListener.Prefixes.Add($"http://127.0.0.1:{ViewerPort}/");
        _viewerListener.Start();

        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_listener, isViewerHost: false, _cts.Token));
        _viewerAcceptLoop = Task.Run(() => AcceptLoopAsync(_viewerListener, isViewerHost: true, _cts.Token));
    }

    private async Task AcceptLoopAsync(HttpListener listener, bool isViewerHost, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            _ = Task.Run(() => isViewerHost
                ? HandleViewerRequestAsync(ctx, ct)
                : HandleRequestAsync(ctx, ct), ct);
        }
    }

    private async Task HandleViewerRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var req = ctx.Request;
            var hostHeader = req.Headers["Host"];
            if (!IsValidLocalHost(hostHeader, ViewerPort))
            {
                await WriteJsonErrorAsync(ctx.Response, 403, "Security.InvalidHost", "Forbidden Host header.", ct).ConfigureAwait(false);
                return;
            }

            var path = req.Url?.AbsolutePath ?? "/";
            var method = req.HttpMethod.ToUpperInvariant();

            if (path.StartsWith("/viewer/", StringComparison.OrdinalIgnoreCase) && method is "GET" or "HEAD")
            {
                await ServeMountedWebAppAsync(ctx.Response, path, ct).ConfigureAwait(false);
                return;
            }

            if (path == "/api/analysis" && method == "POST")
            {
                await HandleAnalysisPostAsync(req, ctx.Response, ct).ConfigureAwait(false);
                return;
            }

            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            try
            {
                await WriteJsonErrorAsync(ctx.Response, 500, "Server.Unhandled", ex.Message, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Response already closed.
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var req = ctx.Request;
            var hostHeader = req.Headers["Host"];
            if (!IsValidLocalHost(hostHeader, Port))
            {
                await WriteJsonErrorAsync(ctx.Response, 403, "Security.InvalidHost", "Forbidden Host header.", ct).ConfigureAwait(false);
                return;
            }

            var path = req.Url?.AbsolutePath ?? "/";
            var method = req.HttpMethod.ToUpperInvariant();

            if (path == "/" || path == "/index.html")
            {
                await ServeEmbeddedShellHtmlAsync(ctx.Response, ct).ConfigureAwait(false);
                return;
            }

            if (path == "/shell/shell.css")
            {
                await ServeEmbeddedResourceAsync(ctx.Response, "WebAssets.shell.css", "text/css; charset=utf-8", ct).ConfigureAwait(false);
                return;
            }

            if (path == "/shell/shell.js")
            {
                await ServeEmbeddedResourceAsync(ctx.Response, "WebAssets.shell.js", "application/javascript; charset=utf-8", ct).ConfigureAwait(false);
                return;
            }

            if (path == "/api/events" && method == "GET")
            {
                var queryToken = req.QueryString["token"];
                if (!string.Equals(queryToken, _token, StringComparison.Ordinal))
                {
                    await WriteJsonErrorAsync(ctx.Response, 403, "Security.InvalidToken", "Missing or invalid SSE token.", ct).ConfigureAwait(false);
                    return;
                }

                await HandleEventsStreamAsync(ctx.Response, ct).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(path, "/api/analysis", StringComparison.OrdinalIgnoreCase))
            {
                var provided = req.Headers["X-Mby-Token"];
                if (!string.Equals(provided, _token, StringComparison.Ordinal))
                {
                    await WriteJsonErrorAsync(ctx.Response, 403, "Security.InvalidToken", "Missing or invalid X-Mby-Token header.", ct).ConfigureAwait(false);
                    return;
                }

                await RouteApiAsync(req, ctx.Response, method, path, ct).ConfigureAwait(false);
                return;
            }

            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            try
            {
                await WriteJsonErrorAsync(ctx.Response, 500, "Server.Unhandled", ex.Message, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Response already closed.
            }
        }
    }

    private async Task RouteApiAsync(HttpListenerRequest req, HttpListenerResponse resp, string method, string path, CancellationToken ct)
    {
        switch (path, method)
        {
            case ("/api/bootstrap", "GET"):
            {
                var settings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
                var apps = await _managedApps.ListAppsAsync(ct).ConfigureAwait(false);
                var meta = _metadataProvider.GetMetadata();
                AgentsMdStatus? agentsStatus = null;
                IReadOnlyList<OutdatedWorkspaceTool> outdatedTools = [];
                if (!string.IsNullOrWhiteSpace(settings.ActiveWorkspace?.WorkspacePath))
                {
                    _fileWatcher.UpdateActiveWorkspace(settings.ActiveWorkspace.WorkspacePath);
                    var st = await _readyWorkspace.GetStatusAsync(settings.ActiveWorkspace.WorkspacePath, ct).ConfigureAwait(false);
                    if (st.IsSuccess)
                    {
                        agentsStatus = st.Value;
                    }

                    outdatedTools = await _workspaceMaintenance.DetectOutdatedToolsAsync(settings.ActiveWorkspace.WorkspacePath, ct).ConfigureAwait(false);
                }

                await WriteJsonAsync(resp, 200, new
                {
                    metadata = meta,
                    appsDirectory = _dataPaths.AppsDirectory,
                    viewerOrigin = ViewerBaseUrl.TrimEnd('/'),
                    settings,
                    apps,
                    agentsMdStatus = agentsStatus,
                    outdatedTools,
                    ecosystemRepos = EcosystemCatalog.Repositories,
                    builtInPresets = BuiltInPresets.All
                }, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/apps", "GET"):
            {
                var apps = await _managedApps.ListAppsAsync(ct).ConfigureAwait(false);
                await WriteJsonAsync(resp, 200, new { appsDirectory = _dataPaths.AppsDirectory, apps }, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/apps/download", "POST"):
            {
                var body = await ReadJsonAsync<ToolKeyRequest>(req, ct).ConfigureAwait(false);
                using var _ = _fileWatcher.SuppressScope("apps");
                var res = await _managedApps.DownloadToolAsync(body?.Tool ?? string.Empty, ct).ConfigureAwait(false);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/apps/download-status", "GET"):
            {
                var tool = req.QueryString["tool"] ?? "mby";
                var snap = _managedApps.GetDownloadStatus(tool);
                await WriteJsonAsync(resp, 200, new { status = snap }, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/apps/check-update", "GET"):
            {
                var tool = req.QueryString["tool"] ?? string.Empty;
                var res = await _managedApps.CheckToolUpdateAsync(tool, ct).ConfigureAwait(false);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/apps/uninstall", "POST"):
            {
                var body = await ReadJsonAsync<UninstallAppRequest>(req, ct).ConfigureAwait(false);
                using var _ = _fileWatcher.SuppressScope("apps");
                var res = await _managedApps.UninstallAppAsync(body?.IdOrFileName ?? string.Empty, ct).ConfigureAwait(false);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/apps/import", "POST"):
            {
                var body = await ReadJsonAsync<PathRequest>(req, ct).ConfigureAwait(false);
                var source = body?.Path;
                if (string.IsNullOrWhiteSpace(source))
                {
                    var picked = await _dialogs.PickFilesAsync("Select Executable (.exe) to Import into apps\\", "Executable Files (*.exe)", "*.exe", false, null, ct).ConfigureAwait(false);
                    if (picked.Count == 0)
                    {
                        await WriteJsonAsync(resp, 200, new { cancelled = true }, ct).ConfigureAwait(false);
                        return;
                    }

                    source = picked[0];
                }

                using var _ = _fileWatcher.SuppressScope("apps");
                var res = await _managedApps.ImportLocalExecutableAsync(source, ct).ConfigureAwait(false);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/workspace/select", "POST"):
            {
                var body = await ReadJsonAsync<PathRequest>(req, ct).ConfigureAwait(false);
                var res = await _workspaceSetup.SelectWorkspaceAsync(body?.Path ?? string.Empty, ct).ConfigureAwait(false);
                if (res.IsFailure)
                {
                    await WriteErrorAsync(resp, res.Error!, ct).ConfigureAwait(false);
                    return;
                }

                _fileWatcher.UpdateActiveWorkspace(res.Value.WorkspacePath);
                var outdated = await _workspaceMaintenance.DetectOutdatedToolsAsync(res.Value.WorkspacePath, ct).ConfigureAwait(false);
                var currentSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
                await WriteJsonAsync(resp, 200, new
                {
                    state = res.Value,
                    outdatedTools = outdated,
                    recentWorkspaces = currentSettings.RecentWorkspaces ?? []
                }, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/workspace/plan", "POST"):
            {
                var body = await ReadJsonAsync<WorkspaceInitApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null)
                {
                    await WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid plan payload.", ct).ConfigureAwait(false);
                    return;
                }

                var res = await _workspaceSetup.PlanInitializationAsync(
                    body.WorkspacePath,
                    body.SelectedAppFileNames ?? [],
                    body.StagedItems ?? [],
                    ct).ConfigureAwait(false);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/workspace/initialize", "POST"):
            {
                var body = await ReadJsonAsync<WorkspaceInitApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null)
                {
                    await WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid initialize payload.", ct).ConfigureAwait(false);
                    return;
                }

                using var _ = _fileWatcher.SuppressScope("workspace");
                var res = await _workspaceSetup.InitializeWorkspaceAsync(
                    body.WorkspacePath,
                    body.SelectedAppFileNames ?? [],
                    body.StagedItems ?? [],
                    body.ConflictPolicy,
                    ct).ConfigureAwait(false);

                if (res.IsFailure)
                {
                    await WriteErrorAsync(resp, res.Error!, ct).ConfigureAwait(false);
                    return;
                }

                _fileWatcher.UpdateActiveWorkspace(res.Value.State.WorkspacePath);
                var currentSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
                await WriteJsonAsync(resp, 200, new
                {
                    initResult = res.Value.InitResult,
                    activeWorkspace = res.Value.State,
                    recentWorkspaces = currentSettings.RecentWorkspaces ?? []
                }, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/workspace/discover", "GET"):
            {
                var webApps = await _libraryHandler.DiscoverActiveWorkspaceWebAppsAsync(ct).ConfigureAwait(false);
                var wsRes = await _workspaceActions.ListMbyWorkspacesAsync(ct).ConfigureAwait(false);
                await WriteJsonAsync(resp, 200, new
                {
                    webApps,
                    mbyWorkspaces = wsRes.IsSuccess ? wsRes.Value : [],
                    mbyError = wsRes.IsFailure ? wsRes.Error?.Description : null
                }, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/workspace/agents-md/status", "GET"):
            {
                var ws = req.QueryString["workspacePath"];
                var res = await _readyWorkspace.GetStatusAsync(ws, ct).ConfigureAwait(false);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/workspace/ready", "POST"):
            {
                var body = await ReadJsonAsync<ReadyWorkspaceRequest>(req, ct).ConfigureAwait(false);
                if (body is null)
                {
                    await WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid ready workspace request.", ct).ConfigureAwait(false);
                    return;
                }

                using var _ = _fileWatcher.SuppressScope("workspace");
                var res = await _readyWorkspace.ReadyWorkspaceAsync(body, ct).ConfigureAwait(false);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/workspace/open-folder", "POST"):
            {
                var body = await ReadJsonAsync<PathRequest>(req, ct).ConfigureAwait(false);
                var target = body?.Path;
                if (string.IsNullOrWhiteSpace(target))
                {
                    var settings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
                    target = settings.ActiveWorkspace?.WorkspacePath;
                }

                var res = _externalLauncher.OpenFolder(target ?? string.Empty);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/workspace/reset/plan", "POST"):
            {
                var body = await ReadJsonAsync<ResetWorkspaceApiRequest>(req, ct).ConfigureAwait(false);
                var res = await _workspaceMaintenance.PlanResetAsync(body?.WorkspacePath, body?.Scope ?? WorkspaceResetScope.ExecutablesOnly, ct).ConfigureAwait(false);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/workspace/reset/execute", "POST"):
            {
                var body = await ReadJsonAsync<ResetWorkspaceApiRequest>(req, ct).ConfigureAwait(false);
                using var _ = _fileWatcher.SuppressScope("workspace");
                var res = await _workspaceMaintenance.ExecuteResetAsync(body?.WorkspacePath, body?.Scope ?? WorkspaceResetScope.ExecutablesOnly, ct).ConfigureAwait(false);
                if (res.IsFailure)
                {
                    await WriteErrorAsync(resp, res.Error!, ct).ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(resp, 200, new
                {
                    resetResult = res.Value.ResetResult,
                    activeWorkspace = res.Value.UpdatedWorkspace
                }, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/workspace/outdated", "GET"):
            {
                var ws = req.QueryString["workspacePath"];
                var tools = await _workspaceMaintenance.DetectOutdatedToolsAsync(ws, ct).ConfigureAwait(false);
                await WriteJsonAsync(resp, 200, new { outdatedTools = tools }, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/workspace/update-tools", "POST"):
            {
                var body = await ReadJsonAsync<PathRequest>(req, ct).ConfigureAwait(false);
                using var _ = _fileWatcher.SuppressScope("workspace");
                var res = await _workspaceMaintenance.UpdateOutdatedToolsAsync(body?.Path, ct).ConfigureAwait(false);
                if (res.IsFailure)
                {
                    await WriteErrorAsync(resp, res.Error!, ct).ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(resp, 200, new
                {
                    updateResult = res.Value.UpdateResult,
                    activeWorkspace = res.Value.UpdatedWorkspace
                }, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/specs", "GET"):
            {
                var refresh = string.Equals(req.QueryString["refresh"], "true", StringComparison.OrdinalIgnoreCase);
                var (specs, isLive, activeMbyPath) = await _specCatalog.LoadAsync(refresh, ct).ConfigureAwait(false);
                await WriteJsonAsync(resp, 200, new { specs, isLive, activeMbyPath }, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/prompt/compose", "POST"):
            {
                var draft = await ReadJsonAsync<PromptDraft>(req, ct).ConfigureAwait(false);
                if (draft is null)
                {
                    await WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid PromptDraft payload.", ct).ConfigureAwait(false);
                    return;
                }

                var composed = await _promptHandlers.ComposeAsync(draft, ct).ConfigureAwait(false);
                var plainText = Meloberry.Wrapper.Domain.Services.PromptComposer.ToPlainText(composed.Markdown);
                await WriteJsonAsync(resp, 200, new
                {
                    composed.Markdown,
                    composed.CharacterCount,
                    composed.ApproximateTokens,
                    plainText
                }, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/prompt/raw-json", "POST"):
            {
                var draft = await ReadJsonAsync<PromptDraft>(req, ct).ConfigureAwait(false);
                if (draft is null)
                {
                    await WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid PromptDraft payload.", ct).ConfigureAwait(false);
                    return;
                }

                var res = await _templateHandler.BuildRawJsonAsync(draft, ct).ConfigureAwait(false);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/prompt/save-json", "POST"):
            {
                var body = await ReadJsonAsync<SaveJsonApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null || string.IsNullOrWhiteSpace(body.RawJson))
                {
                    await WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid raw JSON payload.", ct).ConfigureAwait(false);
                    return;
                }

                var settings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
                var saveTarget = await _dialogs.SaveFileAsync(
                    "Export Raw Session JSON",
                    body.DefaultName ?? "template_session.json",
                    "JSON File (*.json)",
                    "json",
                    settings.ActiveWorkspace?.WorkspacePath,
                    ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(saveTarget))
                {
                    await WriteJsonAsync(resp, 200, new { cancelled = true }, ct).ConfigureAwait(false);
                    return;
                }

                var res = await _promptHandlers.SavePromptAsync(saveTarget, body.RawJson, ct).ConfigureAwait(false);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/prompt/generate-template", "POST"):
            {
                var draft = await ReadJsonAsync<PromptDraft>(req, ct).ConfigureAwait(false);
                if (draft is null)
                {
                    await WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid PromptDraft payload.", ct).ConfigureAwait(false);
                    return;
                }

                var res = await _templateHandler.GenerateDraftTemplateAsync(draft, ct).ConfigureAwait(false);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/prompt/save", "POST"):
            {
                var body = await ReadJsonAsync<SavePromptApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null)
                {
                    await WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid save prompt payload.", ct).ConfigureAwait(false);
                    return;
                }

                var settings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
                var saveTarget = await _dialogs.SaveFileAsync(
                    "Save Prompt Markdown",
                    "PROMPT.md",
                    "Markdown File (*.md)",
                    "md",
                    settings.ActiveWorkspace?.WorkspacePath,
                    ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(saveTarget))
                {
                    await WriteJsonAsync(resp, 200, new { cancelled = true }, ct).ConfigureAwait(false);
                    return;
                }

                var res = await _promptHandlers.SavePromptAsync(saveTarget, body.Markdown ?? string.Empty, ct).ConfigureAwait(false);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/presets", "GET"):
            {
                var presets = await _settingsPresets.GetAllPresetsAsync(ct).ConfigureAwait(false);
                await WriteJsonAsync(resp, 200, new { presets }, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/presets", "POST"):
            {
                var preset = await ReadJsonAsync<PromptPreset>(req, ct).ConfigureAwait(false);
                if (preset is null)
                {
                    await WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid preset.", ct).ConfigureAwait(false);
                    return;
                }

                var res = await _settingsPresets.SaveCustomPresetAsync(preset, ct).ConfigureAwait(false);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/presets", "DELETE"):
            {
                var id = req.QueryString["id"] ?? string.Empty;
                var res = await _settingsPresets.DeleteCustomPresetAsync(id, ct).ConfigureAwait(false);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/viewer/open", "POST"):
            {
                var body = await ReadJsonAsync<PathRequest>(req, ct).ConfigureAwait(false);
                var folder = body?.Path;
                if (string.IsNullOrWhiteSpace(folder))
                {
                    var settings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
                    folder = await _dialogs.PickFolderAsync("Select Compiled Meloberry WebApp Folder", settings.ActiveWorkspace?.WorkspacePath, ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(folder))
                    {
                        await WriteJsonAsync(resp, 200, new { cancelled = true }, ct).ConfigureAwait(false);
                        return;
                    }
                }

                var res = await _libraryHandler.OpenWebAppAsync(folder, ct).ConfigureAwait(false);
                if (res.IsFailure)
                {
                    await WriteErrorAsync(resp, res.Error!, ct).ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(resp, 200, new
                {
                    slotId = res.Value.SlotId,
                    url = $"{ViewerBaseUrl.TrimEnd('/')}{res.Value.Url}",
                    entry = res.Value.Entry
                }, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/workspaces/build", "POST"):
            {
                var body = await ReadJsonAsync<BuildWorkspaceApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null || string.IsNullOrWhiteSpace(body.WorkspaceId))
                {
                    await WriteJsonErrorAsync(resp, 400, "Request.Invalid", "WorkspaceId is required.", ct).ConfigureAwait(false);
                    return;
                }

                var res = await _workspaceActions.BuildAndOpenAsync(body.WorkspaceId, body.OutputDir, ct).ConfigureAwait(false);
                if (res.IsFailure)
                {
                    await WriteErrorAsync(resp, res.Error!, ct).ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(resp, 200, new
                {
                    slotId = res.Value.SlotId,
                    url = $"{ViewerBaseUrl.TrimEnd('/')}{res.Value.Url}",
                    entry = res.Value.Entry
                }, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/workspaces/export", "POST"):
            {
                var body = await ReadJsonAsync<ExportPkgApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null || string.IsNullOrWhiteSpace(body.WorkspaceId))
                {
                    await WriteJsonErrorAsync(resp, 400, "Request.Invalid", "WorkspaceId is required.", ct).ConfigureAwait(false);
                    return;
                }

                var settings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
                var outPath = body.OutputPath;
                if (string.IsNullOrWhiteSpace(outPath))
                {
                    outPath = await _dialogs.SaveFileAsync(
                        "Export .mbypkg Archive",
                        $"{body.WorkspaceId}.mbypkg",
                        "Meloberry Package (*.mbypkg)",
                        "mbypkg",
                        settings.ActiveWorkspace?.WorkspacePath,
                        ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(outPath))
                    {
                        await WriteJsonAsync(resp, 200, new { cancelled = true }, ct).ConfigureAwait(false);
                        return;
                    }
                }

                var res = await _workspaceActions.ExportPackageAsync(body.WorkspaceId, body.Target ?? "android", outPath, ct).ConfigureAwait(false);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/self-update/check", "POST"):
            {
                var res = await _selfUpdate.CheckAsync(ct).ConfigureAwait(false);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/self-update/install", "POST"):
            {
                var res = await _selfUpdate.DownloadAndInstallAsync(ct).ConfigureAwait(false);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/self-update/status", "GET"):
            {
                await WriteJsonAsync(resp, 200, new { status = _selfUpdate.GetStatus() }, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/dialogs/pick-folder", "POST"):
            {
                var body = await ReadJsonAsync<DialogFolderRequest>(req, ct).ConfigureAwait(false);
                var folder = await _dialogs.PickFolderAsync(body?.Title ?? "Select Folder", body?.InitialDir, ct).ConfigureAwait(false);
                await WriteJsonAsync(resp, 200, new { path = folder, cancelled = string.IsNullOrWhiteSpace(folder) }, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/dialogs/pick-files", "POST"):
            {
                var body = await ReadJsonAsync<DialogFilesRequest>(req, ct).ConfigureAwait(false);
                var files = await _dialogs.PickFilesAsync(
                    body?.Title ?? "Select Files",
                    body?.FilterLabel ?? "All Study Files (*.pdf;*.md;*.txt;*.json;*.png;*.jpg)",
                    body?.FilterPattern ?? "*.*",
                    body?.AllowMultiple ?? true,
                    body?.InitialDir,
                    ct).ConfigureAwait(false);
                await WriteJsonAsync(resp, 200, new { paths = files, cancelled = files.Count == 0 }, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/shell/open-url", "POST"):
            {
                var body = await ReadJsonAsync<UrlRequest>(req, ct).ConfigureAwait(false);
                var res = _externalLauncher.OpenAllowlistedUrl(body?.Url ?? string.Empty);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/settings", "GET"):
            {
                var settings = await _settingsPresets.GetSettingsAsync(ct).ConfigureAwait(false);
                await WriteJsonAsync(resp, 200, settings, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/settings", "PUT"):
            {
                var body = await ReadJsonAsync<UpdateSettingsApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null)
                {
                    await WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid settings payload.", ct).ConfigureAwait(false);
                    return;
                }

                var res = await _settingsPresets.UpdateSettingsAsync(
                    body.ThemeMode,
                    body.MbyExecutableOverride,
                    body.MelomgExecutableOverride,
                    body.DefaultBuildOutputDir,
                    body.SelfUpdateRepoOwner,
                    body.SelfUpdateRepoName,
                    body.OverrideWebAppTheme,
                    ct).ConfigureAwait(false);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            case ("/api/settings/override-theme", "PUT"):
            {
                var body = await ReadJsonAsync<OverrideThemeApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null)
                {
                    await WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid override-theme payload.", ct).ConfigureAwait(false);
                    return;
                }

                var res = await _settingsPresets.UpdateOverrideWebAppThemeAsync(body.OverrideWebAppTheme, ct).ConfigureAwait(false);
                await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return;
            }

            default:
                await WriteJsonErrorAsync(resp, 404, "Route.NotFound", $"Unknown API endpoint: {method} {path}", ct).ConfigureAwait(false);
                return;
        }
    }

    private async Task HandleAnalysisPostAsync(HttpListenerRequest req, HttpListenerResponse resp, CancellationToken ct)
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
                originUri.Port == ViewerPort;
        }
        else if (!string.IsNullOrWhiteSpace(referer))
        {
            isOriginValid = Uri.TryCreate(referer, UriKind.Absolute, out var refUri) &&
                string.Equals(refUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(refUri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(refUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) &&
                refUri.Port == ViewerPort &&
                refUri.AbsolutePath.StartsWith("/viewer/", StringComparison.OrdinalIgnoreCase);
        }

        if (!isOriginValid)
        {
            await WriteJsonErrorAsync(resp, 403, "Security.InvalidOrigin", "Cross-origin or untrusted /api/analysis denied.", ct).ConfigureAwait(false);
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

        var (tooLarge, rawBody) = await ReadBoundedUtf8BodyAsync(req, MaxBodyBytes, ct).ConfigureAwait(false);
        if (tooLarge)
        {
            await WriteJsonErrorAsync(resp, 413, "Request.PayloadTooLarge", "Analysis payload exceeds 10 MB limit.", ct).ConfigureAwait(false);
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

        var res = await _analysisHandler.CaptureAsync(slotId, reportJson, markdown, ct).ConfigureAwait(false);
        await WriteResultAsync(resp, res, ct).ConfigureAwait(false);
    }

    private async Task ServeMountedWebAppAsync(HttpListenerResponse resp, string path, CancellationToken ct)
    {
        var trimmed = path["/viewer/".Length..];
        var slash = trimmed.IndexOf('/');
        if (slash <= 0)
        {
            resp.StatusCode = 404;
            resp.Close();
            return;
        }

        var slotId = trimmed[..slash];
        var relPath = trimmed[(slash + 1)..];
        if (string.IsNullOrWhiteSpace(relPath))
        {
            relPath = "index.html";
        }

        if (!_mountRegistry.TryResolve(slotId, out var webDir))
        {
            resp.StatusCode = 404;
            resp.Close();
            return;
        }

        var decoded = Uri.UnescapeDataString(relPath).Replace('/', Path.DirectorySeparatorChar);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(webDir)) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(fullRoot, decoded));
        if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
        {
            resp.StatusCode = 404;
            resp.Close();
            return;
        }

        var ext = Path.GetExtension(target);
        resp.ContentType = MimeTypes.GetValueOrDefault(ext, "application/octet-stream");
        resp.Headers["Cache-Control"] = "no-store";
        resp.StatusCode = 200;

        if (string.Equals(relPath, "index.html", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ext, ".html", StringComparison.OrdinalIgnoreCase))
        {
            var html = await File.ReadAllTextAsync(target, Encoding.UTF8, ct).ConfigureAwait(false);
            var bridge = BuildViewerThemeBridgeScript(Port);
            var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            html = bodyClose >= 0 ? html.Insert(bodyClose, bridge) : html + bridge;
            var bytes = Encoding.UTF8.GetBytes(html);
            await resp.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            resp.Close();
            return;
        }

        await using var fs = File.OpenRead(target);
        await fs.CopyToAsync(resp.OutputStream, ct).ConfigureAwait(false);
        resp.Close();
    }

    private static string BuildViewerThemeBridgeScript(int shellPort) => $$"""
<script>
(() => {
  const allowed127 = "http://127.0.0.1:{{shellPort}}";
  const allowedLocal = "http://localhost:{{shellPort}}";
  const origTheme = document.documentElement.getAttribute("data-theme");
  window.addEventListener("message", ev => {
    if (ev.origin !== allowed127 && ev.origin !== allowedLocal) return;
    const d = ev.data;
    if (!d || d.type !== "mby-theme-override") return;
    const styleId = "mby-wrapper-theme-fallback";
    let el = document.getElementById(styleId);
    if (d.override) {
      if (!el && document.head && d.fallbackCss) {
        el = document.createElement("style");
        el.id = styleId;
        el.textContent = String(d.fallbackCss);
        document.head.appendChild(el);
      }
      if (d.theme) document.documentElement.setAttribute("data-theme", String(d.theme));
    } else {
      if (el) el.remove();
      if (origTheme !== null && origTheme !== "") document.documentElement.setAttribute("data-theme", origTheme);
      else document.documentElement.removeAttribute("data-theme");
    }
  });
})();
</script>
""";

    private async Task ServeEmbeddedShellHtmlAsync(HttpListenerResponse resp, CancellationToken ct)
    {
        var html = ReadEmbeddedText("WebAssets.shell.html");
        html = html.Replace("__MBY_TOKEN_VALUE__", _token, StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(html);
        resp.ContentType = "text/html; charset=utf-8";
        resp.Headers["X-Frame-Options"] = "DENY";
        resp.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
        resp.StatusCode = 200;
        await resp.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        resp.Close();
    }

    private async Task ServeEmbeddedResourceAsync(HttpListenerResponse resp, string suffix, string contentType, CancellationToken ct)
    {
        var text = ReadEmbeddedText(suffix);
        var bytes = Encoding.UTF8.GetBytes(text);
        resp.ContentType = contentType;
        resp.StatusCode = 200;
        await resp.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        resp.Close();
    }

    private static string ReadEmbeddedText(string suffix)
    {
        var asm = typeof(LocalHttpWrapperServer).Assembly;
        var resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Missing embedded WebAsset '{suffix}'.");

        using var stream = asm.GetManifestResourceStream(resName)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task<(bool TooLarge, string Body)> ReadBoundedUtf8BodyAsync(
        HttpListenerRequest req,
        int maxBytes,
        CancellationToken ct)
    {
        if (req.ContentLength64 > maxBytes)
        {
            return (true, string.Empty);
        }

        using var ms = new MemoryStream();
        var buffer = new byte[16384];
        long total = 0;
        int read;
        while ((read = await req.InputStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                return (true, string.Empty);
            }

            ms.Write(buffer, 0, read);
        }

        return (false, Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest req, CancellationToken ct)
    {
        try
        {
            var (tooLarge, body) = await ReadBoundedUtf8BodyAsync(req, MaxBodyBytes, ct).ConfigureAwait(false);
            if (tooLarge || string.IsNullOrWhiteSpace(body))
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch
        {
            return default;
        }
    }

    private static Task WriteResultAsync<T>(HttpListenerResponse resp, Result<T> result, CancellationToken ct) =>
        result.IsSuccess
            ? WriteJsonAsync(resp, 200, result.Value, ct)
            : WriteErrorAsync(resp, result.Error!, ct);

    private static Task WriteResultAsync(HttpListenerResponse resp, Result result, CancellationToken ct) =>
        result.IsSuccess
            ? WriteJsonAsync(resp, 200, new { ok = true }, ct)
            : WriteErrorAsync(resp, result.Error!, ct);

    private static Task WriteErrorAsync(HttpListenerResponse resp, Error error, CancellationToken ct)
    {
        var status = error.Type switch
        {
            ErrorType.Validation => 400,
            ErrorType.NotFound => 404,
            ErrorType.Conflict => 409,
            ErrorType.Security => 403,
            ErrorType.Network => 502,
            ErrorType.ExternalProcess => 422,
            _ => 500
        };
        return WriteJsonErrorAsync(resp, status, error.Code, error.Description, ct, error.Hint);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse resp, int status, object? payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.StatusCode = status;
        resp.ContentType = "application/json; charset=utf-8";
        await resp.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        resp.Close();
    }

    private static Task WriteJsonErrorAsync(
        HttpListenerResponse resp,
        int status,
        string code,
        string description,
        CancellationToken ct,
        string? hint = null) =>
        WriteJsonAsync(resp, status, new { error = new { code, description, hint } }, ct);

    private async Task HandleEventsStreamAsync(HttpListenerResponse resp, CancellationToken ct)
    {
        resp.ContentType = "text/event-stream; charset=utf-8";
        resp.Headers["Cache-Control"] = "no-cache";
        resp.SendChunked = true;
        resp.StatusCode = 200;

        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _sseClients[id] = channel;

        try
        {
            var hello = Encoding.UTF8.GetBytes(": connected\n\n");
            await resp.OutputStream.WriteAsync(hello, ct).ConfigureAwait(false);
            await resp.OutputStream.FlushAsync(ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                while (channel.Reader.TryRead(out var frame))
                {
                    await resp.OutputStream.WriteAsync(frame, ct).ConfigureAwait(false);
                    await resp.OutputStream.FlushAsync(ct).ConfigureAwait(false);
                }

                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pingCts.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    if (!await channel.Reader.WaitToReadAsync(pingCts.Token).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    var ping = Encoding.UTF8.GetBytes(": ping\n\n");
                    await resp.OutputStream.WriteAsync(ping, ct).ConfigureAwait(false);
                    await resp.OutputStream.FlushAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // Client disconnected or server shutting down.
        }
        finally
        {
            _sseClients.TryRemove(id, out _);
            channel.Writer.TryComplete();
            try { resp.Close(); } catch { }
        }
    }

    private void OnWorkspaceFileChanged(WorkspaceFileChangeEvent evt)
    {
        var payload = JsonSerializer.Serialize(evt, JsonOptions);
        var frame = Encoding.UTF8.GetBytes($"event: fschange\ndata: {payload}\n\n");

        foreach (var kv in _sseClients)
        {
            kv.Value.Writer.TryWrite(frame);
        }
    }

    private static bool IsValidLocalHost(string? hostHeader, int port)
    {
        if (string.IsNullOrWhiteSpace(hostHeader))
        {
            return false;
        }

        return string.Equals(hostHeader, $"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hostHeader, $"localhost:{port}", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindFreeTcpPort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        return ((IPEndPoint)tcp.LocalEndpoint).Port;
    }

    public void Dispose() =>
        DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _fileWatcher.Changed -= OnWorkspaceFileChanged;

        foreach (var kv in _sseClients)
        {
            kv.Value.Writer.TryComplete();
        }
        _sseClients.Clear();

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        _listener?.Close();
        _viewerListener?.Close();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
                // Shutdown completed.
            }
        }

        if (_viewerAcceptLoop is not null)
        {
            try
            {
                await _viewerAcceptLoop.ConfigureAwait(false);
            }
            catch
            {
                // Shutdown completed.
            }
        }

        _cts?.Dispose();
    }

    private sealed record ToolKeyRequest(string? Tool);
    private sealed record UninstallAppRequest(string? IdOrFileName);
    private sealed record PathRequest(string? Path);
    private sealed record UrlRequest(string? Url);
    private sealed record WorkspaceInitApiRequest(
        string WorkspacePath,
        IReadOnlyList<string>? SelectedAppFileNames,
        IReadOnlyList<StagedCopyItem>? StagedItems,
        ConflictPolicy ConflictPolicy = ConflictPolicy.SkipExisting);
    private sealed record ResetWorkspaceApiRequest(string? WorkspacePath, WorkspaceResetScope Scope = WorkspaceResetScope.ExecutablesOnly);
    private sealed record SavePromptApiRequest(string? Markdown);
    private sealed record SaveJsonApiRequest(string? RawJson, string? DefaultName);
    private sealed record BuildWorkspaceApiRequest(string WorkspaceId, string? OutputDir);
    private sealed record ExportPkgApiRequest(string WorkspaceId, string? Target, string? OutputPath);
    private sealed record DialogFolderRequest(string? Title, string? InitialDir);
    private sealed record DialogFilesRequest(string? Title, string? FilterLabel, string? FilterPattern, bool AllowMultiple = true, string? InitialDir = null);
    private sealed record UpdateSettingsApiRequest(
        string? ThemeMode,
        string? MbyExecutableOverride,
        string? MelomgExecutableOverride,
        string? DefaultBuildOutputDir,
        string? SelfUpdateRepoOwner,
        string? SelfUpdateRepoName,
        bool? OverrideWebAppTheme = null);
    private sealed record OverrideThemeApiRequest(bool OverrideWebAppTheme);
}
