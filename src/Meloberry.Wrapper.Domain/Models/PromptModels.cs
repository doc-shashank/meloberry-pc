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

/// <summary>Scope stored inside a `.mbypreset` JSON file.</summary>
public enum PresetFileScope
{
    Workflow,
    Component
}

/// <summary>Step-wide workflow mode determining whether Prompt generation is used.</summary>
public enum WorkflowMode
{
    Direct,
    LiveAgent,
    ClassicPrompt
}

/// <summary>Per-spec direct authoring content source.</summary>
public enum SpecContentMode
{
    None,
    RawText,
    MarkdownFile,
    ImageFile
}

/// <summary>Direct content payload attached to an ordered spec instance.</summary>
public sealed record SpecContentSource(
    SpecContentMode Mode = SpecContentMode.None,
    string? RawText = null,
    string? MarkdownFilePath = null,
    string? ImagePath = null,
    string? Caption = null,
    string? AltText = null);

/// <summary>Single shopping-list item selected by the user for an article or quiz.</summary>
public sealed record ShoppingListSelection(
    string Topic,
    bool Enabled,
    int? DesiredCount = null,
    string? Notes = null,
    IReadOnlyList<string>? Modes = null,
    IReadOnlyDictionary<string, int>? ModeCounts = null);

/// <summary>Single ordered block/question instance inside a component's Organizer track.</summary>
public sealed record OrderedSpecInstance(
    string Id,
    string Topic,
    int InstanceNumber,
    string? Subtype = null,
    SpecContentSource? Content = null);

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
    IReadOnlyList<PromptComponentPlan>? Components = null,
    WorkflowMode WorkflowMode = WorkflowMode.Direct,
    bool AllowPlaceholdersOnFinalize = false);

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

/// <summary>Portable `.mbypreset` file payload supporting workflow or single-component presets.</summary>
public sealed record MbyPresetFile(
    int FormatVersion,
    PresetFileScope Scope,
    string Name,
    string? Description = null,
    PromptPreset? WorkflowPreset = null,
    PromptComponentPlan? ComponentPreset = null);

/// <summary>Built-in study prompt presets (3 per container: Session, Article, Quiz).</summary>
public static class BuiltInPresets
{
    public static IReadOnlyList<PromptPreset> All { get; } =
    [
        // Session (3)
        new(
            Id: "full-chapter-session",
            Name: "Full Chapter Study Session",
            Description: "Structured teaching article followed by an active-recall quiz.",
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
            ]),
        new(
            Id: "clinical-case-session",
            Name: "Clinical Case & Rapid Recall",
            Description: "Differential summary article paired with clinical reasoning and assertion-reason drills.",
            Container: ContainerKind.Session,
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            Selections:
            [
                new("text", true, 1),
                new("callout", true, 2, null, ["clinical", "warning"]),
                new("table", true, 1, null, ["hideableCols"]),
                new("mcq", true, 4, null, ["single"]),
                new("assertion-reason", true, 3, null, ["4opt"]),
                new("diff", true, 2, null, ["fill"])
            ],
            IsBuiltIn: true,
            Components:
            [
                new(
                    "a1",
                    ComponentKind.Article,
                    "Clinical Overview",
                    [
                        new("text", true, 1, "Concise pathophysiology & diagnostic approach"),
                        new("callout", true, 2, "Key clinical signs and red flags", ["clinical", "warning"]),
                        new("table", true, 1, "Differential comparison", ["hideableCols"])
                    ]),
                new(
                    "q1",
                    ComponentKind.Quiz,
                    "Reasoning & Differentiation",
                    [
                        new("mcq", true, 4, "Single-best-answer clinical stems", ["single"]),
                        new("assertion-reason", true, 3, "Cause-and-effect reasoning", ["4opt"]),
                        new("diff", true, 2, "Compare key presentations", ["fill"])
                    ])
            ]),

