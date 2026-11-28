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

    [Fact]
    public async Task LiveDraftHandler_SetContent_SupportsImageFile_AndRejectsNonImagesOrTraversal()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "live_img_data"));
        var ws = Path.Combine(_tempRoot, "live_img_ws");
        var assetsDir = Path.Combine(ws, "assets");
        Directory.CreateDirectory(assetsDir);

        var validImg = Path.Combine(assetsDir, "diagram.png");
        var notImg = Path.Combine(ws, "notes.md");
        var outsideImg = Path.Combine(_tempRoot, "escaped.png");
        await File.WriteAllBytesAsync(validImg, [0x89, 0x50, 0x4E, 0x47]);
        await File.WriteAllTextAsync(notImg, "# Not an image");
        await File.WriteAllBytesAsync(outsideImg, [0x89, 0x50, 0x4E, 0x47]);

        var settingsStore = new JsonSettingsStore(paths);
        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true)));
        var draftStore = new WorkspaceDraftStore();
        var handler = new LiveDraftHandler(draftStore, settingsStore);

        var addComp = await handler.MutateAsync(ws, new LiveDraftMutationRequest(Op: "add-comp", ComponentKind: ComponentKind.Article, Title: "Visuals"));
        Assert.True(addComp.IsSuccess);
        var compId = addComp.Value.TargetComponentId!;

        var addSpec = await handler.MutateAsync(ws, new LiveDraftMutationRequest(Op: "add-spec", TargetId: compId, Topic: "image", Count: 1));
        Assert.True(addSpec.IsSuccess);
        var specId = addSpec.Value.TargetSpecId!;

        Assert.Equal("Security.InvalidPath", (await handler.MutateAsync(ws, new LiveDraftMutationRequest(Op: "set-content", TargetId: specId, ContentMode: SpecContentMode.ImageFile, ImagePath: "../escaped.png"))).Error!.Code);
        Assert.Equal("Security.InvalidPath", (await handler.MutateAsync(ws, new LiveDraftMutationRequest(Op: "set-content", TargetId: specId, ContentMode: SpecContentMode.ImageFile, ImagePath: "notes.md"))).Error!.Code);

        var okRes = await handler.MutateAsync(ws, new LiveDraftMutationRequest(
            Op: "set-content",
            TargetId: specId,
            ContentMode: SpecContentMode.ImageFile,
            ImagePath: "assets/diagram.png",
            Caption: "Figure 1: Diagram",
            AltText: "Anatomical diagram"));
        Assert.True(okRes.IsSuccess, okRes.Error?.Description);

        var reloaded = await draftStore.LoadAsync(ws);
        var inst = reloaded.Components!.First(c => c.Id == compId).OrderedItems!.First(s => s.Id == specId);
        Assert.Equal(SpecContentMode.ImageFile, inst.Content!.Mode);
        Assert.Equal("assets/diagram.png", inst.Content.ImagePath);
        Assert.Equal("Figure 1: Diagram", inst.Content.Caption);
        Assert.Equal("Anatomical diagram", inst.Content.AltText);
    }

    [Fact]
    public async Task LiveDraftChangedEvent_CarriesOperationNameForAllMutations()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "ops_p6"));
        var ws = Path.Combine(_tempRoot, "ws_ops");
        Directory.CreateDirectory(ws);

        var settingsStore = new JsonSettingsStore(paths);
        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true)));

        var handler = new LiveDraftHandler(new WorkspaceDraftStore(), settingsStore);
        var observedOps = new List<string?>();
        handler.DraftChanged += evt => observedOps.Add(evt.Op);

        await handler.MutateAsync(ws, new LiveDraftMutationRequest("patch-meta", Title: "Ops Test"));
        var addComp = await handler.MutateAsync(ws, new LiveDraftMutationRequest("add-comp", ComponentKind: ComponentKind.Article, Title: "A"));
        var addSpec = await handler.MutateAsync(ws, new LiveDraftMutationRequest("add-spec", TargetId: addComp.Value.TargetComponentId, Topic: "callout", Subtype: "pearl"));
        await handler.MutateAsync(ws, new LiveDraftMutationRequest("update-spec", TargetId: addSpec.Value.TargetSpecId, Subtype: "clinical", Notes: "Updated note"));
        await handler.MutateAsync(ws, new LiveDraftMutationRequest("set-content", TargetId: addSpec.Value.TargetSpecId, ContentMode: SpecContentMode.RawText, RawText: "Body"));

        Assert.Equal(["patch-meta", "add-comp", "add-spec", "update-spec", "set-content"], observedOps);
    }

    [Fact]
    public async Task LiveDraftHandler_GetDraftAsync_PreservesPresetSubtypesOnFreshWorkspace()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "data_subtypes"));
        var ws = Path.Combine(_tempRoot, "ws_fresh");
        Directory.CreateDirectory(ws);

        var settingsStore = new JsonSettingsStore(paths);
        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true)));

        var draftStore = new WorkspaceDraftStore();
        var handler = new LiveDraftHandler(draftStore, settingsStore);

        var res = await handler.GetDraftAsync(ws);
        Assert.True(res.IsSuccess);

        var article = res.Value.Draft.Components!.Single(c => c.Id == "a1");
        var quiz = res.Value.Draft.Components!.Single(c => c.Id == "q1");

        var callouts = article.OrderedItems!.Where(i => i.Topic == "callout").Select(i => i.Subtype).ToList();
        Assert.Equal(["pearl", "clinical"], callouts);

        var tables = article.OrderedItems!.Where(i => i.Topic == "table").Select(i => i.Subtype).ToList();
        Assert.Equal(["hideableCols"], tables);

        var mcqs = quiz.OrderedItems!.Where(i => i.Topic == "mcq").Select(i => i.Subtype).ToList();
        Assert.Equal(5, mcqs.Count);
        Assert.All(mcqs, s => Assert.Equal("mixed", s));

        var flashcards = quiz.OrderedItems!.Where(i => i.Topic == "flashcard").Select(i => i.Subtype).ToList();
        Assert.Equal(5, flashcards.Count);
        Assert.All(flashcards, s => Assert.Equal("text", s));
    }

    [Fact]
    public async Task LiveDraftHandler_SynthesizesFromEmptyOrderedItems_AndClearsStaleModeCounts()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "data_empty_ordered"));
        var ws = Path.Combine(_tempRoot, "ws_empty_ordered");
        Directory.CreateDirectory(ws);

        var settingsStore = new JsonSettingsStore(paths);
        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true)));
        var draftStore = new WorkspaceDraftStore();
        await draftStore.SaveAsync(ws, new PromptDraft(
            Container: ContainerKind.StandaloneArticle,
            Title: "Empty Ordered",
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
                    "Article",
                    [new("callout", true, 1, ModeCounts: new Dictionary<string, int> { ["pearl"] = 1 })],
                    [])
            ]));

        var handler = new LiveDraftHandler(draftStore, settingsStore);
        var loaded = await handler.GetDraftAsync(ws);
        Assert.True(loaded.IsSuccess);
        var spec = Assert.Single(loaded.Value.Draft.Components!.Single().OrderedItems!);
        Assert.Equal("pearl", spec.Subtype);

        var cleared = await handler.MutateAsync(ws, new LiveDraftMutationRequest(
            Op: "update-spec",
            TargetId: spec.Id,
            Subtype: ""));
        Assert.True(cleared.IsSuccess);
        var updatedComp = cleared.Value.Draft.Components!.Single();
        Assert.Null(updatedComp.OrderedItems!.Single().Subtype);
        Assert.Null(updatedComp.Selections.Single(s => s.Topic == "callout").ModeCounts);
    }

    [Fact]
    public async Task LiveDraftHandler_PatchMeta_TracksWorkflowModeExplicitlyChanged()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "data_wf"));
        var ws = Path.Combine(_tempRoot, "ws_wf");
        Directory.CreateDirectory(ws);

        var settingsStore = new JsonSettingsStore(paths);
        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true)));

        var draftStore = new WorkspaceDraftStore();
        var handler = new LiveDraftHandler(draftStore, settingsStore);

        var events = new List<LiveDraftChangedEvent>();
        handler.DraftChanged += evt => events.Add(evt);

        var titlePatch = await handler.MutateAsync(ws, new LiveDraftMutationRequest(
            Op: "patch-meta",
            Title: "New Title Only"));
        Assert.True(titlePatch.IsSuccess);
        Assert.Single(events);
        Assert.False(events[0].WorkflowModeExplicitlyChanged);

        var modePatch = await handler.MutateAsync(ws, new LiveDraftMutationRequest(
            Op: "patch-meta",
            WorkflowMode: WorkflowMode.ClassicPrompt));
        Assert.True(modePatch.IsSuccess);
        Assert.Equal(2, events.Count);
        Assert.True(events[1].WorkflowModeExplicitlyChanged);
    }

    [Fact]
    public async Task LiveDraftHandler_EmitsHumanFriendlySummaries_WithoutRawIdsOrEnums()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "live_summaries"));
        var ws = Path.Combine(_tempRoot, "ws_summaries");
        var mdPath = Path.Combine(ws, "notes", "intro.md");
        var imgPath = Path.Combine(ws, "assets", "diagram.png");
        Directory.CreateDirectory(Path.GetDirectoryName(mdPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(imgPath)!);
        await File.WriteAllTextAsync(mdPath, "# Intro");
        await File.WriteAllBytesAsync(imgPath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var settingsStore = new JsonSettingsStore(paths);
        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true)));
        var draftStore = new WorkspaceDraftStore();
        var handler = new LiveDraftHandler(draftStore, settingsStore);

        var patchMeta = await handler.MutateAsync(ws, new LiveDraftMutationRequest("patch-meta", Title: "Cardiology", Description: "Ch 1"));
        Assert.True(patchMeta.IsSuccess);
        Assert.Contains("title \"Cardiology\"", patchMeta.Value.Summary);
        Assert.Contains("description", patchMeta.Value.Summary);

        var addComp = await handler.MutateAsync(ws, new LiveDraftMutationRequest("add-comp", ComponentKind: ComponentKind.Quiz, Title: "Drill 1"));
        Assert.True(addComp.IsSuccess);
        var compId = addComp.Value.TargetComponentId!;

        var renameComp = await handler.MutateAsync(ws, new LiveDraftMutationRequest("patch-meta", TargetId: compId, Title: "Final Quiz"));
        Assert.True(renameComp.IsSuccess);
        Assert.Equal("Renamed \"Drill 1\" to \"Final Quiz\"", renameComp.Value.Summary);
        Assert.DoesNotContain(compId, renameComp.Value.Summary);

        var moveComp = await handler.MutateAsync(ws, new LiveDraftMutationRequest("move-comp", TargetId: compId, Index: 0));
        Assert.True(moveComp.IsSuccess);
        Assert.Equal("Moved \"Final Quiz\" to position 1", moveComp.Value.Summary);
        Assert.DoesNotContain(compId, moveComp.Value.Summary);
        Assert.DoesNotContain("index 0", moveComp.Value.Summary);

        var addOne = await handler.MutateAsync(ws, new LiveDraftMutationRequest("add-spec", TargetId: compId, Topic: "mcq", Count: 1));
        Assert.True(addOne.IsSuccess);
        Assert.Equal("Added mcq spec to \"Final Quiz\"", addOne.Value.Summary);
        Assert.DoesNotContain("spec(s)", addOne.Value.Summary);

        var addTwo = await handler.MutateAsync(ws, new LiveDraftMutationRequest("add-spec", TargetId: compId, Topic: "flashcard", Count: 2));
        Assert.True(addTwo.IsSuccess);
        Assert.Equal("Added 2 flashcard specs to \"Final Quiz\"", addTwo.Value.Summary);

        var specId = addOne.Value.TargetSpecId!;
        var updateSpec = await handler.MutateAsync(ws, new LiveDraftMutationRequest("update-spec", TargetId: specId, Subtype: "multi"));
        Assert.True(updateSpec.IsSuccess);
        Assert.Equal("Updated mcq (subtype -> multi) in \"Final Quiz\"", updateSpec.Value.Summary);

        var setRaw = await handler.MutateAsync(ws, new LiveDraftMutationRequest("set-content", TargetId: specId, ContentMode: SpecContentMode.RawText, RawText: "Stem?\n- [x] A\n- [ ] B"));
        Assert.True(setRaw.IsSuccess);
        Assert.Equal("Filled mcq text in \"Final Quiz\"", setRaw.Value.Summary);
        Assert.DoesNotContain("RawText", setRaw.Value.Summary);

        var setMd = await handler.MutateAsync(ws, new LiveDraftMutationRequest("set-content", TargetId: specId, ContentMode: SpecContentMode.MarkdownFile, MarkdownFilePath: "notes/intro.md"));
        Assert.True(setMd.IsSuccess);
        Assert.Equal("Attached intro.md to mcq", setMd.Value.Summary);
        Assert.DoesNotContain("MarkdownFile", setMd.Value.Summary);

        var setImg = await handler.MutateAsync(ws, new LiveDraftMutationRequest("set-content", TargetId: specId, ContentMode: SpecContentMode.ImageFile, ImagePath: "assets/diagram.png", RawText: "Stem?\n- [x] A\n- [ ] B"));
        Assert.True(setImg.IsSuccess);
        Assert.Equal("Attached diagram.png to mcq", setImg.Value.Summary);
        Assert.DoesNotContain("ImageFile", setImg.Value.Summary);

        var clearContent = await handler.MutateAsync(ws, new LiveDraftMutationRequest("set-content", TargetId: specId, ContentMode: SpecContentMode.None));
        Assert.True(clearContent.IsSuccess);
        Assert.Equal("Cleared mcq content", clearContent.Value.Summary);
        Assert.DoesNotContain("None", clearContent.Value.Summary);

        var rmComp = await handler.MutateAsync(ws, new LiveDraftMutationRequest("rm-comp", TargetId: compId));
        Assert.True(rmComp.IsSuccess);
        Assert.Equal("Removed Quiz \"Final Quiz\"", rmComp.Value.Summary);
        Assert.DoesNotContain(compId, rmComp.Value.Summary);
    }

    [Fact]
    public async Task LiveDraftHandler_PatchMeta_FormatsContainerAndWorkflowModeHumanReadably()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "meta_fmt"));
        var ws = Path.Combine(_tempRoot, "ws_meta_fmt");
        Directory.CreateDirectory(ws);

        var settingsStore = new JsonSettingsStore(paths);
        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true)));
        var handler = new LiveDraftHandler(new WorkspaceDraftStore(), settingsStore);

        var res1 = await handler.MutateAsync(ws, new LiveDraftMutationRequest(
            "patch-meta",
            Container: ContainerKind.StandaloneArticle,
            WorkflowMode: WorkflowMode.LiveAgent));
        Assert.True(res1.IsSuccess);
        Assert.Contains("Standalone Article", res1.Value.Summary);
        Assert.Contains("Live Agent", res1.Value.Summary);
        Assert.DoesNotContain("StandaloneArticle", res1.Value.Summary);
        Assert.DoesNotContain("LiveAgent", res1.Value.Summary);

        var res2 = await handler.MutateAsync(ws, new LiveDraftMutationRequest(
            "patch-meta",
            Container: ContainerKind.StandaloneQuiz,
            WorkflowMode: WorkflowMode.ClassicPrompt));
        Assert.True(res2.IsSuccess);
        Assert.Contains("Standalone Quiz", res2.Value.Summary);
        Assert.Contains("Classic Prompt", res2.Value.Summary);
        Assert.DoesNotContain("StandaloneQuiz", res2.Value.Summary);
        Assert.DoesNotContain("ClassicPrompt", res2.Value.Summary);
    }

    [Fact]
    public async Task LiveDraftHandler_CloneSpecAndMoveSpecTo_HandlesSameAndCrossContainerMovesAndKindMismatch()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "clone_move"));
        var ws = Path.Combine(_tempRoot, "ws_clone_move");
        Directory.CreateDirectory(ws);

        var settingsStore = new JsonSettingsStore(paths);
        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true)));
        var handler = new LiveDraftHandler(new WorkspaceDraftStore(), settingsStore);

        var a1 = (await handler.MutateAsync(ws, new LiveDraftMutationRequest("add-comp", ComponentKind: ComponentKind.Article, Title: "Article 1"))).Value.TargetComponentId!;
        var a2 = (await handler.MutateAsync(ws, new LiveDraftMutationRequest("add-comp", ComponentKind: ComponentKind.Article, Title: "Article 2"))).Value.TargetComponentId!;
        var q1 = (await handler.MutateAsync(ws, new LiveDraftMutationRequest("add-comp", ComponentKind: ComponentKind.Quiz, Title: "Quiz 1"))).Value.TargetComponentId!;

        var specRes = await handler.MutateAsync(ws, new LiveDraftMutationRequest(
            "add-spec",
            TargetId: a1,
            Topic: "callout",
            Subtype: "clinical",
            Count: 1));
        Assert.True(specRes.IsSuccess);
        var specId = specRes.Value.TargetSpecId!;

        var setContent = await handler.MutateAsync(ws, new LiveDraftMutationRequest(
            "set-content",
            TargetId: specId,
            ContentMode: SpecContentMode.RawText,
            RawText: "Important pearl"));
        Assert.True(setContent.IsSuccess);

        var cloneRes = await handler.MutateAsync(ws, new LiveDraftMutationRequest("clone-spec", TargetId: specId));
        Assert.True(cloneRes.IsSuccess, cloneRes.Error?.Description);
        var clonedId = cloneRes.Value.TargetSpecId!;
        Assert.NotEqual(specId, clonedId);

        var a1AfterClone = cloneRes.Value.Draft.Components!.Single(c => c.Id == a1);
        Assert.Equal(2, a1AfterClone.OrderedItems!.Count);
        Assert.Equal("Important pearl", a1AfterClone.OrderedItems[1].Content?.RawText);

        var moveRes = await handler.MutateAsync(ws, new LiveDraftMutationRequest(
            "move-spec-to",
            TargetId: clonedId,
            TargetContainerId: a2,
            Index: 0));
        Assert.True(moveRes.IsSuccess, moveRes.Error?.Description);
        Assert.Single(moveRes.Value.Draft.Components!.Single(c => c.Id == a1).OrderedItems!);
        Assert.Equal(clonedId, Assert.Single(moveRes.Value.Draft.Components!.Single(c => c.Id == a2).OrderedItems!).Id);

        var crossKind = await handler.MutateAsync(ws, new LiveDraftMutationRequest(
            "move-spec-to",
            TargetId: specId,
            TargetContainerId: q1));
        Assert.True(crossKind.IsFailure);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
