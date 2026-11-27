using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Domain.Services;
using Meloberry.Wrapper.Infrastructure.Persistence;
using Meloberry.Wrapper.Infrastructure.Server;
using Meloberry.Wrapper.Infrastructure.Workspace;
using Xunit;

namespace Meloberry.Wrapper.UnitTests;

public sealed class MarkdownAssemblerAndFinalizeTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_finalize_test_{Guid.NewGuid():N}");

    [Fact]
    public void WorkspaceMarkdownAssembler_SmartWrapsUniformly_PassesPrefencedVerbatim_AndGuardsEmptySpecs()
    {
        var loadedMd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["notes/cell.md"] = "Cell organelle summary.\n![Golgi](figs/golgi.png \"Fig. 1\")"
        };

        var draft = new PromptDraft(
            Container: ContainerKind.Session,
            Title: "Cell Biology 101",
            Description: "Core organelles and recall",
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            SourceText: string.Empty,
            Selections: [],
            Attachments: [],
            Components:
            [
                new PromptComponentPlan(
                    "a1",
                    ComponentKind.Article,
                    "Organelles",
                    [new("text", true, 1), new("callout", true, 1), new("image", true, 1), new("reveal", true, 1)],
                    [
                        new("s1", "text", 1, null, new SpecContentSource(SpecContentMode.MarkdownFile, null, "notes/cell.md")),
                        new("s2", "callout", 1, "clinical", new SpecContentSource(SpecContentMode.RawText, "Golgi modifies proteins.")),
                        new("s3", "image", 1, null, new SpecContentSource(SpecContentMode.RawText, "assets/fig_1.1.png")),
                        new("s4", "reveal", 1, "showFirst", new SpecContentSource(SpecContentMode.RawText, ":::reveal{title=\"Custom\"}\n> S1\nBody\n:::"))
                    ]),
                new PromptComponentPlan(
                    "q1",
                    ComponentKind.Quiz,
                    "Checkup",
                    [new("mcq", true, 1), new("numeric", true, 1)],
                    [
                        new("q_1", "mcq", 1, "multi", new SpecContentSource(SpecContentMode.RawText, "Select purines?\n- [x] A\n- [x] G\n- [ ] C")),
                        new("q_2", "numeric", 1, null, null)
                    ])
            ]);

        var strictRes = WorkspaceMarkdownAssembler.Assemble(draft, loadedMd, allowPlaceholders: false);
        Assert.True(strictRes.IsSuccess);
        Assert.Single(strictRes.Value.MissingContentSpecs);
        Assert.Equal("q_2", strictRes.Value.MissingContentSpecs[0].SpecId);

        var phRes = WorkspaceMarkdownAssembler.Assemble(draft, loadedMd, allowPlaceholders: true);
        Assert.True(phRes.IsSuccess);
        Assert.Empty(phRes.Value.MissingContentSpecs);
        Assert.Single(phRes.Value.PlaceholderSpecsUsed);
        Assert.True(phRes.Value.Files.ContainsKey("_session.md"));

        var sessionMd = phRes.Value.Files["_session.md"];
        Assert.Contains("kind: session", sessionMd);
        Assert.Contains("01_organelles.md", sessionMd);
        Assert.Contains("02_checkup.md", sessionMd);

        var articleMd = phRes.Value.Files["01_organelles.md"];
        Assert.Contains("kind: article", articleMd);
        Assert.Contains("Cell organelle summary.", articleMd);
        Assert.Contains(":::callout{tone=\"clinical\"}", articleMd);
        Assert.Contains("![](assets/fig_1.1.png)", articleMd);
        Assert.Contains(":::reveal{title=\"Custom\"}", articleMd);

        var quizMd = phRes.Value.Files["02_checkup.md"];
        Assert.Contains(":::mcq{multi=true}", quizMd);
        Assert.Contains(":::numeric{", quizMd);
        Assert.Contains("figs/golgi.png", phRes.Value.ReferencedAssetPaths);
    }

    [Fact]
    public async Task FinalizeWorkspaceHandler_AssemblesRunsBatchAndMountsWebApp()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "finalize_data"));
        Directory.CreateDirectory(paths.AppsDirectory);
        await File.WriteAllBytesAsync(Path.Combine(paths.AppsDirectory, "mby-cli-2.1.2.exe"), [(byte)'M', (byte)'Z', 1]);

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
        Assert.True(finRes.IsSuccess);
        Assert.Equal("Session_Final999", finRes.Value.WorkspaceId);
        Assert.Contains("/viewer/", finRes.Value.ViewerUrl);
        Assert.NotNull(mountRegistry.ActiveSlotId);
        Assert.Contains("\"op\":\"apply\"", batchCli.CapturedBatchJson);
        Assert.Contains("\"op\":\"check\"", batchCli.CapturedBatchJson);
        Assert.Contains("\"op\":\"build\"", batchCli.CapturedBatchJson);
        Assert.True(File.Exists(Path.Combine(finRes.Value.BuildSourceDirectory, "assets", "diagram.png")));
    }

    private sealed class StubBatchMbyCliClient(string ws) : IMbyCliClient
    {
        public string? CapturedBatchJson { get; private set; }
        public Task<Result<IReadOnlyList<SpecTopicItem>>> GetSpecCatalogAsync(string exePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<SpecCatalogResult>> GetSpecCatalogWithIconsAsync(string exePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<AgentsMdSpecDetail>>> GetSpecDetailsAsync(string exePath, string? workingDir, IReadOnlyList<string> topics, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<WorkspaceSummary>>> ListWorkspacesAsync(string exePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> BuildWorkspaceAsync(string exePath, string workspaceId, string outputDir, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> ExportPackageAsync(string exePath, string workspaceId, string target, string outputPath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> ApplySpecJsonAsync(string exePath, string specFilePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();

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
