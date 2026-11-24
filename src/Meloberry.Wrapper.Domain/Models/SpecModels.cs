namespace Meloberry.Wrapper.Domain.Models;

/// <summary>Selectable mode/option choice for an authoring spec inside Prompt Studio.</summary>
public sealed record SpecModeOption(string Key, string Label);

/// <summary>Group of selectable modes/variants (single- or multi-select) for a spec topic.</summary>
public sealed record SpecModeGroup(
    string FieldName,
    string Label,
    bool MultiSelect,
    IReadOnlyList<SpecModeOption> Options);

/// <summary>Compact authoring spec topic displayed in the Prompt Studio shopping list.</summary>
public sealed record SpecTopicItem(
    string Topic,
    string Scope,
    string Category,
    string Summary,
    string CompactSignature,
    string Command,
    bool IsVirtual = false,
    IReadOnlyList<SpecModeGroup>? ModeGroups = null);

/// <summary>Built-in fallback, virtual blocks, and mode descriptors aligned with live mby schemas.</summary>
public static class SpecCatalogDefaults
{
    public const string VirtualLinkTopicName = "link";

    public static readonly SpecTopicItem VirtualLinkTopic = new(
        Topic: VirtualLinkTopicName,
        Scope: "content",
        Category: "block",
        Summary: "Article hover link cards referenced inline via [text](mby://card/<id>) or :::link blocks.",
        CompactSignature: "// Inside article.links[] + inline Markdown\n[label](mby://card/<id>)\n// Or Markdown card block:\n:::link{id=\"card-id\" title=\"Title\"}\nHover card body or ![](assets/img.png)\n:::",
        Command: "Inside article.links[] or Markdown article",
        IsVirtual: true,
        ModeGroups: GetModeGroupsForTopic(VirtualLinkTopicName));

    /// <summary>Returns UI-selectable modes derived from live `mby spec` schemas.</summary>
    public static IReadOnlyList<SpecModeGroup> GetModeGroupsForTopic(string topic) => topic.ToLowerInvariant() switch
    {
        "callout" =>
        [
            new("tone", "Tone", true,
            [
                new("pearl", "Pearl"),
                new("clinical", "Clinical"),
                new("warning", "Warning")
            ])
        ],
        "table" =>
        [
            new("options", "Table", true,
            [
                new("hideableCols", "Hideable cols"),
                new("stickyFirstCol", "Sticky 1st col")
            ])
        ],
        "mcq" =>
        [
            new("variant", "Selection", false,
            [
                new("single", "Single"),
                new("multi", "Multi-correct"),
                new("mixed", "Mixed")
            ])
        ],
        "flashcard" =>
        [
            new("variant", "Style", false,
            [
                new("text", "Text"),
                new("diagram", "Diagram/Masked"),
                new("mixed", "Mixed")
            ])
        ],
        "fill-blank" =>
        [
            new("matching", "Matching", true,
            [
                new("fuzzy", "Fuzzy"),
                new("caseSensitive", "Case-sensitive")
            ])
        ],
        "assertion-reason" =>
        [
            new("scheme", "Scheme", false,
            [
                new("4opt", "4-opt"),
                new("5opt", "5-opt"),
                new("both-false", "Include both-false")
            ])
        ],
        "spot-error" =>
        [
            new("options", "Behavior", true,
            [
                new("requireFix", "Require fix"),
                new("allowNoError", "Allow no-error")
            ])
        ],
        "diff" =>
        [
            new("mode", "Mode", false,
            [
                new("fill", "Fill"),
                new("spot-wrong", "Spot wrong"),
                new("rearrange", "Rearrange")
            ]),
            new("options", "Extra", true, [new("bank", "Word bank")])
        ],
        "mechanism" =>
        [
            new("features", "Structure", true,
            [
                new("parallel", "Parallel steps"),
                new("distractors", "Distractors")
            ])
        ],
        "relabel" =>
        [
            new("mode", "Mode", false,
            [
                new("reveal", "Reveal"),
                new("bank", "Bank"),
                new("recall", "Recall")
            ]),
            new("style", "Mask", false,
            [
                new("solid", "Solid"),
                new("blur", "Blur"),
                new("pixelate", "Pixelate"),
                new("whiteout", "Whiteout")
            ])
        ],
        _ => []
    };

    /// <summary>Minimal fallback list when `mby` has not been installed yet so Prompt Studio remains usable.</summary>
    public static IReadOnlyList<SpecTopicItem> BuiltInFallbackCatalog { get; } =
    [
        new("text", "content", "block", "Markdown + KaTeX vertical text block.", "{ kind:\"text\", ref?:string, text:string }", "Inside article.blocks[]", false, GetModeGroupsForTopic("text")),
        new("image", "content", "block", "Confined article image block with optional caption.", "{ kind:\"image\", ref?:string, imagePath:string, caption?:string, alt?:string }", "Inside article.blocks[]", false, GetModeGroupsForTopic("image")),
        new("callout", "content", "block", "Pearl / clinical / warning highlighted callout.", "{ kind:\"callout\", tone?:\"pearl\"|\"clinical\"|\"warning\", title?:string, text:string }", "Inside article.blocks[]", false, GetModeGroupsForTopic("callout")),
        new("table", "content", "block", "Interactive Markdown/JSON table with hideable study columns.", "{ kind:\"table\", title?:string, headers:string[], rows:string[][], hideableCols?:int[] }", "Inside article.blocks[]", false, GetModeGroupsForTopic("table")),
        VirtualLinkTopic,
        new("mcq", "content", "question", "Single or multi-correct MCQ question with optional image.", "{ type:\"mcq\", text:string, isMultiCorrect?:bool, options:[{text?:string,isCorrect?:bool}] }", "Inside quiz.questions[]", false, GetModeGroupsForTopic("mcq")),
        new("flashcard", "content", "question", "Two-sided active-recall flashcard.", "{ type:\"flashcard\", front:{text?:string,imagePath?:string}, back:{text?:string,imagePath?:string} }", "Inside quiz.questions[]", false, GetModeGroupsForTopic("flashcard")),
        new("fill-blank", "content", "question", "Fill-in-the-blank stem with {{answer}} slots.", "{ type:\"fill-blank\", template:string, blanks:[{answers:string[]}] }", "Inside quiz.questions[]", false, GetModeGroupsForTopic("fill-blank")),
        new("assertion-reason", "content", "question", "Assertion-Reason 4- or 5-option clinical reasoning item.", "{ type:\"assertion-reason\", assertion:string, reason:string, answer:string, scheme?:string }", "Inside quiz.questions[]", false, GetModeGroupsForTopic("assertion-reason")),
        new("spot-error", "content", "question", "Clinical/statement spot-the-error span correction.", "{ type:\"spot-error\", tokens:..., corrections:... }", "Inside quiz.questions[]", false, GetModeGroupsForTopic("spot-error")),
        new("diff", "content", "question", "Side-by-side comparison / differentiation table drill.", "{ type:\"diff\", prompt:string, leftLabel:string, rightLabel:string, rows:[...] }", "Inside quiz.questions[]", false, GetModeGroupsForTopic("diff")),
        new("mechanism", "content", "question", "Ordered pathway / mechanism step sequencing.", "{ type:\"mechanism\", prompt:string, steps:[...], branches?:[...] }", "Inside quiz.questions[]", false, GetModeGroupsForTopic("mechanism")),
        new("relabel", "content", "question", "Image label identification / matching drill.", "{ type:\"relabel\", prompt:string, imagePath:string, pins:[...] }", "Inside quiz.questions[]", false, GetModeGroupsForTopic("relabel"))
    ];
}
