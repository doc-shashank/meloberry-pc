using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Infrastructure.Server.Endpoints;

namespace Meloberry.Wrapper.Infrastructure.Server;

/// <summary>
/// Pure BCL dual-port HttpListener host: ShellPort serves the embedded Shell UI, SSE, and token-protected REST APIs;
/// ViewerPort serves isolated third-party WebApps (/viewer/&lt;slotId&gt;/) and origin-checked POST /api/analysis.
/// </summary>
public sealed class LocalHttpWrapperServer : IAsyncDisposable, IDisposable
{
    private readonly IWorkspaceFileWatcher _fileWatcher;
    private readonly SseEventHub _sseHub;
    private readonly StaticAssetHost _assets;
    private readonly AppsEndpoints _appsEndpoints;
    private readonly WorkspaceEndpoints _workspaceEndpoints;
    private readonly PromptEndpoints _promptEndpoints;
    private readonly SystemEndpoints _systemEndpoints;
    private readonly LiveControlEndpoints _liveControlEndpoints;
    private readonly ProcessCliSignalHandler _cliSignalHandler;
    private readonly LiveDraftHandler _liveDraftHandler;

    private readonly string _token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    private HttpListener? _listener;
    private HttpListener? _viewerListener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private Task? _viewerAcceptLoop;
    private int _disposed;

