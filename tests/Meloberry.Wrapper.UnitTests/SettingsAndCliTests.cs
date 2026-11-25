using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.Cli;
using Meloberry.Wrapper.Infrastructure.Persistence;
using Meloberry.Wrapper.Infrastructure.Server;
using Meloberry.Wrapper.Infrastructure.Workspace;
using Xunit;

namespace Meloberry.Wrapper.UnitTests;

public sealed class SettingsAndCliTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_cfg_test_{Guid.NewGuid():N}");

    [Fact]
    public async Task JsonSettingsStore_AndPresets_RoundTripCleanly()
    {
        var paths = new WrapperDataPaths(_tempRoot);
        var store = new JsonSettingsStore(paths);
        var handler = new SettingsAndPresetsHandler(store);

        var updated = await handler.UpdateSettingsAsync("dark", null, null, @"C:\Builds", "custom-owner", "custom-pc");
        Assert.True(updated.IsSuccess);
        Assert.Equal("dark", updated.Value.ThemeMode);
        Assert.True(updated.Value.OverrideWebAppTheme);

        var toggledOff = await handler.UpdateOverrideWebAppThemeAsync(false);
        Assert.True(toggledOff.IsSuccess);
        Assert.False(toggledOff.Value.OverrideWebAppTheme);

        var preserved = await handler.UpdateSettingsAsync("paper", null, null, @"C:\Builds", "custom-owner", "custom-pc");
        Assert.True(preserved.IsSuccess);
        Assert.Equal("paper", preserved.Value.ThemeMode);
        Assert.False(preserved.Value.OverrideWebAppTheme);

        var custom = await handler.SaveCustomPresetAsync(new PromptPreset(
            "",
            "My Drill",
            "Notes",
            ContainerKind.StandaloneQuiz,
            AuthoringFormat.Json,
            [new ShoppingListSelection("flashcard", true, 10)]));
        Assert.True(custom.IsSuccess);

        var allPresets = await handler.GetAllPresetsAsync();
        Assert.Contains(allPresets, p => p.Name == "My Drill" && !p.IsBuiltIn);

        Assert.True((await handler.DeleteCustomPresetAsync(custom.Value.Id)).IsSuccess);
        Assert.DoesNotContain(await handler.GetAllPresetsAsync(), p => p.Name == "My Drill");

        // Bug #21: invalid GitHub owner/repo identifiers must be rejected.
        var badOwner = await handler.UpdateSettingsAsync("dark", null, null, null, "evil/repo", "meloberry-pc");
        Assert.True(badOwner.IsFailure);
        Assert.Equal("Settings.InvalidGitHubRepo", badOwner.Error!.Code);

        // Bug #2: allowlist rejects prefix spoofing, traversal, and @host userinfo hijacking.
        Assert.True(EcosystemCatalog.TryValidateAllowlistedUrl("https://github.com/doc-shashank/meloberry-cli/releases", out _));
        Assert.True(EcosystemCatalog.TryValidateAllowlistedUrl("https://developer.microsoft.com/en-us/microsoft-edge/webview2/", out _));
        Assert.False(EcosystemCatalog.TryValidateAllowlistedUrl("https://github.com/doc-shashank/meloberry-cli-evil", out _));
        Assert.False(EcosystemCatalog.TryValidateAllowlistedUrl("https://github.com/doc-shashank/meloberry-cli/../../attacker/repo", out _));
        Assert.False(EcosystemCatalog.TryValidateAllowlistedUrl("https://github.com/doc-shashank/meloberry-cli@evil.com", out _));
        Assert.False(EcosystemCatalog.TryValidateAllowlistedUrl("https://evil.example.com@github.com/doc-shashank/meloberry-cli", out _));
    }

    [Fact]
    public async Task SelectWorkspace_DoesNotLeakSelectedApps_AndBuildUsesSubfolderInDefaultBuildDir()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "ws_cfg"));
        Directory.CreateDirectory(paths.AppsDirectory);
        await File.WriteAllBytesAsync(Path.Combine(paths.AppsDirectory, "mby-cli-2.0.0.exe"), [(byte)'M', (byte)'Z', 0x01]);

        var ws1 = Path.Combine(_tempRoot, "ws1");
        var ws2 = Path.Combine(_tempRoot, "ws2");
        Directory.CreateDirectory(ws1);
        Directory.CreateDirectory(ws2);

        var store = new JsonSettingsStore(paths);
        await store.SaveAsync(new WrapperSettings(
            ActiveWorkspace: new ActiveWorkspaceState(ws1, true, SelectedAppFileNames: ["mby-cli-2.0.0.exe"]),
            DefaultBuildOutputDir: Path.Combine(_tempRoot, "AllBuilds")));

        var appsStore = new ManagedAppsStore(paths);
        var setupHandler = new WorkspaceSetupHandler(new WorkspaceInitializer(paths, new WebAppAnalysisStore()), appsStore, store);

        // Bug #19: selecting ws2 must NOT inherit ws1's SelectedAppFileNames.
        var selRes = await setupHandler.SelectWorkspaceAsync(ws2);
        Assert.True(selRes.IsSuccess);
        Assert.Empty(selRes.Value.SelectedAppFileNames!);

        // Bug #16: DefaultBuildOutputDir builds into <DefaultBuildOutputDir>/<wsId>_web.
        var stubCli = new StubBuildCliClient();
        var appsHandler = new ManagedAppsHandler(appsStore, new NoopDownloader(), store);
        var libHandler = new ManageLibraryHandler(new WebAppMountRegistry(), new WebAppAnalysisStore(), new WorkspaceInitializer(paths, new WebAppAnalysisStore()), store);
        var wsActions = new WorkspaceActionsHandler(appsHandler, libHandler, stubCli, store);

        var buildRes = await wsActions.BuildAndOpenAsync("Session_123");
        Assert.True(buildRes.IsSuccess);
        Assert.Equal(Path.Combine(_tempRoot, "AllBuilds", "Session_123_web"), stubCli.CapturedOutputDir);
    }

    [Fact]
    public async Task LiveCliIntegration_ParsesMbyAndMelomgSpecsWhenBinariesPresent()
    {
        var mbyRepoExe = TestRepoPaths.FindMbyExecutable();
        if (!string.IsNullOrWhiteSpace(mbyRepoExe) && File.Exists(mbyRepoExe))
        {
            var client = new ProcessMbyCliClient();
            var catalog = await client.GetSpecCatalogAsync(mbyRepoExe, null);
            Assert.True(catalog.IsSuccess);
            Assert.Contains(catalog.Value, s => s.Topic == "mcq");
            Assert.Contains(catalog.Value, s => s.Topic == "table");
        }

        var melomgRepoExe = TestRepoPaths.FindMelomgExecutable();
        if (!string.IsNullOrWhiteSpace(melomgRepoExe) && File.Exists(melomgRepoExe))
        {
            var melomgClient = new ProcessMelomgCliClient();
            var specs = await melomgClient.GetSpecTextAsync(melomgRepoExe, null, ["extract", "mask"]);
            Assert.True(specs.IsSuccess);
            Assert.Contains("melomg extract", specs.Value);
            Assert.Contains("melomg mask", specs.Value);
        }

        // Bug #17: missing CLI executable returns Failure instead of throwing uncaught Win32Exception.
        var missingRes = await new ProcessMbyCliClient().GetSpecCatalogAsync(Path.Combine(_tempRoot, "nonexistent_mby.exe"), null);
        Assert.True(missingRes.IsFailure);
        Assert.Equal("Cli.ExecutionFailed", missingRes.Error!.Code);
    }

    private sealed class NoopDownloader : IGitHubToolDownloader
    {
        public Task<Result<ToolReleaseCandidate>> CheckLatestToolAsync(string toolKey, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Result<string>> DownloadToTempFileAsync(ToolReleaseCandidate candidate, Action<DownloadProgressSnapshot> progressCallback, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubBuildCliClient : IMbyCliClient
    {
        public string? CapturedOutputDir { get; private set; }
        public Task<Result<IReadOnlyList<SpecTopicItem>>> GetSpecCatalogAsync(string exePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<AgentsMdSpecDetail>>> GetSpecDetailsAsync(string exePath, string? workingDir, IReadOnlyList<string> topics, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<WorkspaceSummary>>> ListWorkspacesAsync(string exePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> ExportPackageAsync(string exePath, string workspaceId, string target, string outputPath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> ApplySpecJsonAsync(string exePath, string specFilePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public async Task<Result<string>> BuildWorkspaceAsync(string exePath, string workspaceId, string outputDir, string? workingDir, CancellationToken ct = default)
        {
            CapturedOutputDir = outputDir;
            Directory.CreateDirectory(outputDir);
            await File.WriteAllTextAsync(Path.Combine(outputDir, "index.html"), "<html></html>", ct);
            await File.WriteAllTextAsync(Path.Combine(outputDir, "app.js"), "console.log(1);", ct);
            return Result<string>.Success(outputDir);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
