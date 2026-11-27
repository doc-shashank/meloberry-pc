using System.Text.Json;
using Meloberry.Wrapper.Application;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Domain.Services;
using Meloberry.Wrapper.Infrastructure;
using Meloberry.Wrapper.Infrastructure.Persistence;
using Meloberry.Wrapper.Infrastructure.Server;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.Wrapper.UnitTests;

public sealed class TokenAndScaffoldTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_scaffold_test_{Guid.NewGuid():N}");

    public TokenAndScaffoldTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void EstimateTokens_FollowsBpeStructureAndDistinguishesDensity()
    {
        Assert.Equal(0, AgentsMdComposer.EstimateTokens(""));
        Assert.Equal(0, AgentsMdComposer.EstimateTokens(" "));

        var shortText = "Hello world";
        var richJson = """{"container":"session","components":[{"kind":"article","blocks":[{"kind":"table","headers":["A","B"]}]}]}""";

        var shortCount = AgentsMdComposer.EstimateTokens(shortText);
        var jsonCount = AgentsMdComposer.EstimateTokens(richJson);

        Assert.InRange(shortCount, 2, 4);
        Assert.True(jsonCount > shortCount * 5, $"Expected JSON token count ({jsonCount}) to be > 5x short text ({shortCount}).");
    }

    [Fact]
    public void PromptComposer_RespectsUseMelomgForPdfsRegardlessOfAttachments_AndConvertsToPlainText()
    {
        var withMelomg = new PromptDraft(
            Container: ContainerKind.Session,
            Title: "Cardiology Review",
            Description: "",
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            SourceText: "Focus on valve murmurs.",
            Selections: [new("mcq", true, 5)],
            Attachments: [],
            UseMelomgForPdfs: true,
            WorkflowMode: WorkflowMode.ClassicPrompt);

        var withoutMelomg = withMelomg with
        {
            UseMelomgForPdfs = false,
            Attachments = [new("chapter2.pdf", "pdf")]
        };

        var composedOn = PromptComposer.Compose(withMelomg, []);
        var composedOff = PromptComposer.Compose(withoutMelomg, []);

        Assert.Contains("melomg", composedOn.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PDF Figure & Label Masking Workflow", composedOn.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PDF Figure & Label Masking Workflow", composedOff.Markdown, StringComparison.OrdinalIgnoreCase);

        var plain = PromptComposer.ToPlainText("## Header\n\nUse **bold** and `mby apply`.\n```json\n{\"a\":1}\n```");
        Assert.DoesNotContain("##", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("**", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("```", plain, StringComparison.Ordinal);
        Assert.Contains("Header", plain, StringComparison.Ordinal);
        Assert.Contains("mby apply", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionScaffoldGenerator_RejectsMissingQty_AndHonorsOrganizationOrderAndFallback()
    {
        var missingQtyDraft = new PromptDraft(
            Container: ContainerKind.Session,
            Title: "Broken Draft",
            Description: "",
            AuthoringFormat: AuthoringFormat.Json,
            SourceText: "",
            Selections: [],
            Attachments: [],
            Components:
            [
                new("a1", ComponentKind.Article, "Intro", [new("text", true, null)])
            ]);

        var failed = SessionScaffoldGenerator.Generate(missingQtyDraft, []);
        Assert.True(failed.IsFailure);
        Assert.Contains("Qty", failed.Error!.Description, StringComparison.OrdinalIgnoreCase);

        var quizFallbackDraft = new PromptDraft(
            Container: ContainerKind.StandaloneQuiz,
            Title: "Empty Quiz",
            Description: "",
            AuthoringFormat: AuthoringFormat.Json,
            SourceText: "",
            Selections: [],
            Attachments: []);

        var quizResult = SessionScaffoldGenerator.Generate(quizFallbackDraft, []);
        Assert.True(quizResult.IsSuccess);
        using var quizDoc = JsonDocument.Parse(quizResult.Value.RawJson);
        Assert.Equal("quiz", quizDoc.RootElement.GetProperty("kind").GetString());
        Assert.Equal(5, quizDoc.RootElement.GetProperty("questions").GetArrayLength());

        var sessionDraft = new PromptDraft(
            Container: ContainerKind.Session,
            Title: "Organized Session",
            Description: "Structured sequence",
            AuthoringFormat: AuthoringFormat.Json,
            SourceText: "",
            Selections: [],
            Attachments: [],
            Components:
            [
                new(
                    "a1",
                    ComponentKind.Article,
                    "Part 1",
                    [
                        new("text", true, 1),
                        new("table", true, 1, null, ["stickyFirstCol", "hideableCols"]),
                        new("callout", true, 1, null, ["warning"])
                    ],
                    OrderedItems:
                    [
                        new("i1", "table", 1),
                        new("i2", "text", 1),
                        new("i3", "callout", 1)
                    ]),
                new(
                    "q1",
                    ComponentKind.Quiz,
                    "Check",
                    [new("assertion-reason", true, 2)],
                    OrderedItems: [new("q1_1", "assertion-reason", 1)])
            ]);

        var sessionResult = SessionScaffoldGenerator.Generate(sessionDraft, []);
        Assert.True(sessionResult.IsSuccess);
        Assert.NotNull(sessionResult.Value.Warnings);
        Assert.Contains(sessionResult.Value.Warnings!, w => w.Contains("assertion-reason", StringComparison.OrdinalIgnoreCase));
        using var sessionDoc = JsonDocument.Parse(sessionResult.Value.RawJson);
        var comps = sessionDoc.RootElement.GetProperty("components");
        Assert.Equal(2, comps.GetArrayLength());
        var blocks = comps[0].GetProperty("blocks");
        Assert.Equal(3, blocks.GetArrayLength());
        Assert.Equal("table", blocks[0].GetProperty("kind").GetString());
        Assert.Equal("text", blocks[1].GetProperty("kind").GetString());
        Assert.Equal("callout", blocks[2].GetProperty("kind").GetString());
        Assert.Equal("warning", blocks[2].GetProperty("tone").GetString());
        var arQuestions = comps[1].GetProperty("questions");
        Assert.Equal(2, arQuestions.GetArrayLength());
        Assert.Equal("assertion-reason", arQuestions[0].GetProperty("type").GetString());
        Assert.Equal("assertion-reason", arQuestions[1].GetProperty("type").GetString());
    }

    [Fact]
    public void SessionScaffoldGenerator_SupportsMixedMcqAndFlashcard_AndReconcilesTrimAndAppend()
    {
        var mixedDraft = new PromptDraft(
            Container: ContainerKind.StandaloneQuiz,
            Title: "Mixed Drill",
            Description: "",
            AuthoringFormat: AuthoringFormat.Json,
            SourceText: "",
            Selections: [],
            Attachments: [],
            Components:
            [
                new(
                    "q1",
                    ComponentKind.Quiz,
                    "Mixed Quiz",
                    [
                        new("mcq", true, 3, Modes: ["mixed"]),
                        new("flashcard", true, 2, Modes: ["mixed"])
                    ],
                    OrderedItems:
                    [
                        new("m1", "mcq", 1),
                        new("m2", "mcq", 2),
                        new("m3", "mcq", 3),
                        new("m4", "mcq", 4),
                        new("f1", "flashcard", 1)
                    ])
            ]);

        var res = SessionScaffoldGenerator.Generate(mixedDraft, SpecCatalogDefaults.BuiltInFallbackCatalog);
        Assert.True(res.IsSuccess);
        Assert.True(res.Value.RequiresPlaceholderImage);
        Assert.NotNull(res.Value.Warnings);
        Assert.Contains(res.Value.Warnings!, w => w.Contains("added 1 'flashcard'", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(res.Value.Warnings!, w => w.Contains("trimmed 1 'mcq'", StringComparison.OrdinalIgnoreCase));

        using var doc = JsonDocument.Parse(res.Value.RawJson);
        var questions = doc.RootElement.GetProperty("questions");
        Assert.Equal(5, questions.GetArrayLength());

        Assert.False(questions[0].GetProperty("isMultiCorrect").GetBoolean());
        Assert.True(questions[1].GetProperty("isMultiCorrect").GetBoolean());
        Assert.False(questions[2].GetProperty("isMultiCorrect").GetBoolean());

        Assert.False(questions[3].GetProperty("front").TryGetProperty("imagePath", out _));
        Assert.True(questions[4].GetProperty("front").TryGetProperty("imagePath", out _));
    }

    [Fact]
    public void SessionScaffoldGenerator_SupportsBothFalseAssertionReason_FiltersOrderedItems_AndLinksAllCards()
    {
        var arGroups = SpecCatalogDefaults.GetModeGroupsForTopic("assertion-reason");
        Assert.Equal(2, arGroups.Count);
        Assert.Contains(arGroups, g => g.MultiSelect && g.Options.Any(o => o.Key == "both-false"));

        var draft = new PromptDraft(
            Container: ContainerKind.Session,
            Title: "Scaffold Edge Cases",
            Description: "",
            AuthoringFormat: AuthoringFormat.Json,
            SourceText: "",
            Selections: [],
            Attachments: [],
            Components:
            [
                new(
                    "a1",
                    ComponentKind.Article,
                    "Article",
                    [
                        new("text", true, 1),
                        new("link", true, 2)
                    ],
                    OrderedItems:
                    [
                        new("stale_img", "image", 1),
                        new("t1", "text", 1),
                        new("l1", "link", 1),
                        new("l2", "link", 2)
                    ]),
                new(
                    "q1",
                    ComponentKind.Quiz,
                    "Quiz",
                    [new("assertion-reason", true, 1, Modes: ["both-false"])])
            ]);

        var res = SessionScaffoldGenerator.Generate(draft, SpecCatalogDefaults.BuiltInFallbackCatalog);
        Assert.True(res.IsSuccess);
        using var doc = JsonDocument.Parse(res.Value.RawJson);
        var comps = doc.RootElement.GetProperty("components");
        var article = comps[0];
        var blocks = article.GetProperty("blocks");
        Assert.Single(blocks.EnumerateArray());
        var textBody = blocks[0].GetProperty("text").GetString()!;
        Assert.Contains("mby://card/card-1", textBody);
        Assert.Contains("mby://card/card-2", textBody);
        Assert.Equal(2, article.GetProperty("links").GetArrayLength());

        var ar = comps[1].GetProperty("questions")[0];
        Assert.Equal("5opt", ar.GetProperty("scheme").GetString());
        Assert.Equal("both-false", ar.GetProperty("answer").GetString());
    }

    [Fact]
    public async Task SessionTemplateHandler_AppliesGeneratedJsonWithLiveMbyCli()
    {
        var liveMby = TestRepoPaths.FindMbyExecutable();
        if (string.IsNullOrWhiteSpace(liveMby) || !File.Exists(liveMby))
        {
            return;
        }

        var wsDir = Path.Combine(_tempRoot, "ws");
        Directory.CreateDirectory(wsDir);
        File.Copy(liveMby, Path.Combine(wsDir, "mby-cli-2.0.0.exe"), overwrite: true);

        var services = new ServiceCollection();
        services.AddSingleton<IAppShutdownCoordinator>(new NoopShutdown());
        services.AddSingleton<INativeDialogService>(new NoopDialogs());
        services.AddSingleton<IExternalUrlLauncher>(new NoopLauncher());
        services.AddWrapperApplication(typeof(TokenAndScaffoldTests).Assembly);
        services.AddWrapperInfrastructure(Path.Combine(_tempRoot, "data"));

        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<SessionTemplateHandler>();

        var draft = new PromptDraft(
            Container: ContainerKind.Session,
            Title: "Scaffold CLI Integration",
            Description: "Applies live template",
            AuthoringFormat: AuthoringFormat.Json,
            SourceText: "",
            Selections: [],
            Attachments: [],
            WorkingDirectory: wsDir,
            Components:
            [
                new(
                    "a1",
                    ComponentKind.Article,
                    "Visual Overview",
                    [
                        new("text", true, 1),
                        new("image", true, 1),
                        new("callout", true, 1, null, ["pearl"])
                    ]),
                new(
                    "q1",
                    ComponentKind.Quiz,
                    "Quick Drill",
                    [
                        new("mcq", true, 2, null, ["single"]),
                        new("relabel", true, 1)
                    ])
            ]);

        var res = await handler.GenerateDraftTemplateAsync(draft);
        Assert.True(res.IsSuccess, res.Error?.Description);
        Assert.True(File.Exists(res.Value.JsonPath));
        Assert.True(File.Exists(Path.Combine(wsDir, "assets", "placeholder.png")));
        Assert.False(string.IsNullOrWhiteSpace(res.Value.WorkspaceId));
    }

    [Fact]
    public void SemverComparer_ExtractVersion_HandlesExePrereleaseAndBuildMetadata()
    {
        Assert.Equal("2.0.0", SemverComparer.ExtractVersion("mby-cli-2.0.0.exe"));
        Assert.Equal("2.0.0-rc.1", SemverComparer.ExtractVersion("mby-cli-2.0.0-rc.1.exe"));
        Assert.Equal("1.2.3", SemverComparer.ExtractVersion("1.2.3+abc123"));
        Assert.Null(SemverComparer.ExtractVersion(null));
        Assert.Null(SemverComparer.ExtractVersion("mby.exe"));
    }

    [Fact]
    public void SpecCatalogDefaults_GetCategoryForTopic_MapsBlocksQuestionsContainersAndUnknown()
    {
        Assert.Equal("block", SpecCatalogDefaults.GetCategoryForTopic("text"));
        Assert.Equal("block", SpecCatalogDefaults.GetCategoryForTopic("LINK"));
        Assert.Equal("question", SpecCatalogDefaults.GetCategoryForTopic("mcq"));
        Assert.Equal("question", SpecCatalogDefaults.GetCategoryForTopic("relabel"));
        Assert.Equal("container", SpecCatalogDefaults.GetCategoryForTopic("session"));
        Assert.Equal("other", SpecCatalogDefaults.GetCategoryForTopic("unknown-topic"));
    }

    [Fact]
    public void PromptComposer_DirectAndLiveAgentAndClassicModes_RenderExpectedContracts()
    {
        var directDraft = new PromptDraft(
            Container: ContainerKind.Session,
            Title: "Renal Physiology",
            Description: "Filtration and clearance",
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            SourceText: "Ignored long notes in direct mode",
            Selections: [],
            Attachments: [],
            WorkingDirectory: @"C:\ws\renal",
            WorkflowMode: WorkflowMode.Direct,
            AllowPlaceholdersOnFinalize: true,
            Components:
            [
                new PromptComponentPlan(
                    "a1",
                    ComponentKind.Article,
                    "Glomerular Filtration",
                    [new ShoppingListSelection("text", true, 1), new ShoppingListSelection("image", true, 1)],
                    [
                        new OrderedSpecInstance("s1", "text", 1, Content: new SpecContentSource(SpecContentMode.RawText, "Starling forces...")),
                        new OrderedSpecInstance("s2", "image", 1)
                    ]),
                new PromptComponentPlan(
                    "q1",
                    ComponentKind.Quiz,
                    "Quick Check",
                    [new ShoppingListSelection("mcq", true, 1)],
                    [
                        new OrderedSpecInstance("s3", "mcq", 1, Subtype: "single", Content: new SpecContentSource(SpecContentMode.MarkdownFile, null, "q1.md"))
                    ])
            ]);

        var directOut = PromptComposer.Compose(directDraft, SpecCatalogDefaults.BuiltInFallbackCatalog);
        Assert.Contains("Build Readiness", directOut.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 components", directOut.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 filled", directOut.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 empty", directOut.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Schema & Playbook Contract", directOut.Markdown);

        var liveOut = PromptComposer.Compose(directDraft with
        {
            Title = "Cardio Review",
            SourceText = "Focus on aortic stenosis.",
            Attachments = [new SourceAttachment(@"C:\ws\renal\chapter4.pdf", "pdf")],
            UseMelomgForPdfs = true,
            WorkflowMode = WorkflowMode.LiveAgent
        }, SpecCatalogDefaults.BuiltInFallbackCatalog);

        Assert.Contains(".\\mby-pc.bat status --json", liveOut.Markdown);
        Assert.Contains(".\\mby-pc.bat draft get --json", liveOut.Markdown);
        Assert.Contains(".\\mby-pc.bat draft spec set-content", liveOut.Markdown);
        Assert.Contains(".\\mby-pc.bat finalize --json", liveOut.Markdown);
        Assert.Contains("RawText", liveOut.Markdown);
        Assert.Contains("MarkdownFile", liveOut.Markdown);
        Assert.Contains("Empty", liveOut.Markdown);

        var classicWs = PromptComposer.Compose(new PromptDraft(
            Container: ContainerKind.Session,
            Title: "Classical Workspace Prompt",
            Description: "",
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            SourceText: "",
            Selections: [new ShoppingListSelection("mcq", true, 3)],
            Attachments: [],
            UseMelomgForPdfs: true,
            WorkingDirectory: @"C:\ws\bio",
            MbyExecutablePath: @"C:\Users\Alice\AppData\Local\Meloberry\apps\mby-cli-2.1.2.exe",
            MelomgExecutablePath: @"C:\Users\Alice\AppData\Local\Meloberry\apps\meloberry-melomg-2.1.0.exe",
            WorkflowMode: WorkflowMode.ClassicPrompt), SpecCatalogDefaults.BuiltInFallbackCatalog);

        Assert.Contains("`.\\mby-pc.bat mby`", classicWs.Markdown);
        Assert.Contains("`.\\mby-pc.bat melomg`", classicWs.Markdown);
        Assert.DoesNotContain(@"AppData\Local\Meloberry\apps\mby-cli", classicWs.Markdown);
    }

    [Fact]
    public async Task DefaultWorkflowMode_IsDirectAcrossDomainDraftStoreAndJsStore()
    {
        var domainDraft = new PromptDraft(
            Container: ContainerKind.Session,
            Title: "Default Mode Check",
            Description: string.Empty,
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            SourceText: string.Empty,
            Selections: [],
            Attachments: []);

        Assert.Equal(WorkflowMode.Direct, domainDraft.WorkflowMode);

        var ws = Path.Combine(_tempRoot, "default_ws");
        Directory.CreateDirectory(ws);
        var draftStore = new WorkspaceDraftStore();
        var loaded = await draftStore.LoadAsync(ws);
        Assert.Equal(WorkflowMode.Direct, loaded.WorkflowMode);

        var services = new ServiceCollection();
        services.AddSingleton<IAppShutdownCoordinator>(new NoopShutdown());
        services.AddSingleton<INativeDialogService>(new NoopDialogs());
        services.AddSingleton<IExternalUrlLauncher>(new NoopLauncher());
        services.AddWrapperApplication(typeof(LocalHttpWrapperServer).Assembly);
        services.AddWrapperInfrastructure(Path.Combine(_tempRoot, "srv_store"));

        await using var provider = services.BuildServiceProvider();
        await using var server = provider.GetRequiredService<LocalHttpWrapperServer>();
        server.Start();

        using var http = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        var storeJs = await http.GetStringAsync("/shell/js/store.js");
        Assert.Contains("workflowMode: \"direct\"", storeJs);
    }

    [Fact]
    public void ReadinessAndPrompts_CountEmptyComponents_AndUseDetailsAndFinalizeCopy()
    {
        var draft = new PromptDraft(
            Container: ContainerKind.Session,
            Title: "Cardio Module",
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
                    "Filled Article",
                    [new("text", true, 1)],
                    [new("s1", "text", 1, null, new SpecContentSource(SpecContentMode.RawText, "## Ready"))]),
                new PromptComponentPlan(
                    "q1",
                    ComponentKind.Quiz,
                    "Empty Quiz",
                    [],
                    [])
            ],
            WorkflowMode: WorkflowMode.Direct);

        var directPrompt = PromptComposer.Compose(draft, SpecCatalogDefaults.BuiltInFallbackCatalog);
        Assert.Contains("2 components, 2 specs (1 filled, 1 empty)", directPrompt.Markdown);
        Assert.Contains("in Finalize", directPrompt.Markdown);
        Assert.DoesNotContain("in Review", directPrompt.Markdown);

        var livePrompt = PromptComposer.Compose(draft with { WorkflowMode = WorkflowMode.LiveAgent }, SpecCatalogDefaults.BuiltInFallbackCatalog);
        Assert.Contains("2 components, 2 specs (1 filled, 1 empty)", livePrompt.Markdown);
        Assert.Contains("Step 2 - Details", livePrompt.Markdown);
        Assert.Contains("ui view details --json", livePrompt.Markdown);
        Assert.DoesNotContain("Step 2 - Format", livePrompt.Markdown);
        Assert.DoesNotContain("ui view format", livePrompt.Markdown);

        var agentsMd = AgentsMdComposer.Compose(new AgentsMdComposeInput(
            WorkspacePath: @"C:\ws\cardio",
            WorkspaceMbyFileName: "mby.exe",
            WorkspaceMelomgFileName: null,
            ExtraWorkspaceExecutables: [],
            CopiedFiles: [],
            CopiedFolders: [],
            Container: ContainerKind.Session,
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            SelectedSpecs: [],
            MelomgSpecsText: null,
            IncludeLinkBlock: false,
            UsesWorkspaceShim: true,
            WorkflowMode: WorkflowMode.LiveAgent));
        Assert.Contains("Step 2 - Details", agentsMd);
        Assert.Contains("ui view details --json", agentsMd);
        Assert.DoesNotContain("Step 2 - Format", agentsMd);
        Assert.DoesNotContain("ui view format", agentsMd);
    }

    [Fact]
    public void SessionScaffoldGenerator_ExpandsMultiModeCalloutsViaSpecOrderExpander_WhenOrderedItemsNull()
    {
        var draft = new PromptDraft(
            Container: ContainerKind.StandaloneArticle,
            Title: "Callout Modes Test",
            Description: string.Empty,
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            SourceText: string.Empty,
            Selections: [new("callout", true, 2, null, ["pearl", "clinical"])],
            Attachments: [],
            Components:
            [
                new PromptComponentPlan(
                    "a1",
                    ComponentKind.Article,
                    "Core Concepts",
                    [new("callout", true, 2, null, ["pearl", "clinical"])],
                    OrderedItems: null)
            ]);

        var res = SessionScaffoldGenerator.Generate(draft, SpecCatalogDefaults.BuiltInFallbackCatalog);
        Assert.True(res.IsSuccess, res.Error?.Description);
        using var doc = JsonDocument.Parse(res.Value.RawJson);
        var blocks = doc.RootElement.GetProperty("blocks");
        Assert.Equal(2, blocks.GetArrayLength());
        Assert.Equal("pearl", blocks[0].GetProperty("tone").GetString());
        Assert.Equal("clinical", blocks[1].GetProperty("tone").GetString());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore temp cleanup contention.
        }
    }
}
