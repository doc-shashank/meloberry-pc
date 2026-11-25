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
                    new("callout", true, 1, "Highlight Na+ channel", ["clinical"])
                ],
                OrderedItems:
                [
                    new("t1", "text", 1),
                    new("c1", "callout", 1),
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
        Assert.Contains("`text #1` -> `callout #1` -> `text #2`", composed.Markdown);
        Assert.Contains("`clinical`", composed.Markdown);
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

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