        // StandaloneArticle (3)
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
            Id: "visual-atlas-article",
            Name: "Visual Atlas & Annotated Figures",
            Description: "Figure-centric standalone article combining extracted diagrams, captions, and hover notes.",
            Container: ContainerKind.StandaloneArticle,
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            Selections:
            [
                new("text", true, 2),
                new("image", true, 3),
                new("callout", true, 2, null, ["pearl"]),
                new("link", true, 2)
            ],
            IsBuiltIn: true,
            Components:
            [
                new(
                    "a1",
                    ComponentKind.Article,
                    "Visual Atlas",
                    [
                        new("text", true, 2, "Brief structural landmarks and relationships"),
                        new("image", true, 3, "High-resolution annotated diagrams with captions"),
                        new("callout", true, 2, "Visual identification pearls", ["pearl"]),
                        new("link", true, 2, "Hover glossary cards for figure labels")
                    ])
            ]),
        new(
            Id: "comparative-reference-article",
            Name: "Comparative Reference Sheet",
            Description: "Dense side-by-side tables and warning callouts for rapid pre-exam comparison.",
            Container: ContainerKind.StandaloneArticle,
            AuthoringFormat: AuthoringFormat.SingleMarkdown,
            Selections:
            [
                new("text", true, 1),
                new("table", true, 3, null, ["hideableCols", "stickyFirstCol"]),
                new("callout", true, 2, null, ["warning", "clinical"])
            ],
            IsBuiltIn: true,
            Components:
            [
                new(
                    "a1",
                    ComponentKind.Article,
                    "Reference Sheet",
                    [
                        new("text", true, 1, "Short orientation header"),
                        new("table", true, 3, "Comparison matrices with hideable recall columns", ["hideableCols", "stickyFirstCol"]),
                        new("callout", true, 2, "Classic exam traps and distinctions", ["warning", "clinical"])
                    ])
            ]),

        // StandaloneQuiz (3)
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
            Id: "flashcard-cloze-sprint",
            Name: "Rapid Flashcard & Cloze Sprint",
            Description: "Fast-paced active recall using flashcards, fill-in-the-blank clozes, and spot-the-error.",
            Container: ContainerKind.StandaloneQuiz,
            AuthoringFormat: AuthoringFormat.Json,
            Selections:
            [
                new("flashcard", true, 8, null, ["mixed"]),
                new("fill-blank", true, 5, null, ["fuzzy"]),
                new("spot-error", true, 3, null, ["requireFix"])
            ],
            IsBuiltIn: true,
            Components:
            [
                new(
                    "q1",
                    ComponentKind.Quiz,
                    "Recall Sprint",
                    [
                        new("flashcard", true, 8, "Mixed text and diagram cards", ["mixed"]),
                        new("fill-blank", true, 5, "Key terminology clozes", ["fuzzy"]),
                        new("spot-error", true, 3, "Catch & fix subtle misconceptions", ["requireFix"])
                    ])
            ]),
        new(
            Id: "exam-vignettes-mechanisms",
            Name: "Exam Vignettes & Mechanisms",
            Description: "Application-heavy exam drill with MCQs, mechanism ordering, differentiation, and assertion-reason.",
            Container: ContainerKind.StandaloneQuiz,
            AuthoringFormat: AuthoringFormat.MarkdownDirectory,
            Selections:
            [
                new("mcq", true, 6, null, ["single"]),
                new("mechanism", true, 3, null, ["parallel", "distractors"]),
                new("diff", true, 2, null, ["rearrange"]),
                new("assertion-reason", true, 3, null, ["5opt"])
            ],
            IsBuiltIn: true,
            Components:
            [
                new(
                    "q1",
                    ComponentKind.Quiz,
                    "Exam Vignettes",
                    [
                        new("mcq", true, 6, "Board-style single-correct vignettes", ["single"]),
                        new("mechanism", true, 3, "Order pathway steps cleanly", ["parallel", "distractors"]),
                        new("diff", true, 2, "Compare related entities", ["rearrange"]),
                        new("assertion-reason", true, 3, "5-option assertion-reason", ["5opt"])
                    ])
            ])
    ];
}
