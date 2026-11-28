namespace Meloberry.Wrapper.Domain.Models;

/// <summary>Selectable mode/option choice for an authoring spec inside Prompt Studio.</summary>
public sealed record SpecModeOption(string Key, string Label, string? IconKey = null);

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
    IReadOnlyList<SpecModeGroup>? ModeGroups = null,
    string? DisplayName = null,
    bool SupportsImage = false,
    string? MarkdownHint = null,
    string? MarkdownStarter = null);

/// <summary>Built-in fallback topic catalog used when `mby` is not yet installed.</summary>
public static class SpecCatalogDefaults
{
    /// <summary>Minimal fallback list when `mby` has not been installed yet so Prompt Studio remains usable.</summary>
    public static IReadOnlyList<SpecTopicItem> BuiltInFallbackCatalog { get; } =
    [
        new("text", "content", "block", "Markdown + KaTeX vertical text block.", "{ kind:\"text\", ref?:string, text:string }", "Inside article.blocks[]", false, null, "Text Block", false),
        new("image", "content", "block", "Confined article image block with optional caption.", "{ kind:\"image\", ref?:string, imagePath:string, caption?:string, alt?:string }", "Inside article.blocks[]", false, null, "Image Block", true),
        new("callout", "content", "block", "Pearl / clinical / warning highlighted callout.", "{ kind:\"callout\", tone?:\"pearl\"|\"clinical\"|\"warning\", title?:string, text:string }", "Inside article.blocks[]", false, null, "Callout Box", false),
        new("table", "content", "block", "Interactive Markdown/JSON table with hideable study columns.", "{ kind:\"table\", title?:string, headers:string[], rows:string[][], hideableCols?:int[] }", "Inside article.blocks[]", false, null, "Study Table", false),
        new("diagram-explore", "content", "block", "Interactive diagram with clickable hotspot annotations.", "{ kind:\"diagram-explore\", ref?:string, imagePath:string, title?:string, hotspots:[{label:string,note:string}] }", "Inside article.blocks[]", false, null, "Interactive Diagram", true),
        new("reveal", "content", "block", "Progressive step-through active-recall cards.", "{ kind:\"reveal\", ref?:string, title?:string, showFirst?:bool, steps:[{heading:string,body:string}] }", "Inside article.blocks[]", false, null, "Step-by-Step Reveal", false),
        new("link", "content", "block", "Article hover link cards referenced inline via [text](mby://card/<id>) or :::link blocks.", "{ id:string, title?:string, text?:string, imagePath?:string }", "Inside article.links[] or Markdown article", false, null, "Hover Link Card", true),
        new("mcq", "content", "question", "Single or multi-correct MCQ question with optional image.", "{ type:\"mcq\", text:string, isMultiCorrect?:bool, options:[{text?:string,isCorrect?:bool}] }", "Inside quiz.questions[]", false, null, "Multiple Choice", true),
        new("flashcard", "content", "question", "Two-sided active-recall flashcard.", "{ type:\"flashcard\", front:{text?:string,imagePath?:string}, back:{text?:string,imagePath?:string} }", "Inside quiz.questions[]", false, null, "Flashcard", true),
        new("fill-blank", "content", "question", "Fill-in-the-blank stem with {{primary | alias}} slots.", "{ type:\"fill-blank\", ref?:string, text:string, fuzzy?:bool, caseSensitive?:bool, placeholder?:string, explanation?:string }", "Inside quiz.questions[]", false, null, "Fill in the Blank", false),
        new("assertion-reason", "content", "question", "Assertion-Reason 4- or 5-option clinical reasoning item.", "{ type:\"assertion-reason\", assertion:string, reason:string, answer:string, scheme?:string }", "Inside quiz.questions[]", false, null, "Assertion-Reason", false),
        new("spot-error", "content", "question", "Clinical/statement spot-the-error span correction.", "{ type:\"spot-error\", ref?:string, text:string, imagePath?:string, requireFix?:bool, allowNoError?:bool, noError?:bool, explanation?:string }", "Inside quiz.questions[]", false, null, "Spot the Error", true),
        new("diff", "content", "question", "Side-by-side comparison / differentiation table drill.", "{ type:\"diff\", ref?:string, mode:\"fill\"|\"spot-wrong\"|\"rearrange\", title?:string, bank?:bool, fuzzy?:bool, caseSensitive?:bool, allowNoError?:bool, headers:string[], rows:string[][], explanation?:string }", "Inside quiz.questions[]", false, null, "Differentiation Table", false),
        new("mechanism", "content", "question", "Ordered pathway / mechanism step sequencing.", "{ type:\"mechanism\", ref?:string, title?:string, steps:[{orderGroup?:int, text:string, isDistractor?:bool}], explanation?:string }", "Inside quiz.questions[]", false, null, "Mechanism Sequence", false),
        new("relabel", "content", "question", "Image label identification / matching drill.", "{ type:\"relabel\", ref?:string, imagePath:string, prompt?:string, mode?:\"reveal\"|\"bank\"|\"recall\", style?:\"solid\"|\"blur\"|\"pixelate\"|\"whiteout\", masksPath?:string, include?:string[], boxes?:[{label:string,aliases?:string[],rect?:[x,y,w,h]}], distractors?:string[], explanation?:string }", "Inside quiz.questions[]", false, null, "Diagram Relabel", true),
        new("case", "content", "question", "Shared clinical vignette driving linked sub-questions.", "{ type:\"case\", ref?:string, title?:string, stem:string, questions:[...], explanation?:string }", "Inside quiz.questions[]", false, null, "Case Vignette", true),
        new("classify", "content", "question", "Multi-bucket classification sorting drill.", "{ type:\"classify\", ref?:string, title?:string, buckets:string[], items:[{bucket:string,text:string}] }", "Inside quiz.questions[]", false, null, "Classification", false),
        new("match", "content", "question", "Two-column matching pairs with optional distractors.", "{ type:\"match\", ref?:string, title?:string, pairs:[{left:string,right:string}] }", "Inside quiz.questions[]", false, null, "Matching", false),
        new("numeric", "content", "question", "Quantitative numeric question with tolerance.", "{ type:\"numeric\", ref?:string, prompt:string, answer:number, tolerance?:number|string, unit?:string }", "Inside quiz.questions[]", false, null, "Numeric Response", true)
    ];

    private static readonly Lazy<Dictionary<string, string>> FallbackCategoryByTopic = new(() =>
        BuiltInFallbackCatalog.ToDictionary(s => s.Topic, s => s.Category, StringComparer.OrdinalIgnoreCase));

    /// <summary>Resolves the built-in category for a topic from <see cref="BuiltInFallbackCatalog"/>.</summary>
    public static string GetCategoryForTopic(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return "other";
        }

        var trimmed = topic.Trim();
        if (FallbackCategoryByTopic.Value.TryGetValue(trimmed, out var category))
        {
            return category;
        }

        return trimmed.ToLowerInvariant() switch
        {
            "session" or "article" or "quiz" => "container",
            _ => "other"
        };
    }
}
