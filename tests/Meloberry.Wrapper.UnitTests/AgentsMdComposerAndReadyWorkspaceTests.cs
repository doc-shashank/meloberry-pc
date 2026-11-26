using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Domain.Services;
using Meloberry.Wrapper.Infrastructure.Cli;
using Meloberry.Wrapper.Infrastructure.Persistence;
using Meloberry.Wrapper.Infrastructure.Workspace;
using Xunit;

namespace Meloberry.Wrapper.UnitTests;

public sealed class AgentsMdComposerAndReadyWorkspaceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_agents_test_{Guid.NewGuid():N}");

    [Fact]
    public async Task ReadyWorkspaceHandler_RequiresOverwriteConfirmation_AndExportsTargetedSchemas()
    {
        var dataDir = Path.Combine(_tempRoot, "data");
        var paths = new WrapperDataPaths(dataDir);
        Directory.CreateDirectory(paths.AppsDirectory);
        var workspace = Path.Combine(_tempRoot, "ws");
        Directory.CreateDirectory(workspace);

        var settingsStore = new JsonSettingsStore(paths);
        await settingsStore.SaveAsync(new WrapperSettings(
            ActiveWorkspace: new ActiveWorkspaceState(
                WorkspacePath: workspace,
                IsInitialized: true,
                SelectedAppFileNames: ["mby-cli-2.0.0.exe"],
                StagedItems: [new StagedCopyItem(Path.Combine(_tempRoot, "ch2.pdf"), StagedCopyKind.File, "ch2.pdf", "ch2.pdf")])));

        var mbyRepoExe = TestRepoPaths.FindMbyExecutable();
        var appsStore = new ManagedAppsStore(paths);
        if (!string.IsNullOrWhiteSpace(mbyRepoExe) && File.Exists(mbyRepoExe))
        {
            await appsStore.ImportLocalExecutableAsync(mbyRepoExe);
        }

        var mbyClient = new ProcessMbyCliClient();
        var specLoader = new LoadSpecCatalogHandler(appsStore, mbyClient, settingsStore);
        var handler = new ReadyWorkspaceHandler(
            new AgentsMdFileWriter(),
            appsStore,
            mbyClient,
            new ProcessMelomgCliClient(),
            settingsStore,
            specLoader);

        var externalAttachment = Path.Combine(_tempRoot, "external_notes.pdf");
        var draft = new PromptDraft(
            Container: ContainerKind.Session,
            Title: "Chapter 2",
            Description: "Action potentials",
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            SourceText: "Focus on depolarization",
            Selections:
            [
                new ShoppingListSelection("text", true, 2),
                new ShoppingListSelection("mcq", true, 4),
                new ShoppingListSelection("link", true, 2)
            ],
            Attachments: [new SourceAttachment(externalAttachment, "pdf")],
            WorkingDirectory: workspace);

        var first = await handler.ReadyWorkspaceAsync(new ReadyWorkspaceRequest(draft, OverwriteConfirmed: false));
        Assert.True(first.IsSuccess);
        Assert.False(first.Value.OverwroteExisting);
        var exportedText = await File.ReadAllTextAsync(first.Value.OutputPath);
        Assert.Contains("Active Workspace Contract", exportedText);
        Assert.Contains("### `link` (block)", exportedText);
        Assert.Contains("**External Reference Files**", exportedText);
        Assert.Contains(externalAttachment, exportedText);
        Assert.DoesNotContain("./external_notes.pdf", exportedText);
        if (!string.IsNullOrWhiteSpace(mbyRepoExe) && File.Exists(mbyRepoExe))
        {
            Assert.Contains("mcq", first.Value.IncludedTopics);
            Assert.Contains("### `mcq` (question)", exportedText);
        }

        var statusRes = await handler.GetStatusAsync(workspace);
        Assert.True(statusRes.IsSuccess);
        Assert.True(statusRes.Value.Exists);
        Assert.Equal(exportedText, statusRes.Value.Preview);
        Assert.True(statusRes.Value.SizeBytes > 0);
        Assert.Equal(exportedText.Length, statusRes.Value.CharacterCount);
        Assert.True(statusRes.Value.ApproximateTokens > 0);

        // Subfolder export accumulates tracked Ready Workspace artifacts
        var subDir = Path.Combine(workspace, "submod");
        Directory.CreateDirectory(subDir);
        var subExport = await handler.ReadyWorkspaceAsync(new ReadyWorkspaceRequest(draft, OverwriteConfirmed: false, TargetDirectory: subDir));
        Assert.True(subExport.IsSuccess);
        Assert.Equal(Path.Combine(subDir, "AGENTS.md"), subExport.Value.OutputPath);
        var savedSettings = await settingsStore.LoadAsync();
        Assert.NotNull(savedSettings.ActiveWorkspace?.LastReadyWorkspaceArtifacts);
        Assert.Contains("AGENTS.md", savedSettings.ActiveWorkspace!.LastReadyWorkspaceArtifacts!);
        Assert.Contains("submod/AGENTS.md", savedSettings.ActiveWorkspace!.LastReadyWorkspaceArtifacts!);

        var conflict = await handler.ReadyWorkspaceAsync(new ReadyWorkspaceRequest(draft, OverwriteConfirmed: false));
        Assert.True(conflict.IsFailure);
        Assert.Equal("Workspace.AgentsMdExists", conflict.Error!.Code);

        var second = await handler.ReadyWorkspaceAsync(new ReadyWorkspaceRequest(draft, OverwriteConfirmed: true));
        Assert.True(second.IsSuccess);
        Assert.True(second.Value.OverwroteExisting);
    }

    [Fact]
    public void PromptComposer_SupportsComponentsModesSequence_AndFiveMcqFallback()
    {
        var components = new List<PromptComponentPlan>
        {
            new(
                "a1",
                ComponentKind.Article,
                "Membrane Basics",
                [
                    new("text", true, 2, "Keep concise"),
                    new("callout", true, 2, "Highlight Na+ channel", ["pearl", "clinical"],
                        ModeCounts: new Dictionary<string, int> { ["pearl"] = 1, ["clinical"] = 1 })
                ],
                OrderedItems:
                [
                    new("t1", "text", 1),
                    new("c1", "callout", 1, Subtype: "pearl"),
                    new("t2", "text", 2)
                ]),
            new("q1", ComponentKind.Quiz, "Quick Check", []) // Empty quiz -> 5 MCQs fallback!
        };

        var sessionDraft = new PromptDraft(
            Container: ContainerKind.Session,
            Title: "Neuro Physiology",
            Description: "Session blueprint",
            AuthoringFormat: AuthoringFormat.Json,
            SourceText: "",
            Selections: [],
            Attachments: [new SourceAttachment(@"C:\ws\ch2.pdf", "pdf")],
            AgentsMdExported: true,
            WorkingDirectory: @"C:\ws",
            MbyExecutablePath: @"C:\ws\mby-cli-2.0.0.exe",
            Components: components);

        var composed = PromptComposer.Compose(sessionDraft, SpecCatalogDefaults.BuiltInFallbackCatalog);
        Assert.Contains("### 1. [A1] Article: Membrane Basics", composed.Markdown);
        Assert.Contains("`text #1` -> `callout:pearl #1` -> `text #2`", composed.Markdown);
        Assert.Contains("`pearl` \u00d71, `clinical` \u00d71", composed.Markdown);
        Assert.Contains("### 2. [Q1] Quiz: Quick Check", composed.Markdown);
        Assert.Contains("| `mcq` | 5 | `mixed` | Default fallback (no quiz types selected) |", composed.Markdown);

        // StandaloneQuiz with zero selections also falls back to 5 MCQs.
        var emptyQuiz = new PromptDraft(
            Container: ContainerKind.StandaloneQuiz,
            Title: "Empty Quiz",
            Description: "",
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            SourceText: "",
            Selections: [new ShoppingListSelection("text", true)], // invalid block filtered out!
            Attachments: []);

        var quizOut = PromptComposer.Compose(emptyQuiz, SpecCatalogDefaults.BuiltInFallbackCatalog);
        Assert.DoesNotContain("| `text` |", quizOut.Markdown);
        Assert.Contains("| `mcq` | 5 | `mixed` | Default fallback (no quiz types selected) |", quizOut.Markdown);
    }

    [Fact]
    public void AgentsMdAndPromptComposer_HandleAbsoluteCliPaths_MultilineNotes_AndBacktickFences()
    {
        var agentsMd = AgentsMdComposer.Compose(new AgentsMdComposeInput(
            WorkspacePath: @"C:\ws",
            Container: ContainerKind.Session,
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            WorkspaceMbyFileName: @"C:\Users\Alice\AppData\Local\Meloberry\apps\mby-cli-2.0.0.exe",
            WorkspaceMelomgFileName: @"C:\Tools With Spaces\meloberry-melomg-2.1.0.exe",
            ExtraWorkspaceExecutables: [],
            CopiedFiles: ["ch2.pdf"],
            CopiedFolders: [],
            SelectedSpecs: [],
            MelomgSpecsText: "Uses ```triple``` backticks",
            IncludeLinkBlock: false));

        Assert.DoesNotContain(@".\C:\", agentsMd);
        Assert.Contains(@"C:\Users\Alice\AppData\Local\Meloberry\apps\mby-cli-2.0.0.exe", agentsMd);
        Assert.Contains("& \"C:\\Tools With Spaces\\meloberry-melomg-2.1.0.exe\"", agentsMd);
        Assert.Contains("````text", agentsMd);
        Assert.DoesNotContain("``Study Session", agentsMd);

        var prompt = PromptComposer.Compose(new PromptDraft(
            Container: ContainerKind.StandaloneArticle,
            Title: "Notes Test",
            Description: "",
            AuthoringFormat: AuthoringFormat.SingleMarkdown,
            SourceText: "Sample:\n```md\nhello\n```",
            Selections: [new("text", true, 1, "Line 1\nLine 2 | Pipe")],
            Attachments: [],
            MbyExecutablePath: "bad|path<>name.exe",
            WorkingDirectory: @"C:\ws"), SpecCatalogDefaults.BuiltInFallbackCatalog);

        Assert.Contains("| `text` | 1 | — | Line 1 Line 2 \\| Pipe |", prompt.Markdown);
        Assert.Contains("````text", prompt.Markdown);
        Assert.DoesNotContain("`Single Markdown File`", prompt.Markdown);
    }

    [Fact]
    public async Task ReadyWorkspaceHandler_ExcludesMissingStagedPaths_AndHonorsExplicitToolOverrides()
    {
        var dataDir = Path.Combine(_tempRoot, "data_prec");
        var paths = new WrapperDataPaths(dataDir);
        Directory.CreateDirectory(paths.AppsDirectory);
        var workspace = Path.Combine(_tempRoot, "ws_prec");
        Directory.CreateDirectory(workspace);

        var presentFile = Path.Combine(workspace, "present.txt");
        await File.WriteAllTextAsync(presentFile, "hello");
        var presentFolder = Path.Combine(workspace, "PresentFigs");
        Directory.CreateDirectory(presentFolder);

        // Create both a workspace mby.exe and a higher-priority explicit override executable.
        await File.WriteAllBytesAsync(Path.Combine(workspace, "mby-cli-1.0.0.exe"), [(byte)'M', (byte)'Z', 0x90, 0x00]);
        var overrideDir = Path.Combine(_tempRoot, "overrides");
        Directory.CreateDirectory(overrideDir);
        var overrideMby = Path.Combine(overrideDir, "custom-mby.exe");
        var overrideMelomg = Path.Combine(overrideDir, "custom-melomg.exe");
        await File.WriteAllBytesAsync(overrideMby, [(byte)'M', (byte)'Z', 0x90, 0x00]);
        await File.WriteAllBytesAsync(overrideMelomg, [(byte)'M', (byte)'Z', 0x90, 0x00]);

        var settingsStore = new JsonSettingsStore(paths);
        await settingsStore.SaveAsync(new WrapperSettings(
            ActiveWorkspace: new ActiveWorkspaceState(
                WorkspacePath: workspace,
                IsInitialized: true,
                StagedItems:
                [
                    new StagedCopyItem(presentFile, StagedCopyKind.File, "present.txt", "present.txt"),
                    new StagedCopyItem(Path.Combine(_tempRoot, "missing.pdf"), StagedCopyKind.File, "missing.pdf", "missing.pdf"),
                    new StagedCopyItem(presentFolder, StagedCopyKind.Folder, "PresentFigs", "PresentFigs"),
                    new StagedCopyItem(Path.Combine(_tempRoot, "MissingFigs"), StagedCopyKind.Folder, "MissingFigs", "MissingFigs")
                ]),
            MbyExecutableOverride: overrideMby,
            MelomgExecutableOverride: overrideMelomg));

        var appsStore = new ManagedAppsStore(paths);
        var mbyClient = new ProcessMbyCliClient();
        var specLoader = new LoadSpecCatalogHandler(appsStore, mbyClient, settingsStore);
        var handler = new ReadyWorkspaceHandler(
            new AgentsMdFileWriter(),
            appsStore,
            mbyClient,
            new ProcessMelomgCliClient(),
            settingsStore,
            specLoader);

        var draft = new PromptDraft(
            Container: ContainerKind.Session,
            Title: "Precedence Test",
            Description: "",
            AuthoringFormat: AuthoringFormat.Json,
            SourceText: "",
            Selections: [new("text", true, 1)],
            Attachments: [new SourceAttachment(Path.Combine(workspace, "deleted_in_ws.pdf"), "pdf")],
            CopiedFolders: ["NonExistentCopiedFolder"],
            UseMelomgForPdfs: true,
            WorkingDirectory: workspace);

        var res = await handler.ReadyWorkspaceAsync(new ReadyWorkspaceRequest(draft, OverwriteConfirmed: false));
        Assert.True(res.IsSuccess);

        var agentsText = await File.ReadAllTextAsync(res.Value.OutputPath);
        Assert.Contains("./present.txt", agentsText);
        Assert.Contains("./PresentFigs/", agentsText);
        Assert.DoesNotContain("./missing.pdf", agentsText);
        Assert.DoesNotContain("./deleted_in_ws.pdf", agentsText);
        Assert.DoesNotContain("./MissingFigs/", agentsText);
        Assert.DoesNotContain("./NonExistentCopiedFolder/", agentsText);

        Assert.Contains(overrideMby, agentsText);
        Assert.DoesNotContain(@".\mby-cli-1.0.0.exe", agentsText);
        Assert.Contains(overrideMelomg, agentsText);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
