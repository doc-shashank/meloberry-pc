using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meloberry.Wrapper.Application;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure;
using Meloberry.Wrapper.Infrastructure.GitHub;
using Meloberry.Wrapper.Infrastructure.Server;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.Wrapper.UnitTests;

public sealed partial class LocalServerAndAnalysisTests : IAsyncLifetime
{
    [GeneratedRegex(@"(?:import\s+[^""']*?\s+from\s+|import\s*)[""'](\.[^""']+)[""']", RegexOptions.CultureInvariant)]
    private static partial Regex EsModuleImportRegex();

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_srv_test_{Guid.NewGuid():N}");
    private ServiceProvider? _provider;
    private LocalHttpWrapperServer? _server;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        var services = new ServiceCollection();
        services.AddSingleton<IAppShutdownCoordinator>(new NoopShutdown());
        services.AddSingleton<INativeDialogService>(new NoopDialogs());
        services.AddSingleton<IExternalUrlLauncher>(new NoopLauncher());
        services.AddWrapperApplication(typeof(LocalHttpWrapperServer).Assembly);
        services.AddWrapperInfrastructure(Path.Combine(_tempRoot, "data"));

        _provider = services.BuildServiceProvider();
        _server = _provider.GetRequiredService<LocalHttpWrapperServer>();
        _server.Start();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Server_EnforcesDualPortOriginIsolation_AndCapturesAnalysis()
    {
        using var shellHttp = new HttpClient { BaseAddress = new Uri(_server!.BaseUrl) };
        using var viewerHttp = new HttpClient { BaseAddress = new Uri(_server.ViewerBaseUrl) };
        Assert.NotEqual(_server.Port, _server.ViewerPort);

        // Shell GET / has frame-ancestors 'none', X-Frame-Options: DENY, Cache-Control: no-store, and real injected version.
        var rootResp = await shellHttp.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, rootResp.StatusCode);
        Assert.Equal("DENY", rootResp.Headers.GetValues("X-Frame-Options").Single());
        Assert.True(rootResp.Headers.CacheControl?.NoStore);
        var shellHtml = await rootResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("__MBY_VERSION_VALUE__", shellHtml);
        Assert.DoesNotContain("__MBY_PRODUCT_NAME__", shellHtml);
        Assert.DoesNotContain("v1.0.0", shellHtml);

        // Icon registry serves UI, parent specs (incl. live case/match/numeric), and derivative subtype icons.
        var iconsJsonResp = await shellHttp.GetAsync("/shell/icons.json");
        Assert.Equal(HttpStatusCode.OK, iconsJsonResp.StatusCode);
        using (var iconsDoc = System.Text.Json.JsonDocument.Parse(await iconsJsonResp.Content.ReadAsStringAsync()))
        {
            Assert.True(iconsDoc.RootElement.TryGetProperty("ui/nav-setup", out _));
            Assert.True(iconsDoc.RootElement.TryGetProperty("ui/distribute", out _));
            Assert.True(iconsDoc.RootElement.TryGetProperty("ui/article", out _));
            Assert.True(iconsDoc.RootElement.TryGetProperty("ui/quiz", out _));
            Assert.True(iconsDoc.RootElement.TryGetProperty("ui/arrow-up", out _));
            Assert.True(iconsDoc.RootElement.TryGetProperty("ui/save", out _));
            Assert.True(iconsDoc.RootElement.TryGetProperty("ui/folder-open", out _));
            Assert.True(iconsDoc.RootElement.TryGetProperty("ui/copy", out _));
            Assert.True(iconsDoc.RootElement.TryGetProperty("ui/code", out _));
            Assert.True(iconsDoc.RootElement.TryGetProperty("ui/wand", out _));
            Assert.True(iconsDoc.RootElement.TryGetProperty("ui/check-circle", out _));
            Assert.True(iconsDoc.RootElement.TryGetProperty("ui/block-generic", out _));
            Assert.True(iconsDoc.RootElement.TryGetProperty("ui/question-generic", out _));
            Assert.False(iconsDoc.RootElement.TryGetProperty("specs/callout-pearl", out _));
        }
        var svgResp = await shellHttp.GetAsync("/shell/icons/ui/nav-setup.svg");
        Assert.Equal(HttpStatusCode.OK, svgResp.StatusCode);
        Assert.Contains("image/svg+xml", svgResp.Content.Headers.ContentType?.ToString() ?? string.Empty);

        // Missing token must fail with 403.
        var unauth = await shellHttp.GetAsync("/api/bootstrap");
        Assert.Equal(HttpStatusCode.Forbidden, unauth.StatusCode);

        // ShellPort rejects /api/analysis and /viewer/*.
        var shellAnalysis = await shellHttp.PostAsync("/api/analysis", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, shellAnalysis.StatusCode);