    public LocalHttpWrapperServer(
        IWorkspaceFileWatcher fileWatcher,
        SseEventHub sseHub,
        StaticAssetHost assets,
        AppsEndpoints appsEndpoints,
        WorkspaceEndpoints workspaceEndpoints,
        PromptEndpoints promptEndpoints,
        SystemEndpoints systemEndpoints,
        LiveControlEndpoints liveControlEndpoints,
        ProcessCliSignalHandler cliSignalHandler,
        LiveDraftHandler liveDraftHandler)
    {
        _fileWatcher = fileWatcher;
        _sseHub = sseHub;
        _assets = assets;
        _appsEndpoints = appsEndpoints;
        _workspaceEndpoints = workspaceEndpoints;
        _promptEndpoints = promptEndpoints;
        _systemEndpoints = systemEndpoints;
        _liveControlEndpoints = liveControlEndpoints;
        _cliSignalHandler = cliSignalHandler;
        _liveDraftHandler = liveDraftHandler;

        _fileWatcher.Changed += OnWorkspaceFileChanged;
        _liveDraftHandler.DraftChanged += OnLiveDraftChanged;
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

        var (shellListener, shellPort) = StartListenerOnFreePort();
        HttpListener viewerListener;
        int viewerPort;
        try
        {
            (viewerListener, viewerPort) = StartListenerOnFreePort(excludePort: shellPort);
        }
        catch
        {
            try { shellListener.Close(); } catch { }
            throw;
        }

        Port = shellPort;
        ViewerPort = viewerPort;
        _listener = shellListener;
        _viewerListener = viewerListener;

        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_listener, isViewerHost: false, _cts.Token));
        _viewerAcceptLoop = Task.Run(() => AcceptLoopAsync(_viewerListener, isViewerHost: true, _cts.Token));
    }

    private static (HttpListener Listener, int Port) StartListenerOnFreePort(int maxAttempts = 10, int? excludePort = null)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var port = FindFreeTcpPort();
            if (excludePort.HasValue && port == excludePort.Value)
            {
                continue;
            }

            var listener = new HttpListener();
            try
            {
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
                return (listener, port);
            }
            catch (Exception ex) when ((ex is HttpListenerException or SocketException) && attempt < maxAttempts - 1)
            {
                try { listener.Close(); } catch { }
            }
            catch
            {
                try { listener.Close(); } catch { }
                throw;
            }
        }

        throw new InvalidOperationException("Failed to bind a local HTTP listener port after multiple attempts.");
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
            if (!IsValidLocalHost(req.Headers["Host"], ViewerPort))
            {
                await HttpEnvelope.WriteJsonErrorAsync(ctx.Response, 403, "Security.InvalidHost", "Forbidden Host header.", ct).ConfigureAwait(false);
                return;
            }

            var path = req.Url?.AbsolutePath ?? "/";
            var method = req.HttpMethod.ToUpperInvariant();

            if (path.StartsWith("/viewer/", StringComparison.OrdinalIgnoreCase) && method is "GET" or "HEAD")
            {
                await _assets.ServeMountedWebAppAsync(ctx.Response, path, Port, method == "HEAD", ct).ConfigureAwait(false);
                return;
            }

            if (path == "/api/analysis" && method == "POST")
            {
                await _systemEndpoints.HandleAnalysisPostAsync(req, ctx.Response, ViewerPort, ct).ConfigureAwait(false);
                return;
            }

            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            try
            {
                await HttpEnvelope.WriteJsonErrorAsync(ctx.Response, 500, "Server.Unhandled", ex.Message, CancellationToken.None).ConfigureAwait(false);
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
            if (!IsValidLocalHost(req.Headers["Host"], Port))
            {
                await HttpEnvelope.WriteJsonErrorAsync(ctx.Response, 403, "Security.InvalidHost", "Forbidden Host header.", ct).ConfigureAwait(false);
                return;
            }

            var path = req.Url?.AbsolutePath ?? "/";
            var method = req.HttpMethod.ToUpperInvariant();

            if (path == "/" || path == "/index.html")
            {
                await _assets.ServeShellHtmlAsync(ctx.Response, _token, ct).ConfigureAwait(false);
                return;
            }

            if (path == "/shell/shell.css")
            {
                await _assets.ServeBundledCssAsync(ctx.Response, ct).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/shell/js/", StringComparison.OrdinalIgnoreCase) && method is "GET" or "HEAD")
            {
                await _assets.ServeJsModuleAsync(ctx.Response, path["/shell/js/".Length..], method == "HEAD", ct).ConfigureAwait(false);
                return;
            }

            if (path == "/shell/icons.json" && method is "GET" or "HEAD")
            {
                await _assets.ServeIconsJsonAsync(ctx.Response, ct).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/shell/icons/", StringComparison.OrdinalIgnoreCase) && method is "GET" or "HEAD")
            {
                await _assets.ServeIconSvgAsync(ctx.Response, path["/shell/icons/".Length..], ct).ConfigureAwait(false);
                return;
            }

            if (path == "/api/events" && method == "GET")
            {
                var queryToken = req.QueryString["token"];
                if (!string.Equals(queryToken, _token, StringComparison.Ordinal))
                {
                    await HttpEnvelope.WriteJsonErrorAsync(ctx.Response, 403, "Security.InvalidToken", "Missing or invalid SSE token.", ct).ConfigureAwait(false);
                    return;
                }

                await _sseHub.HandleStreamAsync(ctx.Response, ct).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(path, "/api/analysis", StringComparison.OrdinalIgnoreCase))
            {
                var provided = req.Headers["X-Mby-Token"];
                if (!string.Equals(provided, _token, StringComparison.Ordinal))
                {
                    await HttpEnvelope.WriteJsonErrorAsync(ctx.Response, 403, "Security.InvalidToken", "Missing or invalid X-Mby-Token header.", ct).ConfigureAwait(false);
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
                await HttpEnvelope.WriteJsonErrorAsync(ctx.Response, 500, "Server.Unhandled", ex.Message, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Response already closed.
            }
        }
    }

    private async Task RouteApiAsync(HttpListenerRequest req, HttpListenerResponse resp, string method, string path, CancellationToken ct)
    {
        if (await _appsEndpoints.TryHandleAsync(req, resp, method, path, ct).ConfigureAwait(false)) return;
        if (await _workspaceEndpoints.TryHandleAsync(req, resp, method, path, ViewerBaseUrl, ct).ConfigureAwait(false)) return;
        if (await _promptEndpoints.TryHandleAsync(req, resp, method, path, ct).ConfigureAwait(false)) return;
        if (await _liveControlEndpoints.TryHandleAsync(req, resp, method, path, ViewerBaseUrl, ct).ConfigureAwait(false)) return;
        if (await _systemEndpoints.TryHandleAsync(req, resp, method, path, ViewerBaseUrl, ct).ConfigureAwait(false)) return;

        await HttpEnvelope.WriteJsonErrorAsync(resp, 404, "Route.NotFound", $"Unknown API endpoint: {method} {path}", ct).ConfigureAwait(false);
    }

    private void OnLiveDraftChanged(LiveDraftChangedEvent evt)
    {
        _sseHub.Broadcast("draft-updated", new
        {
            workspacePath = evt.WorkspacePath,
            draft = evt.Draft,
            source = evt.Source,
            summary = evt.Summary,
            targetCompId = evt.TargetComponentId,
            targetSpecId = evt.TargetSpecId,
            op = evt.Op
        });
    }

    private void OnWorkspaceFileChanged(WorkspaceFileChangeEvent evt)
    {
        if (string.Equals(evt.Scope, "signal", StringComparison.OrdinalIgnoreCase))
        {
            _ = HandleCliSignalAsync(evt.FullPath);
            return;
        }

        _sseHub.Broadcast("fschange", evt);
    }

    private async Task HandleCliSignalAsync(string fullPath)
    {
        try
        {
            var token = _cts?.Token ?? CancellationToken.None;
            var res = await _cliSignalHandler.ProcessFileAsync(fullPath, token).ConfigureAwait(false);
            if (res is { IsSuccess: true, Value: { } built })
            {
                _sseHub.Broadcast("webapp-built", new
                {
                    slotId = built.SlotId,
                    url = $"{ViewerBaseUrl.TrimEnd('/')}{built.RelativeUrl}",
                    entry = built.Entry,
                    workspaceId = built.WorkspaceId,
                    title = built.Title,
                    signalId = built.SignalId
                });
            }
        }
        catch
        {
            // Swallow transient signal file read/mount errors so watcher loop never crashes.
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
        _liveDraftHandler.DraftChanged -= OnLiveDraftChanged;
        _sseHub.CompleteAll();

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        _listener?.Close();
        _viewerListener?.Close();

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        }

        if (_viewerAcceptLoop is not null)
        {
            try { await _viewerAcceptLoop.ConfigureAwait(false); } catch { }
        }

        _cts?.Dispose();
    }
}
