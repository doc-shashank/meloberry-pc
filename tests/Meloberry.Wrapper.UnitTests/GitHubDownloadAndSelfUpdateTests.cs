using System.Net;
using System.Security.Cryptography;
using System.Text;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.GitHub;
using Meloberry.Wrapper.Infrastructure.Workspace;
using Xunit;

namespace Meloberry.Wrapper.UnitTests;

public sealed class GitHubDownloadAndSelfUpdateTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_gh_test_{Guid.NewGuid():N}");

    [Fact]
    public async Task ToolDownloader_FallsBackToContentsApp_AndVerifiesPeAndSha256()
    {
        var paths = new WrapperDataPaths(_tempRoot);
        var fakeExe = new byte[] { (byte)'M', (byte)'Z', 0x90, 0x00, 0x42 };
        var sha = Convert.ToHexStringLower(SHA256.HashData(fakeExe));

        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (url.EndsWith("/contents/app", StringComparison.Ordinal))
            {
                var json = "[{\"name\":\"mby-cli-2.0.0.exe\",\"size\":5,\"download_url\":\"https://raw.githubusercontent.com/doc-shashank/meloberry-cli/main/app/mby-cli-2.0.0.exe\"}]";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(fakeExe)
            };
        });

        var helper = new GitHubHttpClientHelper(new FakeMetaProvider("1.0.0"), handler);
        var downloader = new GitHubToolDownloader(helper, paths);

        var check = await downloader.CheckLatestToolAsync("mby");
        Assert.True(check.IsSuccess);
        Assert.Equal("contents/app", check.Value.Source);
        Assert.Equal("2.0.0", check.Value.ExtractedVersion);

        var badHashCandidate = check.Value with { Sha256Hex = "sha256:0000" };
        var badDl = await downloader.DownloadToTempFileAsync(badHashCandidate, _ => { });
        Assert.True(badDl.IsFailure);
        Assert.Equal("Update.VerificationFailed", badDl.Error!.Code);

        var okCandidate = check.Value with { Sha256Hex = $"sha256:{sha}" };
        var okDl = await downloader.DownloadToTempFileAsync(okCandidate, _ => { });
        Assert.True(okDl.IsSuccess);
        Assert.True(File.Exists(okDl.Value));

        // Verify two-step CheckToolUpdateAsync detects newer GitHub version against installed apps\ v1.9.0.
        var appsStore = new ManagedAppsStore(paths);
        File.Delete(okDl.Value);
        await File.WriteAllBytesAsync(Path.Combine(paths.AppsDirectory, "mby-cli-1.9.0.exe"), fakeExe);
        var settingsStore = new Meloberry.Wrapper.Infrastructure.Persistence.JsonSettingsStore(paths);
        var appsHandler = new Meloberry.Wrapper.Application.UseCases.ManagedAppsHandler(appsStore, downloader, settingsStore);
        var toolUpdate = await appsHandler.CheckToolUpdateAsync("mby");
        Assert.True(toolUpdate.IsSuccess);
        Assert.True(toolUpdate.Value.IsInstalled);
        Assert.Equal("1.9.0", toolUpdate.Value.InstalledVersion);
        Assert.True(toolUpdate.Value.UpdateAvailable);
        Assert.Equal("2.0.0", toolUpdate.Value.LatestVersion);
    }

    [Fact]
    public async Task SelfUpdater_Handles404RepoGracefully_AndDetectsNewerSetupRelease()
    {
        var paths = new WrapperDataPaths(_tempRoot);

        var notPublishedHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var updater404 = new GitHubSelfUpdater(new GitHubHttpClientHelper(new FakeMetaProvider("1.0.0"), notPublishedHandler), paths);
        var res404 = await updater404.CheckForUpdateAsync("doc-shashank", "meloberry-pc", "1.0.0");
        Assert.True(res404.IsSuccess);
        Assert.True(res404.Value.RepoNotPublished);
        Assert.False(res404.Value.UpdateAvailable);

        var releaseJson = "{\"tag_name\":\"v1.1.0\",\"assets\":[{\"name\":\"mby-pc-setup-1.1.0.exe\",\"browser_download_url\":\"https://github.com/doc-shashank/meloberry-pc/releases/download/v1.1.0/mby-pc-setup-1.1.0.exe\",\"size\":1024}]}";
        var releaseHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
        });
        var updaterOk = new GitHubSelfUpdater(new GitHubHttpClientHelper(new FakeMetaProvider("1.0.0"), releaseHandler), paths);
        var resOk = await updaterOk.CheckForUpdateAsync("doc-shashank", "meloberry-pc", "1.0.0");
        Assert.True(resOk.IsSuccess);
        Assert.True(resOk.Value.UpdateAvailable);
        Assert.Equal("1.1.0", resOk.Value.LatestVersion);
        Assert.Equal("mby-pc-setup-1.1.0.exe", resOk.Value.AssetFileName);
    }

    [Fact]
    public void Allowlist_RejectsNonGitHubOrHttpHosts()
    {
        Assert.False(GitHubHttpClientHelper.IsAllowedGitHubUri("http://github.com/doc-shashank/meloberry-cli", out _));
        Assert.False(GitHubHttpClientHelper.IsAllowedGitHubUri("https://evil.example.com/mby.exe", out _));
        Assert.True(GitHubHttpClientHelper.IsAllowedGitHubUri("https://objects.githubusercontent.com/file.exe", out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private sealed class FakeMetaProvider(string version) : IWrapperMetadataProvider
    {
        public WrapperMetadata GetMetadata() => new("Meloberry PC", "Meloberry", "mby-pc", version, "doc-shashank", "meloberry-pc");
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