        // Authorized bootstrap succeeds and advertises viewerOrigin, paperThemeFallbackCss, and resolvedTools.
        using var bootReq = new HttpRequestMessage(HttpMethod.Get, "/api/bootstrap");
        bootReq.Headers.Add("X-Mby-Token", _server.AuthToken);
        var bootResp = await shellHttp.SendAsync(bootReq);
        Assert.Equal(HttpStatusCode.OK, bootResp.StatusCode);
        var bootJson = await bootResp.Content.ReadAsStringAsync();
        Assert.Contains(_server.ViewerBaseUrl.TrimEnd('/'), bootJson);
        using (var bootDoc = System.Text.Json.JsonDocument.Parse(bootJson))
        {
            Assert.Contains("html[data-theme=\"paper\"]", bootDoc.RootElement.GetProperty("paperThemeFallbackCss").GetString() ?? string.Empty);
            var tools = bootDoc.RootElement.GetProperty("resolvedTools");
            Assert.True(tools.TryGetProperty("mby", out _));
            Assert.True(tools.TryGetProperty("melomg", out _));
        }

        // /shell/shell.css concatenates base styles with paper-theme.css, and /api/apps includes resolvedTools.
        var cssResp = await shellHttp.GetAsync("/shell/shell.css");
        Assert.Equal(HttpStatusCode.OK, cssResp.StatusCode);
        var cssText = await cssResp.Content.ReadAsStringAsync();
        Assert.Contains("html[data-theme=\"paper\"]", cssText);
        Assert.Contains("--callout-pearl-bg", cssText);

        using var appsReq = new HttpRequestMessage(HttpMethod.Get, "/api/apps");
        appsReq.Headers.Add("X-Mby-Token", _server.AuthToken);
        var appsResp = await shellHttp.SendAsync(appsReq);
        Assert.Equal(HttpStatusCode.OK, appsResp.StatusCode);
        using (var appsDoc = System.Text.Json.JsonDocument.Parse(await appsResp.Content.ReadAsStringAsync()))
        {
            Assert.True(appsDoc.RootElement.TryGetProperty("resolvedTools", out _));
        }

        // Mount dummy WebApp.
        var webDir = Path.Combine(_tempRoot, "sample_web");
        Directory.CreateDirectory(webDir);
        await File.WriteAllTextAsync(Path.Combine(webDir, "index.html"), "<html><body>ok</body></html>");
        await File.WriteAllTextAsync(Path.Combine(webDir, "app.js"), "console.log('ok');");

        using var openReq = new HttpRequestMessage(HttpMethod.Post, "/api/viewer/open")
        {
            Content = new StringContent($"{{\"path\":{System.Text.Json.JsonSerializer.Serialize(webDir)}}}", Encoding.UTF8, "application/json")
        };
        openReq.Headers.Add("X-Mby-Token", _server.AuthToken);
        var openResp = await shellHttp.SendAsync(openReq);
        Assert.Equal(HttpStatusCode.OK, openResp.StatusCode);
        var openJson = await openResp.Content.ReadAsStringAsync();
        using var openDoc = System.Text.Json.JsonDocument.Parse(openJson);
        var viewerUrl = openDoc.RootElement.GetProperty("url").GetString()!;
        Assert.StartsWith(_server.ViewerBaseUrl, viewerUrl, StringComparison.OrdinalIgnoreCase);

        // ViewerPort rejects privileged shell routes (/ and /api/bootstrap).
        Assert.Equal(HttpStatusCode.NotFound, (await viewerHttp.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewerHttp.GetAsync("/api/bootstrap")).StatusCode);

