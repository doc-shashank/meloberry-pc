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
using Meloberry.Wrapper.Infrastructure.Workspace;
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
        Assert.Equal(0, TokenEstimator.EstimateTokens(""));
        Assert.Equal(0, TokenEstimator.EstimateTokens(" "));

        var shortText = "Hello world";
        var richJson = """{"container":"session","components":[{"kind":"article","blocks":[{"kind":"table","headers":["A","B"]}]}]}""";

        var shortCount = TokenEstimator.EstimateTokens(shortText);
        var jsonCount = TokenEstimator.EstimateTokens(richJson);

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
    public void MbyCliCompatibility_EvaluatesMinimumFloorPrereleasesAndCapabilities()
    {
        var tooOld = MbyCliCompatibility.Evaluate("2.1.6", MbyCliCompatibility.RequiredCapabilities);
        Assert.False(tooOld.IsCompatible);
        Assert.Contains("2.2.0", tooOld.IncompatibilityReason);

        var beta1 = MbyCliCompatibility.Evaluate("2.2.0-beta-1", MbyCliCompatibility.RequiredCapabilities);
        Assert.True(beta1.IsCompatible, beta1.IncompatibilityReason);

        var missingCap = MbyCliCompatibility.Evaluate("2.2.0", ["spec.modeGroups"]);
        Assert.False(missingCap.IsCompatible);
        Assert.Contains("build.allThemes", missingCap.IncompatibilityReason);

        var unparseable = MbyCliCompatibility.Evaluate("legacy", MbyCliCompatibility.RequiredCapabilities);
        Assert.False(unparseable.IsCompatible);
    }

    [Fact]
    public void SpecOrderExpander_ExpandsUsingCatalogModeGroupsAndOrderedItems()
    {
        IReadOnlyList<SpecTopicItem> catalog =
        [
            new(
                "callout",
                "content",
                "block",
                "Callout",
                ":::callout",
                "article",
                ModeGroups: [new SpecModeGroup("tone", "Tone", true, [new("pearl", "Pearl"), new("clinical", "Clinical"), new("warning", "Warning")])]),
            new(
                "assertion-reason",
                "content",
                "question",
                "Assertion-Reason",
                ":::assertion-reason",
                "quiz",
                ModeGroups:
                [
                    new SpecModeGroup("scheme", "Scheme", false, [new("4opt", "4-Option"), new("5opt", "5-Option")]),
                    new SpecModeGroup("answer", "Answers", true, [new("both-true", "Both True"), new("both-false", "Both False")])
                ])
        ];

        var calloutComp = new PromptComponentPlan(
            "a1",
            ComponentKind.Article,
            "Core Concepts",
            [new("callout", true, 2, null, ["pearl", "clinical"])],
            OrderedItems: null);

        var expandedCallouts = SpecOrderExpander.Expand(calloutComp, specs: catalog);
        Assert.Equal(2, expandedCallouts.Count);
        Assert.Equal("pearl", expandedCallouts[0].Subtype);
        Assert.Equal("clinical", expandedCallouts[1].Subtype);

        var arComp = new PromptComponentPlan(
            "q1",
            ComponentKind.Quiz,
            "Check",
            [new("assertion-reason", true, 1, Modes: ["5opt"])]);

        var expandedAr = Assert.Single(SpecOrderExpander.Expand(arComp, specs: catalog));
        Assert.Equal("assertion-reason", expandedAr.Topic);
        Assert.Equal("5opt", expandedAr.Subtype);
    }

    [Fact]
    public async Task SessionTemplateHandler_RejectsIncompatibleMbyCliAndMissingQty()
    {
        var dataDir = Path.Combine(_tempRoot, "sess_compat_data");
        var paths = new WrapperDataPaths(dataDir);
        Directory.CreateDirectory(paths.AppsDirectory);
        await File.WriteAllBytesAsync(Path.Combine(paths.AppsDirectory, "mby-cli-2.1.6.exe"), [(byte)'M', (byte)'Z', 1]);
        var settingsStore = new JsonSettingsStore(paths);
        var appsStore = new ManagedAppsStore(paths);
        var managedApps = new ManagedAppsHandler(appsStore, new StubToolDownloader(), settingsStore, new StubVersionMbyClient("2.1.6", []));
        var handler = new SessionTemplateHandler(managedApps, new StubVersionMbyClient("2.1.6", []), settingsStore);

        var missingQtyDraft = new PromptDraft(
            Container: ContainerKind.Session,
            Title: "Broken Draft",
            Description: "",
            AuthoringFormat: AuthoringFormat.Json,
            SourceText: "",
            Selections: [],
            Attachments: [],
            Components: [new("a1", ComponentKind.Article, "Intro", [new("text", true, null)])]);

        var incompatRes = await handler.BuildRawJsonAsync(missingQtyDraft);
        Assert.True(incompatRes.IsFailure);
        Assert.Equal("Cli.Incompatible", incompatRes.Error!.Code);

        var okApps = new ManagedAppsHandler(appsStore, new StubToolDownloader(), settingsStore, new StubVersionMbyClient("2.2.0-beta-1", MbyCliCompatibility.RequiredCapabilities));
        var okHandler = new SessionTemplateHandler(okApps, new StubVersionMbyClient("2.2.0-beta-1", MbyCliCompatibility.RequiredCapabilities), settingsStore);
        var qtyFail = await okHandler.BuildRawJsonAsync(missingQtyDraft);
        Assert.True(qtyFail.IsFailure);
        Assert.Contains("Qty", qtyFail.Error!.Description, StringComparison.OrdinalIgnoreCase);
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
        File.Copy(liveMby, Path.Combine(wsDir, Path.GetFileName(liveMby)), overwrite: true);

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

        var agentsMd = WrapperAgentsAppendixComposer.CombineWithBaseAgentsMd(
            "# mby CLI AGENTS.md\n\nBase guide.\n",
            new WrapperAgentsAppendixInput(
                WorkspacePath: @"C:\ws\cardio",
                WorkspaceMelomgFileName: null,
                ExtraWorkspaceExecutables: [],
                CopiedFiles: [],
                CopiedFolders: [],
                MelomgSpecsText: null,
                ExternalAttachments: null,
                UsesWorkspaceShim: true,
                WorkflowMode: WorkflowMode.LiveAgent));
        Assert.Contains("Step 2 - Details", agentsMd);
        Assert.Contains("ui view details --json", agentsMd);
        Assert.DoesNotContain("Step 2 - Format", agentsMd);
        Assert.DoesNotContain("ui view format", agentsMd);
    }

    private sealed class StubToolDownloader : IGitHubToolDownloader
    {
        public Task<Result<ToolReleaseCandidate>> CheckLatestToolAsync(string toolKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> DownloadToTempFileAsync(ToolReleaseCandidate candidate, Action<DownloadProgressSnapshot> progressCallback, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubVersionMbyClient(string version, IReadOnlyList<string> capabilities) : IMbyCliClient
    {
        public Task<Result<MbyCliVersionInfo>> GetVersionInfoAsync(string exePath, CancellationToken ct = default) =>
            Task.FromResult(Result<MbyCliVersionInfo>.Success(MbyCliCompatibility.Evaluate(version, capabilities)));
        public Task<Result<IReadOnlyList<SpecTopicItem>>> GetSpecCatalogAsync(string exePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<SpecCatalogResult>> GetSpecCatalogWithIconsAsync(string exePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<AgentsMdSpecDetail>>> GetSpecDetailsAsync(string exePath, string? workingDir, IReadOnlyList<string> topics, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<WorkspaceSummary>>> ListWorkspacesAsync(string exePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<DiscoveredWebApp>>> ListWebAppsAsync(string exePath, string directory, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> BuildWorkspaceAsync(string exePath, string workspaceId, string outputDir, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> BuildWorkspaceAsync(string exePath, string workspaceId, string outputDir, string? workingDir, bool includeAllThemes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> ExportPackageAsync(string exePath, string workspaceId, string target, string outputPath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> ApplySpecJsonAsync(string exePath, string specFilePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> ApplyAtAsync(string exePath, string targetAddr, string specPath, int insertAt, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<ScaffoldCliResult>> ScaffoldAsync(string exePath, string blueprintJson, string format, string? outputPath, bool apply, bool allowPlaceholders, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<AssetAddCliResult>> AddAssetAsync(string exePath, string? workspaceAddr, string? targetDir, string sourceFilePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> PatchWithMarkdownAsync(string exePath, string targetAddr, string markdownFragment, string? subtype, string? imagePath, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<PrimeAgentsMdCliResult>> PrimeAgentsMdAsync(string exePath, IReadOnlyList<string> topics, string container, string authoringFormat, string cliPrefix, string? workspacePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IngestAnalysisCliResult>> IngestAnalysisAsync(string exePath, string webAppDir, string payloadJson, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> MoveEntityAsync(string exePath, string addr, string targetCompAddr, int? index, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> ReorderContainerAsync(string exePath, string containerAddr, IReadOnlyList<string> orderedIds, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<CloneEntityCliResult>> CloneEntityAsync(string exePath, string addr, string? targetCompAddr, int? insertAt, string? newRef, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<MbyBatchExecutionResult>> RunBatchAsync(string exePath, string batchJson, string? workingDir, CancellationToken ct = default) => throw new NotSupportedException();
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
