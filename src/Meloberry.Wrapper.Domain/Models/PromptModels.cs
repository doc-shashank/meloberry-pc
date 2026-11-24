namespace Meloberry.Wrapper.Domain.Models;

/// <summary>Target Meloberry container chosen in Prompt Studio.</summary>
public enum ContainerKind
{
    Session,
    StandaloneArticle,
    StandaloneQuiz
}

/// <summary>Component type inside a session organization blueprint.</summary>
public enum ComponentKind
{
    Article,
    Quiz
}

/// <summary>Recommended file authoring format for the AI agent.</summary>
public enum AuthoringFormat
{
    MarkdownDirectory,
    SingleMarkdown,
    Json
}

/// <summary>Single shopping-list item selected by the user for an article or quiz.</summary>
public sealed record ShoppingListSelection(
    string Topic,
    bool Enabled,
    int? DesiredCount = null,
    string? Notes = null,
    IReadOnlyList<string>? Modes = null);

/// <summary>Single ordered block/question instance inside a component's Organizer track.</summary>
public sealed record OrderedSpecInstance(
    string Id,
    string Topic,
    int InstanceNumber);

/// <summary>Ordered article or quiz component inside a Prompt Studio blueprint.</summary>
public sealed record PromptComponentPlan(
    string Id,
    ComponentKind Kind,
    string Title,
    IReadOnlyList<ShoppingListSelection> Selections,
    IReadOnlyList<OrderedSpecInstance>? OrderedItems = null);

/// <summary>Source text or file attachment passed to the agent prompt.</summary>
public sealed record SourceAttachment(
    string FilePath,
    string Kind);

/// <summary>Complete user configuration from Prompt Studio used to compose prompts and AGENTS.md.</summary>
public sealed record PromptDraft(
    ContainerKind Container,
    string Title,
    string Description,
    AuthoringFormat AuthoringFormat,
    string SourceText,
    IReadOnlyList<ShoppingListSelection> Selections,
    IReadOnlyList<SourceAttachment> Attachments,
    bool UseMelomgForPdfs = true,
    bool EmbedInlineSchemas = false,
    bool LazyCliDiscovery = true,
    bool AgentsMdExported = false,
    string? WorkingDirectory = null,
    string? MbyExecutablePath = null,
    string? MelomgExecutablePath = null,
    IReadOnlyList<string>? CopiedFolders = null,
    IReadOnlyList<PromptComponentPlan>? Components = null);

/// <summary>Rendered Markdown prompt plus token/length statistics.</summary>
public sealed record ComposedPrompt(
    string Markdown,
    int CharacterCount,
    int ApproximateTokens);

/// <summary>Reusable shopping-list &amp; organization preset.</summary>
public sealed record PromptPreset(
    string Id,
    string Name,
    string Description,
    ContainerKind Container,
    AuthoringFormat AuthoringFormat,
    IReadOnlyList<ShoppingListSelection> Selections,
    bool IsBuiltIn = false,
    IReadOnlyList<PromptComponentPlan>? Components = null);

