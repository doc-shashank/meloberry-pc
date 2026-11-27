using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.Persistence;
using Meloberry.Wrapper.Infrastructure.Server;
using Meloberry.Wrapper.Infrastructure.Workspace;
using Xunit;

namespace Meloberry.Wrapper.UnitTests;

public sealed class LiveDraftAndControlTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_live_test_{Guid.NewGuid():N}");

    [Fact]
    public async Task WorkspaceDraftStore_SynthesizesDefault_RoundTripsContent_AndHandlesConcurrentSaves()
    {
        var ws = Path.Combine(_tempRoot, "ws_draft");
        Directory.CreateDirectory(ws);

        var draftStore = new WorkspaceDraftStore();

        // Missing file -> clean default draft from first built-in preset
        var initial = await draftStore.LoadAsync(ws);
        Assert.Equal(WorkflowMode.Direct, initial.WorkflowMode);
        Assert.False(initial.AllowPlaceholdersOnFinalize);
        Assert.NotNull(initial.Components);
        Assert.NotEmpty(initial.Components);

        var mdFile = Path.Combine(ws, "notes", "intro.md");
        Directory.CreateDirectory(Path.GetDirectoryName(mdFile)!);
        await File.WriteAllTextAsync(mdFile, "# Intro\nBody");

        var updated = initial with
        {
            Title = "Neuroscience Ch 3",
            WorkflowMode = WorkflowMode.LiveAgent,
            AllowPlaceholdersOnFinalize = true,
            Components =
            [
                new PromptComponentPlan(
                    "a1",
                    ComponentKind.Article,
                    "Synaptic Transmission",
                    [new ShoppingListSelection("text", true, 2)],
                    [
                        new OrderedSpecInstance("s1", "text", 1, null, new SpecContentSource(SpecContentMode.RawText, "## Key Principle\nVesicles fuse with SNAREs.")),
                        new OrderedSpecInstance("s2", "callout", 1, "clinical", new SpecContentSource(SpecContentMode.MarkdownFile, null, "notes/intro.md"))
                    ])
            ]
        };

        var saveRes = await draftStore.SaveAsync(ws, updated);
        Assert.True(saveRes.IsSuccess);
        Assert.True(File.Exists(Path.Combine(ws, ".meloberry", "pc-draft.json")));

        var reloaded = await draftStore.LoadAsync(ws);
        Assert.Equal("Neuroscience Ch 3", reloaded.Title);
        Assert.Equal(WorkflowMode.LiveAgent, reloaded.WorkflowMode);
        Assert.True(reloaded.AllowPlaceholdersOnFinalize);
        Assert.Equal(SpecContentMode.RawText, reloaded.Components![0].OrderedItems![0].Content!.Mode);
        Assert.Equal("## Key Principle\nVesicles fuse with SNAREs.", reloaded.Components![0].OrderedItems![0].Content!.RawText);
        Assert.Equal(SpecContentMode.MarkdownFile, reloaded.Components![0].OrderedItems![1].Content!.Mode);
        Assert.Equal("notes/intro.md", reloaded.Components![0].OrderedItems![1].Content!.MarkdownFilePath);

        // Concurrent writes remain valid JSON
        var tasks = Enumerable.Range(0, 8)
            .Select(i => draftStore.SaveAsync(ws, reloaded with { Description = $"Pass {i}" }));
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.True(r.IsSuccess));
        var finalLoad = await draftStore.LoadAsync(ws);
        Assert.StartsWith("Pass ", finalLoad.Description);
    }

    [Fact]
    public async Task RuntimeIpcRegistry_WritesValidatesPid_AndCleansUpOwnPidOnly()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "ipc_data"));
        var registry = new RuntimeIpcRegistry(paths);

        await registry.WriteCurrentAsync(50123, 50124, "tok_abc");
        var active = registry.TryReadActive();
        Assert.NotNull(active);
        Assert.Equal(Environment.ProcessId, active!.Pid);
        Assert.Equal(50123, active.ShellPort);
        Assert.Equal(50124, active.ViewerPort);
        Assert.Equal("tok_abc", active.Token);

        registry.CleanupCurrent();
        Assert.False(File.Exists(paths.RuntimeIpcFilePath));
        Assert.Null(registry.TryReadActive());

        Directory.CreateDirectory(paths.BaseDirectory);
        var staleJson = JsonSerializer.Serialize(new RuntimeIpcInfo(9999999, 51000, 51001, "stale", DateTimeOffset.UtcNow));
        await File.WriteAllTextAsync(paths.RuntimeIpcFilePath, staleJson);
        Assert.Null(registry.TryReadActive());

        registry.CleanupCurrent();
        Assert.True(File.Exists(paths.RuntimeIpcFilePath));
    }

    [Fact]
    public async Task LiveDraftHandler_MutatesComponentsSpecsAndContent_AndEmitsTargetIds()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "live_handler"));
        var ws = Path.Combine(_tempRoot, "ws_live");
        var outsideFile = Path.Combine(_tempRoot, "outside.md");
        var validMd = Path.Combine(ws, "specs", "q1.md");
        Directory.CreateDirectory(Path.GetDirectoryName(validMd)!);
        await File.WriteAllTextAsync(outsideFile, "# Outside");
        await File.WriteAllTextAsync(validMd, "Question prompt\n- [x] A\n- [ ] B");

        var settingsStore = new JsonSettingsStore(paths);
        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true)));

        var draftStore = new WorkspaceDraftStore();
        var handler = new LiveDraftHandler(draftStore, settingsStore);

        var events = new List<LiveDraftChangedEvent>();
        handler.DraftChanged += evt => events.Add(evt);

        var patchRes = await handler.MutateAsync(ws, new LiveDraftMutationRequest(
            Op: "patch-meta",
            Title: "Live Cardiology",
            WorkflowMode: WorkflowMode.LiveAgent));
        Assert.True(patchRes.IsSuccess);
        Assert.Equal("Live Cardiology", patchRes.Value.Draft.Title);
        Assert.Equal(WorkflowMode.LiveAgent, patchRes.Value.Draft.WorkflowMode);

        var addCompRes = await handler.MutateAsync(ws, new LiveDraftMutationRequest(
            Op: "add-comp",
            ComponentKind: ComponentKind.Quiz,
            Title: "ECG Check"));
        Assert.True(addCompRes.IsSuccess);
        var quizCompId = addCompRes.Value.TargetComponentId!;
        Assert.False(string.IsNullOrWhiteSpace(quizCompId));

        var addSpecRes = await handler.MutateAsync(ws, new LiveDraftMutationRequest(
            Op: "add-spec",
            TargetId: quizCompId,
            Topic: "mcq",
            Subtype: "multi",
            Count: 1));
        Assert.True(addSpecRes.IsSuccess);
        var specId = addSpecRes.Value.TargetSpecId!;
        Assert.False(string.IsNullOrWhiteSpace(specId));

        var badContent = await handler.MutateAsync(ws, new LiveDraftMutationRequest(
            Op: "set-content",
            TargetId: specId,
            ContentMode: SpecContentMode.MarkdownFile,
            MarkdownFilePath: "../outside.md"));
        Assert.True(badContent.IsFailure);

        var goodContent = await handler.MutateAsync(ws, new LiveDraftMutationRequest(
            Op: "set-content",
            TargetId: specId,
            ContentMode: SpecContentMode.MarkdownFile,
            MarkdownFilePath: "specs/q1.md"));
        Assert.True(goodContent.IsSuccess);

        var quizComp = goodContent.Value.Draft.Components!.First(c => c.Id == quizCompId);
        var inst = quizComp.OrderedItems!.First(s => s.Id == specId);
        Assert.Equal(SpecContentMode.MarkdownFile, inst.Content!.Mode);
        Assert.Equal("specs/q1.md", inst.Content.MarkdownFilePath);

        Assert.Contains(events, e => e.TargetComponentId == quizCompId && e.TargetSpecId == specId && e.Source == "agent");

        var addSpec2 = await handler.MutateAsync(ws, new LiveDraftMutationRequest(
            Op: "add-spec",
            TargetId: quizCompId,
            Topic: "flashcard",
            Count: 1));
        var specId2 = addSpec2.Value.TargetSpecId!;
        var moveRes = await handler.MutateAsync(ws, new LiveDraftMutationRequest(
            Op: "move-spec",
            TargetId: specId2,
            Index: 0));
        Assert.True(moveRes.IsSuccess);
        Assert.Equal(specId2, moveRes.Value.Draft.Components!.First(c => c.Id == quizCompId).OrderedItems![0].Id);

        var rmRes = await handler.MutateAsync(ws, new LiveDraftMutationRequest(Op: "rm-comp", TargetId: quizCompId));
        Assert.True(rmRes.IsSuccess);
        Assert.DoesNotContain(rmRes.Value.Draft.Components!, c => c.Id == quizCompId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
