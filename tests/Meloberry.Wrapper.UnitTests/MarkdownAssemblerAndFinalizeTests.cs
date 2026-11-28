using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.Common;
using Meloberry.Wrapper.Infrastructure.Persistence;
using Meloberry.Wrapper.Infrastructure.Server;
using Meloberry.Wrapper.Infrastructure.Workspace;
using Xunit;

namespace Meloberry.Wrapper.UnitTests;

public sealed class MarkdownAssemblerAndFinalizeTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_finalize_test_{Guid.NewGuid():N}");

    [Fact]
    public async Task FinalizeWorkspaceHandler_AssemblesViaMbyScaffold_RunsAllThemesBatch_AndMountsWebApp()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "finalize_data"));
        Directory.CreateDirectory(paths.AppsDirectory);
        await File.WriteAllBytesAsync(Path.Combine(paths.AppsDirectory, "mby-cli-2.2.0-beta-1.exe"), [(byte)'M', (byte)'Z', 1]);

        var ws = Path.Combine(_tempRoot, "ws_fin");
        Directory.CreateDirectory(ws);
        await File.WriteAllBytesAsync(Path.Combine(ws, "diagram.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        await File.WriteAllTextAsync(Path.Combine(ws, "intro.md"), "# Cell Intro\n![Diagram](diagram.png)");

        var settingsStore = new JsonSettingsStore(paths);
        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true)));

        var draftStore = new WorkspaceDraftStore();
        await draftStore.SaveAsync(ws, new PromptDraft(
            Container: ContainerKind.StandaloneArticle,
            Title: "Cell Finalize Test",
            Description: "Test build",
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            SourceText: string.Empty,
            Selections: [new("text", true, 1)],
            Attachments: [],
            Components:
            [
                new PromptComponentPlan(
                    "a1",
                    ComponentKind.Article,
                    "Intro",
                    [new("text", true, 1)],
                    [new("s1", "text", 1, null, new SpecContentSource(SpecContentMode.MarkdownFile, null, "intro.md"))])
            ]));

        var appsStore = new ManagedAppsStore(paths);
        var mountRegistry = new WebAppMountRegistry();
        var analysisStore = new WebAppAnalysisStore();
        var initializer = new WorkspaceInitializer(paths, analysisStore, new WorkspaceShimWriter());
        var libHandler = new ManageLibraryHandler(mountRegistry, analysisStore, initializer, settingsStore);
        var batchCli = new StubBatchMbyCliClient(ws);

        var finalizeHandler = new FinalizeWorkspaceHandler(
            appsStore,
            draftStore,
            batchCli,
            libHandler,
            settingsStore);

        var finRes = await finalizeHandler.FinalizeAsync(new FinalizeRequest(ws, AllowPlaceholders: false));
        Assert.True(finRes.IsSuccess, finRes.Error?.Description);
        Assert.Equal("Session_Final999", finRes.Value.WorkspaceId);
        Assert.Contains("/viewer/", finRes.Value.ViewerUrl);
        Assert.NotNull(mountRegistry.ActiveSlotId);
        Assert.True(batchCli.ScaffoldCalled);
        Assert.Equal("md", batchCli.CapturedScaffoldFormat);
        Assert.Contains("\"op\":\"apply\"", batchCli.CapturedBatchJson);
        Assert.Contains("\"op\":\"check\"", batchCli.CapturedBatchJson);
        Assert.Contains("\"op\":\"build\"", batchCli.CapturedBatchJson);
        Assert.Contains("\"allThemes\":true", batchCli.CapturedBatchJson);
    }

    [Fact]
    public async Task FinalizeWorkspaceHandler_RejectsIncompatibleMbyCli_AndPropagatesMissingContent()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "fin_incompat"));
        Directory.CreateDirectory(paths.AppsDirectory);
        await File.WriteAllBytesAsync(Path.Combine(paths.AppsDirectory, "mby-cli-2.1.6.exe"), [(byte)'M', (byte)'Z', 1]);
        var ws = Path.Combine(_tempRoot, "ws_incompat");
        Directory.CreateDirectory(ws);

        var settingsStore = new JsonSettingsStore(paths);
        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true)));
        var draftStore = new WorkspaceDraftStore();
        var appsStore = new ManagedAppsStore(paths);
        var initializer = new WorkspaceInitializer(paths, new WebAppAnalysisStore(), new WorkspaceShimWriter());
        var libHandler = new ManageLibraryHandler(new WebAppMountRegistry(), new WebAppAnalysisStore(), initializer, settingsStore);

        var oldCli = new StubBatchMbyCliClient(ws) { ReportedVersion = "2.1.6" };
        var oldHandler = new FinalizeWorkspaceHandler(appsStore, draftStore, oldCli, libHandler, settingsStore);
        var oldRes = await oldHandler.FinalizeAsync(new FinalizeRequest(ws));
        Assert.True(oldRes.IsFailure);
        Assert.Equal("Cli.Incompatible", oldRes.Error!.Code);

        var missingCli = new StubBatchMbyCliClient(ws) { FailScaffoldMissing = true };
        var missingHandler = new FinalizeWorkspaceHandler(appsStore, draftStore, missingCli, libHandler, settingsStore);
        var missingRes = await missingHandler.FinalizeAsync(new FinalizeRequest(ws));
        Assert.True(missingRes.IsFailure);
        Assert.Equal("Prompt.InvalidDraft", missingRes.Error!.Code);
    }

    [Fact]
    public async Task ImportWorkspaceAssetHandler_DelegatesExternalImagesToMbyAssetAddWhenCompatible()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "asset_data"));
        Directory.CreateDirectory(paths.AppsDirectory);
        await File.WriteAllBytesAsync(Path.Combine(paths.AppsDirectory, "mby-cli-2.2.0-beta-1.exe"), [(byte)'M', (byte)'Z', 1]);

        var ws = Path.Combine(_tempRoot, "ws_asset");
        Directory.CreateDirectory(ws);
        var srcDir = Path.Combine(_tempRoot, "ext");
        Directory.CreateDirectory(srcDir);
        var srcImg = Path.Combine(srcDir, "fig.png");
        await File.WriteAllBytesAsync(srcImg, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var settingsStore = new JsonSettingsStore(paths);
        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true)));
        var appsStore = new ManagedAppsStore(paths);
        var cli = new StubBatchMbyCliClient(ws);

        var handler = new ImportWorkspaceAssetHandler(settingsStore, new AtomicFileWriter(), appsStore, cli);
        var res = await handler.ImportAsync(ws, srcImg, "image");
        Assert.True(res.IsSuccess, res.Error?.Description);
        Assert.Equal("assets/fig.png", res.Value.RelativePath);
        Assert.True(cli.AddAssetCalled);
    }

    private sealed class StubBatchMbyCliClient(string ws) : IMbyCliClient
    {
        public string ReportedVersion { get; init; } = "2.2.0-beta-1";
        public bool FailScaffoldMissing { get; init; }
        public bool ScaffoldCalled { get; private set; }
        public bool AddAssetCalled { get; private set; }
        public string? CapturedScaffoldFormat { get; private set; }
        public string? CapturedBatchJson { get; private set; }

        public Task<Result<MbyCliVersionInfo>> GetVersionInfoAsync(string exePath, CancellationToken ct = default) =>
            Task.FromResult(Result<MbyCliVersionInfo>.Success(MbyCliCompatibility.Evaluate(ReportedVersion, MbyCliCompatibility.RequiredCapabilities)));
        public Task<Result<IReadOnlyList<SpecTopicItem>>> GetSpecCatalogAsync(string exePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<SpecCatalogResult>> GetSpecCatalogWithIconsAsync(string exePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<AgentsMdSpecDetail>>> GetSpecDetailsAsync(string exePath, string? workingDir, IReadOnlyList<string> topics, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<WorkspaceSummary>>> ListWorkspacesAsync(string exePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<DiscoveredWebApp>>> ListWebAppsAsync(string exePath, string directory, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> BuildWorkspaceAsync(string exePath, string workspaceId, string outputDir, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> ExportPackageAsync(string exePath, string workspaceId, string target, string outputPath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> ApplySpecJsonAsync(string exePath, string specFilePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> ApplyAtAsync(string exePath, string targetAddr, string specPath, int insertAt, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Result<ScaffoldCliResult>> ScaffoldAsync(
            string exePath,
            string blueprintJson,
            string format,
            string? outputPath,
            bool apply,
            bool allowPlaceholders,
            string? workingDir,
            CancellationToken ct = default)
        {
            ScaffoldCalled = true;
            CapturedScaffoldFormat = format;
            if (FailScaffoldMissing)
            {
                return Task.FromResult(Result<ScaffoldCliResult>.Failure(
                    WrapperErrors.InvalidPromptDraft("Cannot finalize: 1 spec(s) have no content (Quiz:mcq).")));
            }

            var outDir = outputPath ?? Path.Combine(ws, ".meloberry", "build-src", "cell_finalize_test");
            Directory.CreateDirectory(outDir);
            return Task.FromResult(Result<ScaffoldCliResult>.Success(new ScaffoldCliResult(
                Format: format,
                Slug: "cell_finalize_test",
                RawJson: null,
                MarkdownFiles: new Dictionary<string, string> { ["_session.md"] = "# Cell Finalize Test" },
                WrittenPath: outDir,
                RequiresPlaceholderImage: false,
                PlaceholderSpecsUsed: [],
                Warnings: [],
                AppliedTargetId: null)));
        }

        public Task<Result<AssetAddCliResult>> AddAssetAsync(
            string exePath,
            string? workspaceAddr,
            string? targetDir,
            string sourceFilePath,
            string? workingDir,
            CancellationToken ct = default)
        {
            AddAssetCalled = true;
            var fileName = Path.GetFileName(sourceFilePath);
            var fullPath = Path.Combine(targetDir ?? ws, "assets", fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.Copy(sourceFilePath, fullPath, overwrite: true);
            return Task.FromResult(Result<AssetAddCliResult>.Success(new AssetAddCliResult(
                WorkspaceId: workspaceAddr ?? "ws",
                RelativeAssetPath: $"assets/{fileName}",
                FullPath: fullPath,
                AlreadyExisted: false)));
        }

        public Task<Result<string>> PatchWithMarkdownAsync(string exePath, string targetAddr, string markdownFragment, string? subtype, string? imagePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<PrimeAgentsMdCliResult>> PrimeAgentsMdAsync(string exePath, IReadOnlyList<string> topics, string container, string authoringFormat, string cliPrefix, string? workspacePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IngestAnalysisCliResult>> IngestAnalysisAsync(string exePath, string webAppDir, string payloadJson, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> MoveEntityAsync(string exePath, string addr, string targetCompAddr, int? index, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> ReorderContainerAsync(string exePath, string containerAddr, IReadOnlyList<string> orderedIds, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<CloneEntityCliResult>> CloneEntityAsync(string exePath, string addr, string? targetCompAddr, int? insertAt, string? newRef, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();

        public async Task<Result<MbyBatchExecutionResult>> RunBatchAsync(string exePath, string batchJson, string? workingDir, CancellationToken ct = default)
        {
            CapturedBatchJson = batchJson;
            var outWeb = Path.Combine(ws, "cell_finalize_test_web");
            Directory.CreateDirectory(outWeb);
            await File.WriteAllTextAsync(Path.Combine(outWeb, "index.html"), "<html></html>", ct);
            await File.WriteAllTextAsync(Path.Combine(outWeb, "app.js"), "console.log(1);", ct);
            return Result<MbyBatchExecutionResult>.Success(new MbyBatchExecutionResult("Session_Final999", outWeb, 3));
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
