using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.Cli;
using Meloberry.Wrapper.Infrastructure.Common;
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
        var presetStore = new MbyPresetFileStore(paths, new AtomicFileWriter());
        var handler = new SettingsAndPresetsHandler(store, presetStore);

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

        var missingDelete = await handler.DeleteCustomPresetAsync("nonexistent-preset");
        Assert.True(missingDelete.IsFailure);
        Assert.Equal("Preset.NotFound", missingDelete.Error!.Code);

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
        var setupHandler = new WorkspaceSetupHandler(
            new WorkspaceInitializer(paths, new WebAppAnalysisStore()),
            appsStore,
            store,
            new WorkspaceDirectoryBrowser());

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

    [Fact]
    public async Task JsonSettingsStore_UpdateAsync_PreservesAllDisjointConcurrentMutations()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "concurrent_cfg"));
        var store = new JsonSettingsStore(paths);
        var presetStore = new MbyPresetFileStore(paths, new AtomicFileWriter());
        var settingsHandler = new SettingsAndPresetsHandler(store, presetStore);
        var libHandler = new ManageLibraryHandler(
            new WebAppMountRegistry(),
            new WebAppAnalysisStore(),
            new WorkspaceInitializer(paths, new WebAppAnalysisStore()),
            store);

        var webApps = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var dir = Path.Combine(_tempRoot, $"web_{i}");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "index.html"), "<html></html>");
            await File.WriteAllTextAsync(Path.Combine(dir, "app.js"), "console.log(1);");
            webApps.Add(dir);
        }

        var tasks = new List<Task>();
        for (var i = 0; i < 6; i++)
        {
            var idx = i;
            var webDir = webApps[idx];
            var presetName = $"Preset_{idx}";
            tasks.Add(Task.Run(() => libHandler.OpenWebAppAsync(webDir)));
            tasks.Add(Task.Run(() => settingsHandler.SaveCustomPresetAsync(new PromptPreset(
                $"custom-{idx}",
                presetName,
                "Desc",
                ContainerKind.StandaloneQuiz,
                AuthoringFormat.Json,
                [new ShoppingListSelection("mcq", true, 5)]))));
        }

        tasks.Add(Task.Run(() => settingsHandler.UpdateOverrideWebAppThemeAsync(false)));
        await Task.WhenAll(tasks);

        var finalSettings = await store.LoadAsync();
        Assert.False(finalSettings.OverrideWebAppTheme);
        Assert.Equal(6, finalSettings.RecentWebApps?.Count);
        var customPresets = (await settingsHandler.GetAllPresetsAsync()).Where(p => !p.IsBuiltIn).ToList();
        Assert.Equal(6, customPresets.Count);
        Assert.Equal(6, Directory.GetFiles(paths.PresetsDirectory, "*.mbypreset", SearchOption.TopDirectoryOnly).Length);
    }

    [Fact]
    public async Task BuiltInPresets_HasThreePerContainerKind_AndMbyPresetRoundTripsWorkflowAndComponentScopes()
    {
        Assert.Equal(9, BuiltInPresets.All.Count);
        Assert.Equal(3, BuiltInPresets.All.Count(p => p.Container == ContainerKind.Session));
        Assert.Equal(3, BuiltInPresets.All.Count(p => p.Container == ContainerKind.StandaloneArticle));
        Assert.Equal(3, BuiltInPresets.All.Count(p => p.Container == ContainerKind.StandaloneQuiz));

        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "mbypreset_scope"));
        var store = new JsonSettingsStore(paths);
        var presetStore = new MbyPresetFileStore(paths, new AtomicFileWriter());
        var handler = new SettingsAndPresetsHandler(store, presetStore);

        var compPath = Path.Combine(_tempRoot, "card_article.mbypreset");
        var articleComp = new PromptComponentPlan(
            "a1",
            ComponentKind.Article,
            "My Neuro Article",
            [new ShoppingListSelection("text", true, 2), new ShoppingListSelection("callout", true, 1, null, ["pearl"])]);

        var saveCompRes = await handler.SaveComponentPresetFileAsync(compPath, articleComp, "Neuro Article");
        Assert.True(saveCompRes.IsSuccess);
        Assert.True(File.Exists(compPath));

        var loadMatchRes = await handler.LoadComponentPresetFileAsync(compPath, ComponentKind.Article);
        Assert.True(loadMatchRes.IsSuccess);
        Assert.Equal(ComponentKind.Article, loadMatchRes.Value.Kind);
        Assert.Equal("Neuro Article", loadMatchRes.Value.Title);

        var loadMismatchRes = await handler.LoadComponentPresetFileAsync(compPath, ComponentKind.Quiz);
        Assert.True(loadMismatchRes.IsFailure);
        Assert.Equal("Preset.InvalidFile", loadMismatchRes.Error!.Code);

        var importCompAsWorkflow = await handler.ImportWorkflowPresetFileAsync(compPath);
        Assert.True(importCompAsWorkflow.IsFailure);
        Assert.Equal("Preset.InvalidFile", importCompAsWorkflow.Error!.Code);

        var wfPath = Path.Combine(_tempRoot, "imported_session.mbypreset");
        var wfEnvelope = new MbyPresetFile(
            FormatVersion: 1,
            Scope: PresetFileScope.Workflow,
            Name: "Imported Session",
            WorkflowPreset: new PromptPreset(
                "imp-1",
                "Imported Session",
                "Portable workflow",
                ContainerKind.Session,
                AuthoringFormat.MarkdownDirectory,
                [new ShoppingListSelection("mcq", true, 4)]));
        Assert.True((await presetStore.WritePresetFileAsync(wfPath, wfEnvelope)).IsSuccess);

        var importOk = await handler.ImportWorkflowPresetFileAsync(wfPath);
        Assert.True(importOk.IsSuccess);
        Assert.Contains(await handler.GetAllPresetsAsync(), p => p.Name == "Imported Session" && !p.IsBuiltIn);
    }

    [Fact]
    public async Task PathContainment_WorkspaceTreeBoundary_PortablePresetSave_AndSignalParser()
    {
        var baseWs = Path.Combine(_tempRoot, "bound_ws");
        var childWs = Path.Combine(baseWs, "Chapter1");
        var siblingWs = Path.Combine(_tempRoot, "bound_ws2");
        Directory.CreateDirectory(childWs);
        Directory.CreateDirectory(siblingWs);

        Assert.True(PathNormalization.IsSameOrDescendantDirectory(baseWs, baseWs));
        Assert.True(PathNormalization.IsSameOrDescendantDirectory(baseWs, childWs));
        Assert.True(PathNormalization.IsSameOrDescendantDirectory(baseWs + Path.DirectorySeparatorChar, childWs));
        Assert.False(PathNormalization.IsSameOrDescendantDirectory(baseWs, siblingWs));
        Assert.False(PathNormalization.IsSameOrDescendantDirectory(baseWs, _tempRoot));
        Assert.False(PathNormalization.IsSameOrDescendantDirectory(baseWs, Path.Combine(baseWs, "..", "bound_ws2")));

        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "portable_cfg"));
        var store = new JsonSettingsStore(paths);
        var presetStore = new MbyPresetFileStore(paths, new AtomicFileWriter());
        var handler = new SettingsAndPresetsHandler(store, presetStore);
        var setupHandler = new WorkspaceSetupHandler(
            new WorkspaceInitializer(paths, new WebAppAnalysisStore()),
            new ManagedAppsStore(paths),
            store,
            new WorkspaceDirectoryBrowser());

        await setupHandler.SelectWorkspaceAsync(baseWs);

        var rootListing = await setupHandler.ListDirectoryAsync(null);
        Assert.True(rootListing.IsSuccess);
        Assert.Null(rootListing.Value.ParentPath);

        var childListing = await setupHandler.ListDirectoryAsync(childWs);
        Assert.True(childListing.IsSuccess);
        Assert.True(PathNormalization.AreSameDirectory(baseWs, childListing.Value.ParentPath));

        var outsideListing = await setupHandler.ListDirectoryAsync(siblingWs);
        Assert.True(outsideListing.IsFailure);

        // Save workflow preset anywhere on disk + register in CUSTOM library
        var arbitraryDir = Path.Combine(_tempRoot, "DesktopCustom");
        Directory.CreateDirectory(arbitraryDir);
        var arbitraryPath = Path.Combine(arbitraryDir, "my_portable.mbypreset");
        var savedRes = await handler.SaveWorkflowPresetToFileAsync(arbitraryPath, new PromptPreset(
            "",
            "Portable Session",
            "Saved anywhere",
            ContainerKind.Session,
            AuthoringFormat.MarkdownDirectory,
            [new ShoppingListSelection("text", true, 2)]));
        Assert.True(savedRes.IsSuccess);
        Assert.True(File.Exists(arbitraryPath));
        Assert.Contains(await handler.GetAllPresetsAsync(), p => p.Name == "Portable Session" && !p.IsBuiltIn);

        // Saving directly inside PresetsDirectory must not duplicate CUSTOM entries
        var inLibPath = Path.Combine(paths.PresetsDirectory, "direct_lib.mbypreset");
        var directRes = await handler.SaveWorkflowPresetToFileAsync(inLibPath, new PromptPreset(
            "direct-1",
            "Direct Lib Preset",
            "In library",
            ContainerKind.StandaloneArticle,
            AuthoringFormat.SingleMarkdown,
            [new ShoppingListSelection("text", true, 1)]));
        Assert.True(directRes.IsSuccess);
        Assert.Single(await handler.GetAllPresetsAsync(), p => p.Id == "direct-1");

        // MbyCliSignalParser checks
        var validSignal = """
            # generated by mby
            version=1
            event=webapp_built
            id=sig-001
            timestamp=2026-11-27T12:00:00Z
            workspace_id=neuro_01
            path=neuro_01_web
            title=Neuro Study
            producer=mby/2.1.0
            """;
        Assert.True(MbyCliSignalParser.TryParse(validSignal, out var parsedSignal));
        Assert.NotNull(parsedSignal);
        Assert.Equal("webapp_built", parsedSignal!.Event);
        Assert.Equal("sig-001", parsedSignal.Id);
        Assert.Equal("neuro_01_web", parsedSignal.Path);
        Assert.Equal("Neuro Study", parsedSignal.Title);

        Assert.False(MbyCliSignalParser.TryParse("version=1\nevent=webapp_built\n", out _));
        Assert.True(MbyCliSignalParser.TryParse("event=webapp_built\npath=out_web", out var fallbackIdSig));
        Assert.False(string.IsNullOrWhiteSpace(fallbackIdSig!.Id));
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
        public Task<Result<SpecCatalogResult>> GetSpecCatalogWithIconsAsync(string exePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<AgentsMdSpecDetail>>> GetSpecDetailsAsync(string exePath, string? workingDir, IReadOnlyList<string> topics, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<WorkspaceSummary>>> ListWorkspacesAsync(string exePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> ExportPackageAsync(string exePath, string workspaceId, string target, string outputPath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> ApplySpecJsonAsync(string exePath, string specFilePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<MbyBatchExecutionResult>> RunBatchAsync(string exePath, string batchJson, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public async Task<Result<string>> BuildWorkspaceAsync(string exePath, string workspaceId, string outputDir, string? workingDir, CancellationToken ct = default)
        {
            CapturedOutputDir = outputDir;
            Directory.CreateDirectory(outputDir);
            await File.WriteAllTextAsync(Path.Combine(outputDir, "index.html"), "<html></html>", ct);
            await File.WriteAllTextAsync(Path.Combine(outputDir, "app.js"), "console.log(1);", ct);
            return Result<string>.Success(outputDir);
        }
    }

    [Fact]
    public void SpecCatalogDefaults_IncludesV212TopicsAndDisplayNames()
    {
        foreach (var topic in new[] { "diagram-explore", "reveal", "case", "classify", "match", "numeric", "mcq", "text", "link" })
        {
            var item = SpecCatalogDefaults.BuiltInFallbackCatalog.FirstOrDefault(s => s.Topic == topic);
            Assert.NotNull(item);
            Assert.False(string.IsNullOrWhiteSpace(item!.DisplayName));
        }

        Assert.Equal("block", SpecCatalogDefaults.GetCategoryForTopic("diagram-explore"));
        Assert.Equal("block", SpecCatalogDefaults.GetCategoryForTopic("reveal"));
        Assert.Equal("question", SpecCatalogDefaults.GetCategoryForTopic("case"));
        Assert.Equal("question", SpecCatalogDefaults.GetCategoryForTopic("classify"));
        Assert.Equal("question", SpecCatalogDefaults.GetCategoryForTopic("match"));
        Assert.Equal("question", SpecCatalogDefaults.GetCategoryForTopic("numeric"));
    }

    [Fact]
    public async Task LoadSpecCatalogHandler_ResolvesCliIconsAndFallsBackGracefullyWhenMbyMissing()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "fallback"));
        var settingsStore = new JsonSettingsStore(paths);
        var appsStore = new ManagedAppsStore(paths);
        var stubCli = new StubSpecCliClient();
        var handler = new LoadSpecCatalogHandler(appsStore, stubCli, settingsStore);

        var missing = await handler.LoadWithIconsAsync(forceRefresh: true);
        Assert.False(missing.IsLiveCli);
        Assert.Null(missing.ActiveMbyPath);
        Assert.Empty(missing.SpecIcons);
        Assert.Contains(missing.Specs, s => s.Topic == "diagram-explore");

        Directory.CreateDirectory(paths.AppsDirectory);
        var fakeExe = Path.Combine(paths.AppsDirectory, "mby-cli-2.1.2.exe");
        await File.WriteAllBytesAsync(fakeExe, [(byte)'M', (byte)'Z', 0x01]);

        var live = await handler.LoadWithIconsAsync(forceRefresh: true);
        Assert.True(live.IsLiveCli);
        Assert.Equal(fakeExe, live.ActiveMbyPath);
        Assert.Contains(live.Specs, s => s.Topic == "mcq" && s.DisplayName == "Multiple Choice");
        Assert.Contains(live.Specs, s => s.Topic == SpecCatalogDefaults.VirtualLinkTopicName);
        Assert.True(live.SpecIcons.ContainsKey("mcq"));
        Assert.StartsWith("<svg", live.SpecIcons["mcq"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProcessMbyCliClient_ParsesDisplayNameAndOptionalIcons()
    {
        var jsonWithIcons = """
            {
              "success": true,
              "data": {
                "schemas": [
                  {
                    "topic": "mcq",
                    "displayName": "Multiple Choice",
                    "scope": "content",
                    "category": "question",
                    "summary": "MCQ item",
                    "compactSignature": "{ type:\"mcq\" }",
                    "command": "Inside quiz.questions[]"
                  }
                ],
                "icons": {
                  "mcq": "<svg viewBox=\"0 0 24 24\"></svg>",
                  "mcq-multi": "<svg viewBox=\"0 0 24 24\"><rect/></svg>"
                }
              }
            }
            """;

        var parsed = ProcessMbyCliClient.ParseSpecCatalogPayload(jsonWithIcons, 0);
        Assert.True(parsed.IsSuccess);
        Assert.Single(parsed.Value.Specs);
        Assert.Equal("Multiple Choice", parsed.Value.Specs[0].DisplayName);
        Assert.Equal(2, parsed.Value.SpecIcons.Count);
        Assert.Contains("mcq-multi", parsed.Value.SpecIcons.Keys);

        var jsonWithoutIcons = """
            {
              "success": true,
              "data": {
                "schemas": [
                  {
                    "topic": "text",
                    "scope": "content",
                    "category": "block",
                    "summary": "Text block",
                    "compactSignature": "{ kind:\"text\" }",
                    "command": "Inside article.blocks[]"
                  }
                ]
              }
            }
            """;
        var parsedLegacy = ProcessMbyCliClient.ParseSpecCatalogPayload(jsonWithoutIcons, 0);
        Assert.True(parsedLegacy.IsSuccess);
        Assert.Empty(parsedLegacy.Value.SpecIcons);
    }

    [Fact]
    public void StaticAssetHost_ExposesGenericFallbackUiIconsAndNoSpecIcons()
    {
        var icons = StaticAssetHost.GetEmbeddedIconMapSnapshot();
        Assert.True(icons.ContainsKey("ui/block-generic"));
        Assert.True(icons.ContainsKey("ui/question-generic"));
        Assert.DoesNotContain(icons.Keys, k => k.StartsWith("specs/", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StubSpecCliClient : IMbyCliClient
    {
        public Task<Result<IReadOnlyList<SpecTopicItem>>> GetSpecCatalogAsync(string exePath, string? workingDir, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result<SpecCatalogResult>> GetSpecCatalogWithIconsAsync(string exePath, string? workingDir, CancellationToken ct = default) =>
            Task.FromResult(Result<SpecCatalogResult>.Success(new SpecCatalogResult(
                [new SpecTopicItem("mcq", "content", "question", "MCQ", "{ type:\"mcq\" }", "quiz", false, SpecCatalogDefaults.GetModeGroupsForTopic("mcq"), "Multiple Choice")],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mcq"] = "<svg viewBox=\"0 0 24 24\"></svg>"
                })));

        public Task<Result<IReadOnlyList<AgentsMdSpecDetail>>> GetSpecDetailsAsync(string exePath, string? workingDir, IReadOnlyList<string> topics, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<WorkspaceSummary>>> ListWorkspacesAsync(string exePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> BuildWorkspaceAsync(string exePath, string workspaceId, string outputDir, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> ExportPackageAsync(string exePath, string workspaceId, string target, string outputPath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> ApplySpecJsonAsync(string exePath, string specFilePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<MbyBatchExecutionResult>> RunBatchAsync(string exePath, string batchJson, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
