using System.Text.Json;
using Meloberry.Wrapper.Application;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Domain.Services;
using Meloberry.Wrapper.Infrastructure;
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
            UseMelomgForPdfs: true);

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
        using var sessionDoc = JsonDocument.Parse(sessionResult.Value.RawJson);
        var comps = sessionDoc.RootElement.GetProperty("components");
        Assert.Equal(2, comps.GetArrayLength());
        var blocks = comps[0].GetProperty("blocks");
        Assert.Equal(3, blocks.GetArrayLength());
        Assert.Equal("table", blocks[0].GetProperty("kind").GetString());
        Assert.Equal("text", blocks[1].GetProperty("kind").GetString());
        Assert.Equal("callout", blocks[2].GetProperty("kind").GetString());
        Assert.Equal("warning", blocks[2].GetProperty("tone").GetString());
        Assert.Equal("assertion-reason", comps[1].GetProperty("questions")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task SessionTemplateHandler_AppliesGeneratedJsonWithLiveMbyCli()
    {
        var liveMby = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "..", "CLI", "app", "mby-cli-2.0.0.exe"));
        Assert.True(File.Exists(liveMby), $"Expected live CLI at {liveMby}");

        var wsDir = Path.Combine(_tempRoot, "ws");
        Directory.CreateDirectory(wsDir);
        File.Copy(liveMby, Path.Combine(wsDir, "mby-cli-2.0.0.exe"), overwrite: true);

        var services = new ServiceCollection();
        services.AddSingleton<IAppShutdownCoordinator>(new DummyShutdown());
        services.AddSingleton<INativeDialogService>(new DummyDialogs());
        services.AddSingleton<IExternalUrlLauncher>(new DummyLauncher());
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

    private sealed class DummyShutdown : IAppShutdownCoordinator
    {
        public void RequestShutdown() { }
    }

    private sealed class DummyDialogs : INativeDialogService
    {
        public Task<string?> PickFolderAsync(string title, string? initialDir = null, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> PickFilesAsync(string title, string filterLabel, string filterPattern, bool allowMultiple = true, string? initialDir = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> SaveFileAsync(string title, string defaultFileName, string filterLabel, string filterExtension, string? initialDir = null, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class DummyLauncher : IExternalUrlLauncher
    {
        public Result OpenAllowlistedUrl(string url) => Result.Success();
        public Result OpenFolder(string folderPath) => Result.Success();
    }
}
