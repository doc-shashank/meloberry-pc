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

    [Theory]
    [InlineData("relabel", "prompt: Label A\nboxes:\n  - label: A\n    rect: [0.1,0.1,0.2,0.2]")]
    [InlineData("diagram-explore", "- Landmark A: Desc")]
    [InlineData("mcq", "Stem?\n- [x] A\n- [ ] B")]
    [InlineData("flashcard", ":::front\nFront\n:::back\nBack")]
    [InlineData("spot-error", "The [~~wrong~~ -> right] value.")]
    [InlineData("numeric", "Compute x.")]
    [InlineData("case", "Patient presents with chest pain.")]
    public void WorkspaceMarkdownAssembler_PromotesYamlImagePath_ForAllImageCapableTopics(string topic, string body)
    {
        var raw = $"imagePath: assets/fig_01.png\n{body}";
        var kind = topic == "diagram-explore" ? ComponentKind.Article : ComponentKind.Quiz;
        var container = kind == ComponentKind.Article ? ContainerKind.StandaloneArticle : ContainerKind.StandaloneQuiz;
        var draft = new PromptDraft(
            Container: container,
            Title: "Image Promotion",
            Description: string.Empty,
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            SourceText: string.Empty,
            Selections: [],
            Attachments: [],
            Components:
            [
                new PromptComponentPlan(
                    "c1",
                    kind,
                    "Unit",
                    [new(topic, true, 1)],
                    [new("s1", topic, 1, null, new SpecContentSource(SpecContentMode.RawText, raw))])
            ]);

        var res = WorkspaceMarkdownAssembler.Assemble(draft, new Dictionary<string, string>(), allowPlaceholders: false);
        Assert.True(res.IsSuccess);
        var md = res.Value.Files.Values.Single();
        if (topic == "flashcard")
        {
            Assert.DoesNotContain(":::flashcard{image=", md);
            Assert.Contains(":::front\n![](assets/fig_01.png)\n", md);
        }
        else
        {
            Assert.Contains("image=\"assets/fig_01.png\"", md);
        }
        Assert.DoesNotContain("imagePath:", md);
        Assert.Contains("assets/fig_01.png", res.Value.ReferencedAssetPaths);
    }

    [Fact]
    public void WorkspaceMarkdownAssembler_WrapsSpacesInAngleBrackets_AndCollectsAngleBracketedAssets()
    {
        var draft = new PromptDraft(
            Container: ContainerKind.StandaloneArticle,
            Title: "Space Paths",
            Description: string.Empty,
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            SourceText: string.Empty,
            Selections: [],
            Attachments: [],
            Components:
            [
                new PromptComponentPlan(
                    "a1",
                    ComponentKind.Article,
                    "Figures",
                    [new("image", true, 2), new("text", true, 1)],
                    [
                        new("s1", "image", 1, null, new SpecContentSource(SpecContentMode.ImageFile, null, null, "assets/My Fig.png", "Cap", "Alt")),
                        new("s2", "image", 2, null, new SpecContentSource(SpecContentMode.RawText, "assets/Second (1).png")),
                        new("s3", "text", 1, null, new SpecContentSource(SpecContentMode.RawText, "See ![Inline](<assets/Inline Fig.png> \"Inline Cap\")."))
                    ])
            ]);

        var res = WorkspaceMarkdownAssembler.Assemble(draft, new Dictionary<string, string>(), allowPlaceholders: false);
        Assert.True(res.IsSuccess);
        var md = res.Value.Files.Values.Single();
        Assert.Contains("![Alt](<assets/My Fig.png> \"Cap\")", md);
        Assert.Contains("![](<assets/Second (1).png>)", md);
        Assert.Contains("assets/My Fig.png", res.Value.ReferencedAssetPaths);
        Assert.Contains("assets/Second (1).png", res.Value.ReferencedAssetPaths);
        Assert.Contains("assets/Inline Fig.png", res.Value.ReferencedAssetPaths);
    }

    [Fact]
    public void WorkspaceMarkdownAssembler_ImageFileMode_PreventsDoubleWrapping_GuardsEmptyBody_AndWrapsFlashcardSubdirectives()
    {
        var draft = new PromptDraft(
            Container: ContainerKind.StandaloneQuiz,
            Title: "ImageFile Guards",
            Description: string.Empty,
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            SourceText: string.Empty,
            Selections: [],
            Attachments: [],
            Components:
            [
                new PromptComponentPlan(
                    "q1",
                    ComponentKind.Quiz,
                    "Quiz",
                    [new("mcq", true, 3), new("flashcard", true, 1), new("numeric", true, 1)],
                    [
                        new("s1", "mcq", 1, null, new SpecContentSource(SpecContentMode.ImageFile, ":::mcq{multi=true}\nStem?\n- [x] A\n- [ ] B\n:::", null, "assets/ecg.png")),
                        new("s2", "mcq", 2, null, new SpecContentSource(SpecContentMode.ImageFile, ":::mcq{image=\"assets/custom.png\"}\nStem 2?\n- [x] A\n- [ ] B\n:::", null, "assets/ecg.png")),
                        new("s3", "mcq", 3, null, new SpecContentSource(SpecContentMode.ImageFile, null, null, "assets/blank_body.png")),
                        new("s4", "flashcard", 1, "diagram", new SpecContentSource(SpecContentMode.ImageFile, ":::front\nIdentify structure\n:::back\nMitral valve", null, "assets/valve.png")),
                        new("s5", "numeric", 1, null, new SpecContentSource(SpecContentMode.ImageFile, null, null, "assets/graph.png"))
                    ])
            ]);

        var strictRes = WorkspaceMarkdownAssembler.Assemble(draft, new Dictionary<string, string>(), allowPlaceholders: false);
        Assert.True(strictRes.IsSuccess);
        Assert.Equal(2, strictRes.Value.MissingContentSpecs.Count);
        Assert.Contains(strictRes.Value.MissingContentSpecs, m => m.SpecId == "s3");
        Assert.Contains(strictRes.Value.MissingContentSpecs, m => m.SpecId == "s5");

        var strictMd = strictRes.Value.Files.Values.Single();
        Assert.DoesNotContain(":::mcq{image=\"assets/ecg.png\"}\n:::mcq", strictMd);
        Assert.Contains(":::mcq{multi=true image=\"assets/ecg.png\"}", strictMd);
        Assert.Contains(":::mcq{image=\"assets/custom.png\"}", strictMd);
        Assert.DoesNotContain(":::mcq{image=\"assets/blank_body.png\"}\n:::", strictMd);
        Assert.DoesNotContain(":::flashcard{image=", strictMd);
        Assert.Contains(":::flashcard\n:::front\n![](assets/valve.png)\nIdentify structure\n:::back\nMitral valve\n:::", strictMd);
        Assert.DoesNotContain(":::front{image=", strictMd);

        var phRes = WorkspaceMarkdownAssembler.Assemble(draft, new Dictionary<string, string>(), allowPlaceholders: true);
        Assert.True(phRes.IsSuccess);
        Assert.Empty(phRes.Value.MissingContentSpecs);
        Assert.Contains("s3", phRes.Value.PlaceholderSpecsUsed);
        Assert.Contains("s5", phRes.Value.PlaceholderSpecsUsed);

        var phMd = phRes.Value.Files.Values.Single();
        Assert.Contains(":::mcq{image=\"assets/blank_body.png\"}", phMd);
        Assert.DoesNotContain(":::mcq{image=\"assets/blank_body.png\"}\n:::", phMd);
        Assert.Contains("assets/blank_body.png", phRes.Value.ReferencedAssetPaths);
        Assert.Contains(":::numeric{", phMd);
        Assert.Contains("answer=", phMd);
        Assert.Contains("image=\"assets/graph.png\"", phMd);

        var readiness = PromptComposer.Compose(draft with { WorkflowMode = WorkflowMode.Direct }, SpecCatalogDefaults.BuiltInFallbackCatalog);
        Assert.Contains("3 filled, 2 empty", readiness.Markdown);
    }

    [Fact]
    public void WorkspaceMarkdownAssembler_FlashcardImageInjectsIntoFront_AndSingleMcqHasNoEmptyBraces()
    {
        var draft = new PromptDraft(
            Container: ContainerKind.StandaloneQuiz,
            Title: "Valve Flashcards",
            Description: string.Empty,
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            SourceText: string.Empty,
            Selections:
            [
                new("flashcard", true, 4),
                new("mcq", true, 3)
            ],
            Attachments: [],
            Components:
            [
                new PromptComponentPlan(
                    "q1",
                    ComponentKind.Quiz,
                    "Valves Quiz",
                    [new("flashcard", true, 4), new("mcq", true, 3)],
                    [
                        new("f1", "flashcard", 1, "diagram", new SpecContentSource(SpecContentMode.ImageFile, ":::front\nIdentify structure\n:::back\nMitral valve", null, "assets/valve.png")),
                        new("f2", "flashcard", 2, "diagram", new SpecContentSource(SpecContentMode.ImageFile, ":::flashcard\n:::front\nPrefenced front\n:::back\nPrefenced back\n:::", null, "assets/valve 2.png")),
                        new("f3", "flashcard", 3, "diagram", new SpecContentSource(SpecContentMode.RawText, "imagePath: assets/yaml_valve.png\n:::front\nYAML front\n:::back\nYAML back")),
                        new("f4", "flashcard", 4, "diagram", new SpecContentSource(SpecContentMode.ImageFile, null, null, "assets/placeholder_valve.png")),
                        new("m1", "mcq", 1, "single", null),
                        new("m2", "mcq", 2, "mixed", null),
                        new("m3", "mcq", 3, "multi", null)
                    ])
            ]);

        var res = WorkspaceMarkdownAssembler.Assemble(draft, new Dictionary<string, string>(), allowPlaceholders: true);
        Assert.True(res.IsSuccess);
        var md = res.Value.Files.Values.Single();

        Assert.DoesNotContain(":::flashcard{image=", md);
        Assert.Contains(":::flashcard\n:::front\n![](assets/valve.png)\nIdentify structure\n:::back\nMitral valve\n:::", md);
        Assert.Contains(":::flashcard\n:::front\n![](<assets/valve 2.png>)\nPrefenced front\n:::back\nPrefenced back\n:::", md);
        Assert.Contains(":::flashcard\n:::front\n![](assets/yaml_valve.png)\nYAML front\n:::back\nYAML back\n:::", md);
        Assert.Contains(":::flashcard\n:::front\n![](assets/placeholder_valve.png)\nFlashcard Front 4\n:::back\nFlashcard Answer 4\n:::", md);

        Assert.DoesNotContain(":::mcq{}", md);
        Assert.Contains(":::mcq\nMCQ Placeholder 1:", md);
        Assert.Contains(":::mcq\nMCQ Placeholder 2:", md);
        Assert.Contains(":::mcq{multi=true}\nMCQ Placeholder 3:", md);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