/// <summary>Built-in study prompt presets matching common Meloberry workflows.</summary>
public static class BuiltInPresets
{
    public static IReadOnlyList<PromptPreset> All { get; } =
    [
        new(
            Id: "full-chapter-session",
            Name: "Full Chapter Study Session",
            Description: "Two structured teaching articles followed by an active-recall quiz.",
            Container: ContainerKind.Session,
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            Selections:
            [
                new("text", true, 2),
                new("image", true, 1),
                new("callout", true, 2, null, ["pearl", "clinical"]),
                new("table", true, 1, null, ["hideableCols"]),
                new("mcq", true, 5, null, ["mixed"]),
                new("flashcard", true, 5)
            ],
            IsBuiltIn: true,
            Components:
            [
                new(
                    "a1",
                    ComponentKind.Article,
                    "Core Concepts",
                    [
                        new("text", true, 2, "Break down mechanisms cleanly with headings & KaTeX"),
                        new("image", true, 1, "Embed primary figure with caption"),
                        new("callout", true, 2, "Highlight pearl & clinical takeaways", ["pearl", "clinical"]),
                        new("table", true, 1, "Hideable comparison table", ["hideableCols"])
                    ]),
                new(
                    "q1",
                    ComponentKind.Quiz,
                    "Mastery Check",
                    [
                        new("mcq", true, 5, "Clinical vignettes with explanations", ["mixed"]),
                        new("flashcard", true, 5, "High-yield recall cards", ["text"])
                    ])
            ]),
        new(
            Id: "high-yield-article",
            Name: "High-Yield Revision Article",
            Description: "Standalone deep-dive article with hover link cards, callouts, and hideable tables.",
            Container: ContainerKind.StandaloneArticle,
            AuthoringFormat: AuthoringFormat.SingleMarkdown,
            Selections:
            [
                new("text", true, 2, "Concise high-yield prose with clear hierarchy"),
                new("callout", true, 3, "Common pitfalls and pearls", ["pearl", "warning"]),
                new("table", true, 2, "Hideable summary tables", ["hideableCols", "stickyFirstCol"]),
                new("link", true, 3, "Hover cards for quick definitions via mby://card/")
            ],
            IsBuiltIn: true,
            Components:
            [
                new(
                    "a1",
                    ComponentKind.Article,
                    "High-Yield Review",
                    [
                        new("text", true, 2, "Concise high-yield prose with clear hierarchy"),
                        new("callout", true, 3, "Common pitfalls and pearls", ["pearl", "warning"]),
                        new("table", true, 2, "Hideable summary tables", ["hideableCols", "stickyFirstCol"]),
                        new("link", true, 3, "Hover cards for quick definitions via mby://card/")
                    ])
            ]),
        new(
            Id: "active-recall-quiz",
            Name: "Active-Recall Mixed Quiz",
            Description: "Standalone mixed quiz combining MCQs, flashcards, fill-in-the-blank, and assertion-reason.",
            Container: ContainerKind.StandaloneQuiz,
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            Selections:
            [
                new("mcq", true, 6, null, ["mixed"]),
                new("flashcard", true, 5, null, ["text"]),
                new("fill-blank", true, 4, null, ["fuzzy"]),
                new("assertion-reason", true, 3, null, ["4opt"]),
                new("spot-error", true, 3, null, ["requireFix"])
            ],
            IsBuiltIn: true,
            Components:
            [
                new(
                    "q1",
                    ComponentKind.Quiz,
                    "Active Recall Quiz",
                    [
                        new("mcq", true, 6, "Single & multi-correct items", ["mixed"]),
                        new("flashcard", true, 5, null, ["text"]),
                        new("fill-blank", true, 4, null, ["fuzzy"]),
                        new("assertion-reason", true, 3, null, ["4opt"]),
                        new("spot-error", true, 3, null, ["requireFix"])
                    ])
            ]),
        new(
            Id: "diagram-relabel-drill",
            Name: "Diagram Relabeling Drill",
            Description: "PDF diagram extraction + label masking paired with relabeling drills and visual flashcards.",
            Container: ContainerKind.Session,
            AuthoringFormat: AuthoringFormat.Json,
            Selections:
            [
                new("image", true, 2),
                new("relabel", true, 4, null, ["recall", "solid"]),
                new("flashcard", true, 5, null, ["diagram"])
            ],
            IsBuiltIn: true,
            Components:
            [
                new(
                    "a1",
                    ComponentKind.Article,
                    "Annotated Diagrams",
                    [
                        new("text", true, 1, "Brief orientation to figure landmarks"),
                        new("image", true, 2, "Unmasked annotated reference diagrams")
                    ]),
                new(
                    "q1",
                    ComponentKind.Quiz,
                    "Labeling & Visual Recall",
                    [
                        new("relabel", true, 4, "Pin labels onto masked figures", ["recall", "solid"]),
                        new("flashcard", true, 5, "Front: masked diagram via melomg; Back: unmasked answer", ["diagram"])
                    ])
            ])
    ];
}
