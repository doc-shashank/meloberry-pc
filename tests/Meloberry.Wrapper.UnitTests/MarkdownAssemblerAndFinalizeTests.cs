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

    [Fact]
    public void WorkspaceMarkdownAssembler_CollectsBareAndYamlImagePaths_AndPreservesExplicitImages()
    {
        var draft = new PromptDraft(
            Container: ContainerKind.Session,
            Title: "Anatomy Visuals",
            Description: "Figure asset collection test",
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            SourceText: string.Empty,
            Selections: [],
            Attachments: [],
            Components:
            [
                new PromptComponentPlan(
                    "a1",
                    ComponentKind.Article,
                    "Diagrams",
                    [new("image", true, 1), new("diagram-explore", true, 1)],
                    [
                        new("s_img", "image", 1, null, new SpecContentSource(SpecContentMode.RawText, "assets/fig.png")),
                        new("s_diag", "diagram-explore", 1, null, new SpecContentSource(SpecContentMode.RawText, "imagePath: assets/heart.png\n- Apex: Left ventricle tip."))
                    ]),
                new PromptComponentPlan(
                    "q1",
                    ComponentKind.Quiz,
                    "Visual Quiz",
                    [new("relabel", true, 1), new("mcq", true, 1)],
                    [
                        new("q_rel", "relabel", 1, "bank", new SpecContentSource(SpecContentMode.RawText, "masksPath: assets/heart_masks.json\nimagePath: assets/heart_clean.png\nprompt: Identify valves.")),
                        new("q_mcq", "mcq", 1, null, new SpecContentSource(SpecContentMode.RawText, "imagePath: assets/ecg.png\nWhat rhythm is shown?\n- [x] Normal sinus\n- [ ] Atrial fibrillation"))
                    ])
            ]);

        var res = WorkspaceMarkdownAssembler.Assemble(draft, new Dictionary<string, string>(), allowPlaceholders: false);
        Assert.True(res.IsSuccess, res.Error?.Description);
        Assert.False(res.Value.RequiresPlaceholderImage);

        Assert.Contains("assets/fig.png", res.Value.ReferencedAssetPaths);
        Assert.Contains("assets/heart.png", res.Value.ReferencedAssetPaths);
        Assert.Contains("assets/heart_clean.png", res.Value.ReferencedAssetPaths);
        Assert.Contains("assets/heart_masks.json", res.Value.ReferencedAssetPaths);
        Assert.Contains("assets/ecg.png", res.Value.ReferencedAssetPaths);

        var articleMd = res.Value.Files["01_diagrams.md"];
        Assert.Contains("![](assets/fig.png)", articleMd);
        Assert.Contains(":::diagram-explore{image=\"assets/heart.png\"}", articleMd);
        Assert.DoesNotContain("assets/placeholder.png", articleMd);

        var quizMd = res.Value.Files["02_visual_quiz.md"];
        Assert.Contains(":::relabel{image=\"assets/heart_clean.png\"", quizMd);
        Assert.DoesNotContain("assets/placeholder.png", quizMd);
    }

    [Fact]
    public void WorkspaceMarkdownAssembler_HandlesImageFileMode_AcrossImageRelabelDiagramAndMcq()
    {
        var draft = new PromptDraft(
            Container: ContainerKind.Session,
            Title: "Cardiopulmonary Atlas",
            Description: "Structured image specs",
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            SourceText: string.Empty,
            Selections: [],
            Attachments: [],
            WorkflowMode: WorkflowMode.Direct,
            Components:
            [
                new PromptComponentPlan(
                    "a1",
                    ComponentKind.Article,
                    "Core Figures",
                    [new("image", true, 2), new("diagram-explore", true, 1)],
                    [
                        new("s_img1", "image", 1, null, new SpecContentSource(
                            Mode: SpecContentMode.ImageFile,
                            ImagePath: "assets/heart.png",
                            Caption: "Heart",
                            AltText: "Alt")),
                        new("s_img2", "image", 2, null, new SpecContentSource(
                            Mode: SpecContentMode.ImageFile,
                            ImagePath: "assets/lungs.png",
                            Caption: null,
                            AltText: "Lungs Diagram")),
                        new("s_diag", "diagram-explore", 1, null, new SpecContentSource(
                            Mode: SpecContentMode.ImageFile,
                            RawText: "- Apex: Left ventricle tip.",
                            ImagePath: "assets/mediastinum.png"))
                    ]),
                new PromptComponentPlan(
                    "q1",
                    ComponentKind.Quiz,
                    "Visual Drill",
                    [new("relabel", true, 1), new("mcq", true, 1)],
                    [
                        new("q_rel", "relabel", 1, "recall", new SpecContentSource(
                            Mode: SpecContentMode.ImageFile,
                            RawText: "prompt: Identify the chambers.",
                            ImagePath: "assets/heart_masked.png")),
                        new("q_mcq", "mcq", 1, "multi", new SpecContentSource(
                            Mode: SpecContentMode.ImageFile,
                            RawText: "Select all visible structures:\n- [x] Aorta\n- [x] Pulmonary trunk\n- [ ] Femoral artery",
                            ImagePath: "assets/ecg.png"))
                    ])
            ]);

        var res = WorkspaceMarkdownAssembler.Assemble(draft, new Dictionary<string, string>(), allowPlaceholders: false);
        Assert.True(res.IsSuccess, res.Error?.Description);
        Assert.False(res.Value.RequiresPlaceholderImage);
        Assert.Empty(res.Value.MissingContentSpecs);

        Assert.Contains("assets/heart.png", res.Value.ReferencedAssetPaths);
        Assert.Contains("assets/lungs.png", res.Value.ReferencedAssetPaths);
        Assert.Contains("assets/mediastinum.png", res.Value.ReferencedAssetPaths);
        Assert.Contains("assets/heart_masked.png", res.Value.ReferencedAssetPaths);
        Assert.Contains("assets/ecg.png", res.Value.ReferencedAssetPaths);

        var articleMd = res.Value.Files["01_core_figures.md"];
        Assert.Contains("![Alt](assets/heart.png \"Heart\")", articleMd);
        Assert.Contains("![Lungs Diagram](assets/lungs.png)", articleMd);
        Assert.Contains(":::diagram-explore{image=\"assets/mediastinum.png\"}\n- Apex: Left ventricle tip.\n:::", articleMd);
        Assert.DoesNotContain("assets/placeholder.png", articleMd);

        var quizMd = res.Value.Files["02_visual_drill.md"];
        Assert.Contains(":::relabel{image=\"assets/heart_masked.png\" mode=\"recall\" style=\"blur\"}\nprompt: Identify the chambers.\n:::", quizMd);
        Assert.Contains(":::mcq{multi=true image=\"assets/ecg.png\"}\nSelect all visible structures:", quizMd);
        Assert.DoesNotContain("assets/placeholder.png", quizMd);

        var composed = PromptComposer.Compose(draft, SpecCatalogDefaults.BuiltInFallbackCatalog);
        Assert.Contains("5 filled, 0 empty", composed.Markdown);
        Assert.Contains("Image (`assets/heart.png`)", composed.Markdown);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
