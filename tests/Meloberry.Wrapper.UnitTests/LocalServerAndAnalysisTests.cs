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
    public async Task Server_EnforcesTokenAndPathConfinement_AndCapturesAnalysis()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_server!.BaseUrl) };

        // Missing token must fail with 403.
        var unauth = await http.GetAsync("/api/bootstrap");
        Assert.Equal(HttpStatusCode.Forbidden, unauth.StatusCode);

        // Authorized bootstrap succeeds.
        using var bootReq = new HttpRequestMessage(HttpMethod.Get, "/api/bootstrap");
        bootReq.Headers.Add("X-Mby-Token", _server.AuthToken);
        var bootResp = await http.SendAsync(bootReq);
        Assert.Equal(HttpStatusCode.OK, bootResp.StatusCode);

        // Mount dummy WebApp.
        var webDir = Path.Combine(_tempRoot, "sample_web");
        Directory.CreateDirectory(webDir);
        await File.WriteAllTextAsync(Path.Combine(webDir, "index.html"), "<html>ok</html>");
        await File.WriteAllTextAsync(Path.Combine(webDir, "app.js"), "console.log('ok');");

        using var openReq = new HttpRequestMessage(HttpMethod.Post, "/api/viewer/open")
        {
            Content = new StringContent($"{{\"path\":{System.Text.Json.JsonSerializer.Serialize(webDir)}}}", Encoding.UTF8, "application/json")
        };
        openReq.Headers.Add("X-Mby-Token", _server.AuthToken);
        var openResp = await http.SendAsync(openReq);
        Assert.Equal(HttpStatusCode.OK, openResp.StatusCode);
        var openJson = await openResp.Content.ReadAsStringAsync();
        using var openDoc = System.Text.Json.JsonDocument.Parse(openJson);
        var viewerUrl = openDoc.RootElement.GetProperty("url").GetString()!;

        // Mounted WebApp serves index.html cleanly.
        var indexResp = await http.GetAsync(viewerUrl);
        Assert.Equal(HttpStatusCode.OK, indexResp.StatusCode);

        // Traversal attempt outside mounted folder fails 404.
        var traversal = await http.GetAsync(viewerUrl.Replace("index.html", "../data/wrapper-settings.json"));
        Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);

        // Same-origin POST /api/analysis persists report files inside webDir/reports/.
        using var analysisReq = new HttpRequestMessage(HttpMethod.Post, "/api/analysis")
        {
            Content = new StringContent("{\"report\":{\"score\":100},\"markdown\":\"# Analysis\"}", Encoding.UTF8, "application/json")
        };
        analysisReq.Headers.TryAddWithoutValidation("Origin", _server.BaseUrl.TrimEnd('/'));
        var analysisResp = await http.SendAsync(analysisReq);
        Assert.Equal(HttpStatusCode.OK, analysisResp.StatusCode);

        var reportsDir = Path.Combine(webDir, "reports");
        Assert.Single(Directory.GetFiles(reportsDir, "*.analysis.json"));
        Assert.Single(Directory.GetFiles(reportsDir, "*.analysis.md"));
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

    private sealed class NoopShutdown : IAppShutdownCoordinator { public void RequestShutdown() { } }
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
