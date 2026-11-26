using System.Net;
using System.Text;
using Meloberry.Wrapper.Application;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure;
using Meloberry.Wrapper.Infrastructure.Server;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.Wrapper.UnitTests;

public sealed class LocalServerAndAnalysisTests : IAsyncLifetime
{
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
            Assert.True(iconsDoc.RootElement.TryGetProperty("specs/callout-pearl", out _));
            Assert.True(iconsDoc.RootElement.TryGetProperty("specs/case", out _));
            Assert.True(iconsDoc.RootElement.TryGetProperty("specs/match", out _));
            Assert.True(iconsDoc.RootElement.TryGetProperty("specs/numeric", out _));
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

        // Authorized bootstrap succeeds and advertises viewerOrigin.
        using var bootReq = new HttpRequestMessage(HttpMethod.Get, "/api/bootstrap");
        bootReq.Headers.Add("X-Mby-Token", _server.AuthToken);
        var bootResp = await shellHttp.SendAsync(bootReq);
        Assert.Equal(HttpStatusCode.OK, bootResp.StatusCode);
        var bootJson = await bootResp.Content.ReadAsStringAsync();
        Assert.Contains(_server.ViewerBaseUrl.TrimEnd('/'), bootJson);

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

    private sealed class NoopShutdown : IAppShutdownCoordinator
    {
        public void RequestShutdown() { }
        public void RequestShutdownAndLaunch(string executablePath, string arguments) { }
    }
    private sealed class NoopDialogs : INativeDialogService
    {
        public Task<string?> PickFolderAsync(string title, string? initialDir = null, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> PickFilesAsync(string title, string filterLabel, string filterPattern, bool allowMultiple = true, string? initialDir = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> SaveFileAsync(string title, string defaultFileName, string filterLabel, string filterExtension, string? initialDir = null, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class NoopLauncher : IExternalUrlLauncher
    {
        public Result OpenAllowlistedUrl(string url) => Result.Success();
        public Result OpenFolder(string folderPath) => Result.Success();
    }
}