        // Mounted WebApp serves index.html cleanly with theme bridge injected, and ShellPort rejects /viewer/*.
        var indexResp = await viewerHttp.GetAsync(viewerUrl);
        Assert.Equal(HttpStatusCode.OK, indexResp.StatusCode);
        Assert.Contains("mby-theme-override", await indexResp.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await shellHttp.GetAsync(new Uri(viewerUrl).AbsolutePath)).StatusCode);

        // HEAD /viewer/<slotId>/index.html returns 200 OK with empty response body.
        using var headReq = new HttpRequestMessage(HttpMethod.Head, viewerUrl);
        var headResp = await viewerHttp.SendAsync(headReq);
        Assert.Equal(HttpStatusCode.OK, headResp.StatusCode);
        Assert.Empty(await headResp.Content.ReadAsByteArrayAsync());

        // Traversal attempt and invalid path chars outside mounted folder fail 404 (not 500).
        var traversal = await viewerHttp.GetAsync(viewerUrl.Replace("index.html", "../data/wrapper-settings.json"));
        Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);
        var invalidChars = await viewerHttp.GetAsync(viewerUrl.Replace("index.html", "bad*path.html"));
        Assert.Equal(HttpStatusCode.NotFound, invalidChars.StatusCode);

        // Missing Origin + missing Referer on /api/analysis must be denied (Bug #27).
        var noOriginResp = await viewerHttp.PostAsync("/api/analysis", new StringContent("{\"report\":{\"score\":1}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, noOriginResp.StatusCode);

        // localhost Origin on ViewerPort succeeds for MD-only analysis and returns .md path (Bug #27).
        using var mdReq = new HttpRequestMessage(HttpMethod.Post, "/api/analysis")
        {
            Content = new StringContent("{\"markdown\":\"# Summary Only\"}", Encoding.UTF8, "application/json")
        };
        mdReq.Headers.TryAddWithoutValidation("Origin", $"http://localhost:{_server.ViewerPort}");
        var mdResp = await viewerHttp.SendAsync(mdReq);
        Assert.Equal(HttpStatusCode.OK, mdResp.StatusCode);
        var mdSavedPath = System.Text.Json.JsonSerializer.Deserialize<string>(await mdResp.Content.ReadAsStringAsync())!;
        Assert.EndsWith(".analysis.md", mdSavedPath);
        Assert.True(File.Exists(mdSavedPath));

        // Same-origin POST /api/analysis persists report files inside webDir/reports/.
        using var analysisReq = new HttpRequestMessage(HttpMethod.Post, "/api/analysis")
        {
            Content = new StringContent("{\"report\":{\"score\":100},\"markdown\":\"# Analysis\"}", Encoding.UTF8, "application/json")
        };
        analysisReq.Headers.TryAddWithoutValidation("Origin", _server.ViewerBaseUrl.TrimEnd('/'));
        var analysisResp = await viewerHttp.SendAsync(analysisReq);
        Assert.Equal(HttpStatusCode.OK, analysisResp.StatusCode);

        var reportsDir = Path.Combine(webDir, "reports");
        Assert.Single(Directory.GetFiles(reportsDir, "*.analysis.json"));
        Assert.Equal(2, Directory.GetFiles(reportsDir, "*.analysis.md").Length);

        // Bug #27: >10 MB body on /api/analysis returns 413 Payload Too Large.
        var bigPayload = new string('a', 10 * 1024 * 1024 + 64);
        using var bigReq = new HttpRequestMessage(HttpMethod.Post, "/api/analysis")
        {
            Content = new StringContent(bigPayload, Encoding.UTF8, "application/json")
        };
        bigReq.Headers.TryAddWithoutValidation("Origin", _server.ViewerBaseUrl.TrimEnd('/'));
        var bigResp = await viewerHttp.SendAsync(bigReq);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, bigResp.StatusCode);
    }

    [Fact]
    public async Task Server_SseStream_HandlesConcurrentBroadcastsSafely()
    {
        using var shellHttp = new HttpClient { BaseAddress = new Uri(_server!.BaseUrl) };
        using var sseReq = new HttpRequestMessage(HttpMethod.Get, $"/api/events?token={Uri.EscapeDataString(_server.AuthToken)}");
        using var sseResp = await shellHttp.SendAsync(sseReq, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, sseResp.StatusCode);

        await using var stream = await sseResp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var hello = await reader.ReadLineAsync();
        Assert.Equal(": connected", hello);
    }

    [Fact]
    public async Task Server_EnumeratesVisibleSubfolders_AndInjectsEarlyThemeIntoMountedWebAppHead()
    {
        using var shellHttp = new HttpClient { BaseAddress = new Uri(_server!.BaseUrl) };
        using var viewerHttp = new HttpClient { BaseAddress = new Uri(_server.ViewerBaseUrl) };

        // Setup workspace with visible + dot/hidden subdirectories
        var wsRoot = Path.Combine(_tempRoot, "subfolders_ws");
        Directory.CreateDirectory(wsRoot);
        Directory.CreateDirectory(Path.Combine(wsRoot, "Chapter1"));
        Directory.CreateDirectory(Path.Combine(wsRoot, "Chapter2"));
        Directory.CreateDirectory(Path.Combine(wsRoot, ".git"));
        var hiddenDir = Directory.CreateDirectory(Path.Combine(wsRoot, "SecretHidden"));
        hiddenDir.Attributes |= FileAttributes.Hidden;

        using var subReq = new HttpRequestMessage(HttpMethod.Get, $"/api/workspace/subfolders?workspacePath={Uri.EscapeDataString(wsRoot)}");
        subReq.Headers.Add("X-Mby-Token", _server.AuthToken);
        var subResp = await shellHttp.SendAsync(subReq);
        Assert.Equal(HttpStatusCode.OK, subResp.StatusCode);
        using (var subDoc = System.Text.Json.JsonDocument.Parse(await subResp.Content.ReadAsStringAsync()))
        {
            Assert.True(subDoc.RootElement.GetProperty("hasSubfolders").GetBoolean());
            var names = subDoc.RootElement.GetProperty("subfolders")
                .EnumerateArray()
                .Select(e => e.GetProperty("name").GetString())
                .ToList();
            Assert.Contains("Chapter1", names);
            Assert.Contains("Chapter2", names);
            Assert.DoesNotContain(".git", names);
            Assert.DoesNotContain("SecretHidden", names);
        }

        // Enable paper theme override in settings
        using var putSetReq = new HttpRequestMessage(HttpMethod.Put, "/api/settings")
        {
            Content = new StringContent("{\"themeMode\":\"paper\",\"overrideWebAppTheme\":true}", Encoding.UTF8, "application/json")
        };
        putSetReq.Headers.Add("X-Mby-Token", _server.AuthToken);
        var putSetResp = await shellHttp.SendAsync(putSetReq);
        Assert.Equal(HttpStatusCode.OK, putSetResp.StatusCode);

        var webDir = Path.Combine(_tempRoot, "themed_web");
        Directory.CreateDirectory(webDir);
        await File.WriteAllTextAsync(Path.Combine(webDir, "index.html"), "<!DOCTYPE html><html><head><title>App</title></head><body>ok</body></html>");
        await File.WriteAllTextAsync(Path.Combine(webDir, "app.js"), "console.log('themed');");

        using var openReq = new HttpRequestMessage(HttpMethod.Post, "/api/viewer/open")
        {
            Content = new StringContent($"{{\"path\":{System.Text.Json.JsonSerializer.Serialize(webDir)}}}", Encoding.UTF8, "application/json")
        };
        openReq.Headers.Add("X-Mby-Token", _server.AuthToken);
        var openResp = await shellHttp.SendAsync(openReq);
        Assert.Equal(HttpStatusCode.OK, openResp.StatusCode);
        using var openDoc = System.Text.Json.JsonDocument.Parse(await openResp.Content.ReadAsStringAsync());
        var viewerUrl = openDoc.RootElement.GetProperty("url").GetString()!;

        var html = await viewerHttp.GetStringAsync(viewerUrl);
        var headCloseIdx = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        var earlyAttrIdx = html.IndexOf("setAttribute('data-theme',\"paper\")", StringComparison.Ordinal);
        Assert.True(earlyAttrIdx > 0 && earlyAttrIdx < headCloseIdx, "Expected early theme script inside <head> before </head>.");
        Assert.Contains("mby-wrapper-theme-fallback", html);
        Assert.Contains("mby-theme-applied", html);
    }

    [Fact]
    public void HttpEnvelope_MapsAllErrorTypesToExpectedStatusCodes()
    {
        Assert.Equal(400, HttpEnvelope.MapStatusCode(ErrorType.Validation));
        Assert.Equal(404, HttpEnvelope.MapStatusCode(ErrorType.NotFound));
        Assert.Equal(409, HttpEnvelope.MapStatusCode(ErrorType.Conflict));
        Assert.Equal(403, HttpEnvelope.MapStatusCode(ErrorType.Security));
        Assert.Equal(502, HttpEnvelope.MapStatusCode(ErrorType.Network));
        Assert.Equal(422, HttpEnvelope.MapStatusCode(ErrorType.ExternalProcess));
        Assert.Equal(500, HttpEnvelope.MapStatusCode(ErrorType.Io));
    }

    [Fact]
    public async Task SseEventHub_BroadcastsToSubscribers_AndCompletesCleanly()
    {
        var hub = _provider!.GetRequiredService<SseEventHub>();
        using var shellHttp1 = new HttpClient { BaseAddress = new Uri(_server!.BaseUrl) };
        using var shellHttp2 = new HttpClient { BaseAddress = new Uri(_server.BaseUrl) };

        using var req1 = new HttpRequestMessage(HttpMethod.Get, $"/api/events?token={Uri.EscapeDataString(_server.AuthToken)}");
        using var req2 = new HttpRequestMessage(HttpMethod.Get, $"/api/events?token={Uri.EscapeDataString(_server.AuthToken)}");
        using var resp1 = await shellHttp1.SendAsync(req1, HttpCompletionOption.ResponseHeadersRead);
        using var resp2 = await shellHttp2.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

        await using var s1 = await resp1.Content.ReadAsStreamAsync();
        await using var s2 = await resp2.Content.ReadAsStreamAsync();
        using var r1 = new StreamReader(s1, Encoding.UTF8);
        using var r2 = new StreamReader(s2, Encoding.UTF8);

        Assert.Equal(": connected", await r1.ReadLineAsync());
        Assert.Equal(string.Empty, await r1.ReadLineAsync());
        Assert.Equal(": connected", await r2.ReadLineAsync());
        Assert.Equal(string.Empty, await r2.ReadLineAsync());
        Assert.True(hub.SubscriberCount >= 2);

        var evt = new WorkspaceFileChangeEvent("workspace", "created", "AGENTS.md", Path.Combine(_tempRoot, "AGENTS.md"), DateTimeOffset.UtcNow);
        hub.Broadcast("fschange", evt);

        Assert.Equal("event: fschange", await r1.ReadLineAsync());
        var data1 = await r1.ReadLineAsync();
        Assert.StartsWith("data: ", data1);
        Assert.Contains("AGENTS.md", data1);

        Assert.Equal("event: fschange", await r2.ReadLineAsync());
        var data2 = await r2.ReadLineAsync();
        Assert.StartsWith("data: ", data2);
        Assert.Contains("AGENTS.md", data2);

        hub.CompleteAll();
        Assert.Equal(0, hub.SubscriberCount);
    }

    [Fact]
    public async Task StaticAssetHost_BundlesAllStylesheets_AndServesFiveStepWorkflowAssets()
    {
        using var shellHttp = new HttpClient { BaseAddress = new Uri(_server!.BaseUrl) };

        var htmlResp = await shellHttp.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, htmlResp.StatusCode);
        var html = await htmlResp.Content.ReadAsStringAsync();
        Assert.Contains($"<meta name=\"mby-token\" content=\"{_server.AuthToken}\"", html);
        Assert.Contains("<script type=\"module\" src=\"/shell/js/main.js\"></script>", html);
        Assert.DoesNotContain("window.__MBY_TOKEN__", html);
        Assert.DoesNotContain("/shell/shell.js", html);

        Assert.Contains("class=\"workflow-stepper", html);
        foreach (var step in new[] { "setup", "format", "organize", "review", "viewer" })
        {
            Assert.Contains($"data-view=\"{step}\"", html);
        }

        foreach (var viewId in new[] { "id=\"viewSetup\"", "id=\"viewFormat\"", "id=\"viewOrganize\"", "id=\"viewReview\"", "id=\"viewViewer\"" })
        {
            Assert.Contains(viewId, html);
        }

        foreach (var heading in new[] { "Setup Workspace", "Suit Yourself", "Decide the organization", "Review and Finalize" })
        {
            Assert.Contains(heading, html);
        }

        Assert.DoesNotContain("btnResetWorkspace", html);
        Assert.DoesNotContain("id=\"resetMenuWrap\"", html);
        Assert.DoesNotContain("id=\"viewSettings\"", html);
        Assert.DoesNotContain("id=\"btnSaveSettings\"", html);
        Assert.DoesNotContain("mbypreset", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id=\"settingsModal\"", html);
        Assert.Contains("id=\"settingsSavedStatus\"", html);
        Assert.Contains("id=\"activeWsBtn\"", html);
        Assert.Contains("id=\"wsTreePopover\"", html);

        var formatStart = html.IndexOf("id=\"viewFormat\"", StringComparison.Ordinal);
        var organizeStart = html.IndexOf("id=\"viewOrganize\"", StringComparison.Ordinal);
        var reviewStart = html.IndexOf("id=\"viewReview\"", StringComparison.Ordinal);
        var viewerStart = html.IndexOf("id=\"viewViewer\"", StringComparison.Ordinal);
        Assert.True(formatStart > 0 && organizeStart > formatStart && reviewStart > organizeStart && viewerStart > reviewStart);

        var containerTabsIdx = html.IndexOf("id=\"containerTabs\"", StringComparison.Ordinal);
        Assert.True(containerTabsIdx > formatStart && containerTabsIdx < organizeStart, "Expected #containerTabs inside #viewFormat.");

        foreach (var organizeToolbarId in new[] { "id=\"presetMenuWrap\"", "id=\"btnSavePreset\"", "id=\"orgAddNewWrap\"", "id=\"btnRefreshSpecs\"" })
        {
            var idx = html.IndexOf(organizeToolbarId, StringComparison.Ordinal);
            Assert.True(idx > organizeStart && idx < reviewStart, $"Expected '{organizeToolbarId}' inside #viewOrganize.");
        }

        foreach (var reviewId in new[] { "id=\"readyWsMenuWrap\"", "id=\"actionsMenuWrap\"", "id=\"promptOptionsWrap\"", "id=\"chkUseMelomg\"" })
        {
            var idx = html.IndexOf(reviewId, StringComparison.Ordinal);
            Assert.True(idx > reviewStart && idx < viewerStart, $"Expected '{reviewId}' inside #viewReview.");
        }

        var oldJsResp = await shellHttp.GetAsync("/shell/shell.js");
        Assert.Equal(HttpStatusCode.NotFound, oldJsResp.StatusCode);

        var cssResp = await shellHttp.GetAsync("/shell/shell.css");
        Assert.Equal(HttpStatusCode.OK, cssResp.StatusCode);
        Assert.Contains("text/css", cssResp.Content.Headers.ContentType?.ToString() ?? string.Empty);
        var css = await cssResp.Content.ReadAsStringAsync();

        string[] orderedMarkers =
        [
            ":root {",
            "html[data-theme=\"paper\"]",
            "::-webkit-scrollbar",
            ".app-shell",
            "Segoe Script",
            ".sync-wipe-in",
            ".titlebar {",
            ".workflow-stepper",
            ".ws-strip-label-btn",
            ".setup-grid",
            ".review-actions-bar",
            ".org-canvas-track",
            ".org-track-centered",
            ".org-spec-list",
            ".org-drag-ghost",
            ".viewer-full",
            ".add-spec-popover.kind-quiz",
            ".modal-backdrop",
            ".settings-modal-card",
            ".ws-tree-popover"
        ];

        var lastIdx = -1;
        foreach (var marker in orderedMarkers)
        {
            var idx = css.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(idx > lastIdx, $"Expected CSS marker '{marker}' after index {lastIdx}, found at {idx}.");
            lastIdx = idx;
        }

        Assert.Contains("html[data-theme=\"paper\"]", StaticAssetHost.PaperThemeFallbackCss);
        using var bootReq = new HttpRequestMessage(HttpMethod.Get, "/api/bootstrap");
        bootReq.Headers.Add("X-Mby-Token", _server.AuthToken);
        var bootResp = await shellHttp.SendAsync(bootReq);
        Assert.Equal(HttpStatusCode.OK, bootResp.StatusCode);
        using var bootDoc = JsonDocument.Parse(await bootResp.Content.ReadAsStringAsync());
        Assert.Equal(StaticAssetHost.PaperThemeFallbackCss, bootDoc.RootElement.GetProperty("paperThemeFallbackCss").GetString());

        var visited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Uri>();
        queue.Enqueue(new Uri(new Uri(_server.BaseUrl), "/shell/js/main.js"));

        while (queue.Count > 0)
        {
            var currentUri = queue.Dequeue();
            var pathKey = currentUri.AbsolutePath;
            if (visited.ContainsKey(pathKey))
            {
                continue;
            }

            var modResp = await shellHttp.GetAsync(currentUri);
            Assert.True(modResp.StatusCode == HttpStatusCode.OK, $"Expected 200 OK for module '{pathKey}', got {modResp.StatusCode}.");
            Assert.Contains("application/javascript", modResp.Content.Headers.ContentType?.ToString() ?? string.Empty);

            var source = await modResp.Content.ReadAsStringAsync();
            visited[pathKey] = source;

            foreach (Match m in EsModuleImportRegex().Matches(source))
            {
                var relSpecifier = m.Groups[1].Value;
                var nextUri = new Uri(currentUri, relSpecifier);
                Assert.StartsWith("/shell/js/", nextUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
                if (!visited.ContainsKey(nextUri.AbsolutePath))
                {
                    queue.Enqueue(nextUri);
                }
            }
        }

        Assert.True(visited.Count >= 16, $"Expected at least 16 ES modules in graph, found {visited.Count}.");
        Assert.True(visited.ContainsKey("/shell/js/ui/workspaceTree.js"));
        Assert.False(visited.ContainsKey("/shell/js/views/prompt/numberWipe.js"));
        Assert.True(visited.ContainsKey("/shell/js/views/prompt/organizeDrag.js"));
        Assert.True(visited.ContainsKey("/shell/js/views/prompt/organizeFlip.js"));
        Assert.True(visited.ContainsKey("/shell/js/views/prompt/organize.js"));
        Assert.True(visited.ContainsKey("/shell/js/views/prompt/format.js"));
        Assert.True(visited.ContainsKey("/shell/js/views/prompt/review.js"));
        var allJs = string.Join("\n", visited.Values);
        Assert.DoesNotContain("mbypreset", allJs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("window.__onWindowState", allJs);
        Assert.Contains("window.__onNativeDrop", allJs);

        Assert.Equal(HttpStatusCode.NotFound, (await shellHttp.GetAsync("/shell/js/../shell.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await shellHttp.GetAsync("/shell/js/../../missing.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await shellHttp.GetAsync("/shell/js/does-not-exist.js")).StatusCode);
    }

    [Fact]
    public async Task Server_WorkspaceTreeEndpoint_BoundsNavigationInsideActiveWorkspace()
    {
        using var shellHttp = new HttpClient { BaseAddress = new Uri(_server!.BaseUrl) };
        var rootDir = Path.Combine(_tempRoot, "tree_root");
        var subA = Path.Combine(rootDir, "Alpha");
        var subANested = Path.Combine(subA, "Deep");
        Directory.CreateDirectory(subANested);
        Directory.CreateDirectory(Path.Combine(rootDir, "Beta"));
        Directory.CreateDirectory(Path.Combine(rootDir, ".hiddenDot"));
        await File.WriteAllTextAsync(Path.Combine(rootDir, "notes.md"), "hi");

        using var selReq = new HttpRequestMessage(HttpMethod.Post, "/api/workspace/select")
        {
            Content = new StringContent($"{{\"path\":{JsonSerializer.Serialize(rootDir)}}}", Encoding.UTF8, "application/json")
        };
        selReq.Headers.Add("X-Mby-Token", _server.AuthToken);
        var selResp = await shellHttp.SendAsync(selReq);
        Assert.Equal(HttpStatusCode.OK, selResp.StatusCode);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/workspace/tree?path={Uri.EscapeDataString(rootDir)}");
        req.Headers.Add("X-Mby-Token", _server.AuthToken);
        var resp = await shellHttp.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("parentPath").ValueKind);

        var folders = doc.RootElement.GetProperty("folders").EnumerateArray().ToList();
        Assert.Equal(2, folders.Count);
        Assert.Equal("Alpha", folders[0].GetProperty("name").GetString());
        Assert.True(folders[0].GetProperty("hasSubfolders").GetBoolean());
        Assert.Equal("Beta", folders[1].GetProperty("name").GetString());
        Assert.False(folders[1].GetProperty("hasSubfolders").GetBoolean());

        var files = doc.RootElement.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.GetProperty("name").GetString() == "mby-pc.bat");
        Assert.Contains(files, f => f.GetProperty("name").GetString() == "notes.md");

        using var subReq = new HttpRequestMessage(HttpMethod.Get, $"/api/workspace/tree?path={Uri.EscapeDataString(subA)}");
        subReq.Headers.Add("X-Mby-Token", _server.AuthToken);
        var subResp = await shellHttp.SendAsync(subReq);
        Assert.Equal(HttpStatusCode.OK, subResp.StatusCode);
        using var subDoc = JsonDocument.Parse(await subResp.Content.ReadAsStringAsync());
        Assert.Equal(rootDir, subDoc.RootElement.GetProperty("parentPath").GetString(), StringComparer.OrdinalIgnoreCase);

        using var outsideReq = new HttpRequestMessage(HttpMethod.Get, $"/api/workspace/tree?path={Uri.EscapeDataString(_tempRoot)}");
        outsideReq.Headers.Add("X-Mby-Token", _server.AuthToken);
        var outsideResp = await shellHttp.SendAsync(outsideReq);
        Assert.Equal(HttpStatusCode.BadRequest, outsideResp.StatusCode);

        using var badReq = new HttpRequestMessage(HttpMethod.Get, $"/api/workspace/tree?path={Uri.EscapeDataString(Path.Combine(rootDir, "missing_tree"))}");
        badReq.Headers.Add("X-Mby-Token", _server.AuthToken);
        var badResp = await shellHttp.SendAsync(badReq);
        Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);
    }

    [Fact]
    public async Task Server_CliSignalFile_BroadcastsWebAppBuiltSseEvent()
    {
        using var shellHttp = new HttpClient { BaseAddress = new Uri(_server!.BaseUrl) };
        var wsDir = Path.Combine(_tempRoot, "signal_ws");
        var webDir = Path.Combine(wsDir, "lesson_web");
        Directory.CreateDirectory(webDir);
        await File.WriteAllTextAsync(Path.Combine(webDir, "index.html"), "<html><body>Auto WebApp</body></html>");
        await File.WriteAllTextAsync(Path.Combine(webDir, "app.js"), "console.log('auto');");

        using var selReq = new HttpRequestMessage(HttpMethod.Post, "/api/workspace/select")
        {
            Content = new StringContent($"{{\"path\":{JsonSerializer.Serialize(wsDir)}}}", Encoding.UTF8, "application/json")
        };
        selReq.Headers.Add("X-Mby-Token", _server.AuthToken);
        Assert.Equal(HttpStatusCode.OK, (await shellHttp.SendAsync(selReq)).StatusCode);

        using var sseReq = new HttpRequestMessage(HttpMethod.Get, $"/api/events?token={Uri.EscapeDataString(_server.AuthToken)}");
        using var sseResp = await shellHttp.SendAsync(sseReq, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, sseResp.StatusCode);

        await using var stream = await sseResp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal(": connected", await reader.ReadLineAsync());
        Assert.Equal(string.Empty, await reader.ReadLineAsync());

        var signalPath = Path.Combine(wsDir, MbyCliSignal.SignalFileName);
        await File.WriteAllTextAsync(signalPath, "version=1\nevent=webapp_built\nid=srv-sig-1\npath=lesson_web\ntitle=Auto Lesson\n");

        string? evtLine = null;
        string? dataLine = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (line == "event: webapp-built")
            {
                evtLine = line;
                dataLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                break;
            }
        }

        Assert.Equal("event: webapp-built", evtLine);
        Assert.StartsWith("data: ", dataLine);
        Assert.Contains(_server.ViewerBaseUrl.TrimEnd('/'), dataLine);
        Assert.Contains("Auto Lesson", dataLine);
    }

    [Fact]
    public void DependencyInjection_ResolvesAllSplitHandlersAndInfrastructureClients()
    {
        var provider = _provider!;
        Assert.NotNull(provider.GetRequiredService<ManagedAppsHandler>());
        Assert.NotNull(provider.GetRequiredService<WorkspaceSetupHandler>());
        Assert.NotNull(provider.GetRequiredService<WorkspaceMaintenanceHandler>());
        Assert.NotNull(provider.GetRequiredService<ReadyWorkspaceHandler>());
        Assert.NotNull(provider.GetRequiredService<LoadSpecCatalogHandler>());
        Assert.NotNull(provider.GetRequiredService<PromptHandlers>());
        Assert.NotNull(provider.GetRequiredService<SessionTemplateHandler>());
        Assert.NotNull(provider.GetRequiredService<ManageLibraryHandler>());
        Assert.NotNull(provider.GetRequiredService<WorkspaceActionsHandler>());
        Assert.NotNull(provider.GetRequiredService<CaptureAnalysisHandler>());
        Assert.NotNull(provider.GetRequiredService<SelfUpdateHandler>());
        Assert.NotNull(provider.GetRequiredService<SettingsAndPresetsHandler>());

        Assert.NotNull(provider.GetRequiredService<GitHubHttpClientHelper>());
        Assert.NotNull(provider.GetRequiredService<IGitHubToolDownloader>());
        Assert.NotNull(provider.GetRequiredService<IGitHubSelfUpdater>());
        Assert.NotNull(provider.GetRequiredService<IMbyCliClient>());
        Assert.NotNull(provider.GetRequiredService<IMelomgCliClient>());
        Assert.NotNull(provider.GetRequiredService<SseEventHub>());
        Assert.NotNull(provider.GetRequiredService<StaticAssetHost>());
        Assert.NotNull(provider.GetRequiredService<LocalHttpWrapperServer>());
    }

    [Fact]
    public async Task ShellHtmlCssAndJs_ExposeImagePane_DirectReadiness_AgentActivityFeed_AndRemovedInitGate()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_server!.BaseUrl) };

        var html = await http.GetStringAsync("/");
        // Phase 3: Dedicated Image tab & pane
        Assert.Contains("tabSpecContentImage", html);
        Assert.Contains("specContentImagePane", html);
        Assert.Contains("btnPickSpecImageFile", html);
        // Phase 4: Mandatory Initialize gate removed; Copy button moved into #setupStagedCard
        Assert.DoesNotContain("id=\"btnInitializeWorkspace\"", html);
        Assert.DoesNotContain("init-footer-card", html);
        Assert.Contains("id=\"btnCopyStagedItems\"", html);
        Assert.Contains("id=\"stagedCopyBar\"", html);
        // Phase 5: 3-card workflow mode picker & Direct Readiness checklist
        Assert.Contains("id=\"workflowModeCards\"", html);
        Assert.Contains("id=\"directReadinessPanel\"", html);
        Assert.Contains("id=\"liveAgentHandoffBanner\"", html);
        Assert.Contains("id=\"btnCopyLiveHandoff\"", html);
        // Phase 6: Dedicated Live Agent Activity popover
        Assert.Contains("id=\"agentActivityWrap\"", html);
        Assert.Contains("id=\"agentActivityPopover\"", html);
        Assert.Contains("id=\"agentActivityList\"", html);

        var css = await http.GetStringAsync("/shell/shell.css");
        Assert.Contains(".row-between", css);
        Assert.Contains(".agent-activity-popover", css);

        var chromeJs = await http.GetStringAsync("/shell/js/ui/chrome.js");
        Assert.Contains("recordAgentActivity", chromeJs);
        Assert.Contains("agentActivityPopover", chromeJs);
        Assert.DoesNotContain("aws.isInitialized", chromeJs);

        var mainJs = await http.GetStringAsync("/shell/js/main.js");
        Assert.Contains("recordAgentActivity", mainJs);
        Assert.Contains("payload.op === \"patch-meta\"", mainJs);
        Assert.Contains("/api/workspace/import-asset", mainJs);
        Assert.DoesNotContain("activeWorkspace.isInitialized", mainJs);

        var reviewJs = await http.GetStringAsync("/shell/js/views/prompt/review.js");
        Assert.Contains("renderDirectReadinessPanel", reviewJs);
        Assert.Contains("flushLiveDraftSave", reviewJs);
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
