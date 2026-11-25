using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Domain.Services;

/// <summary>Result returned by generating a strict camelCase mby session/article/quiz JSON scaffold.</summary>
public sealed record SessionScaffoldOutput(
    string RawJson,
    bool RequiresPlaceholderImage,
    string SuggestedFileName);

/// <summary>
/// Deterministically generates strict valid `mby` JSON scaffolds directly matching Organization (A/Q),
/// Organizer instance sequence, Qty, and Spec Modes—ready for export or direct `mby apply` ingestion.
/// </summary>
public static class SessionScaffoldGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static Result<SessionScaffoldOutput> Generate(PromptDraft draft, IReadOnlyList<SpecTopicItem> catalog)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(catalog);

        var catalogByTopic = catalog.ToDictionary(s => s.Topic, StringComparer.OrdinalIgnoreCase);

        // Validate that every enabled selection across active components has a positive Qty.
        if (draft.Components is { Count: > 0 })
        {
            foreach (var comp in draft.Components)
            {
                if (draft.Container == ContainerKind.StandaloneArticle && comp.Kind != ComponentKind.Article) continue;
                if (draft.Container == ContainerKind.StandaloneQuiz && comp.Kind != ComponentKind.Quiz) continue;

                var missingQty = comp.Selections.FirstOrDefault(s => s.Enabled && (s.DesiredCount is null or <= 0));
                if (missingQty is not null)
                {
                    return Result<SessionScaffoldOutput>.Failure(WrapperErrors.InvalidPromptDraft(
                        $"Spec '{missingQty.Topic}' in '{comp.Title}' has an empty Qty. Fill every active Qty before generating raw session data."));
                }
            }
        }
        else
        {
            var missingQty = draft.Selections.FirstOrDefault(s => s.Enabled && (s.DesiredCount is null or <= 0));
            if (missingQty is not null)
            {
                return Result<SessionScaffoldOutput>.Failure(WrapperErrors.InvalidPromptDraft(
                    $"Spec '{missingQty.Topic}' has an empty Qty. Fill every active Qty before generating raw session data."));
            }
        }

        var components = PromptComposer.ResolveEffectiveComponents(draft, catalogByTopic);
        var title = string.IsNullOrWhiteSpace(draft.Title) ? "Draft Study Template" : draft.Title.Trim();
        var description = string.IsNullOrWhiteSpace(draft.Description) ? null : draft.Description.Trim();
        var requiresImage = false;

        object rootPayload;
        if (draft.Container == ContainerKind.StandaloneArticle)
        {
            var artComp = components.FirstOrDefault(c => c.Kind == ComponentKind.Article)
                ?? new PromptComponentPlan("a1", ComponentKind.Article, title, []);
            rootPayload = BuildArticleObject(artComp, title, ref requiresImage, includeSessionName: false);
        }
        else if (draft.Container == ContainerKind.StandaloneQuiz)
        {
            var quizComp = components.FirstOrDefault(c => c.Kind == ComponentKind.Quiz)
                ?? new PromptComponentPlan("q1", ComponentKind.Quiz, title, [PromptComposer.DefaultFiveMcqFallback]);
            rootPayload = BuildQuizObject(quizComp, title, description, isStandalone: true, ref requiresImage);
        }
        else
        {
            var builtComponents = new List<object>();
            foreach (var comp in components)
            {
                if (comp.Kind == ComponentKind.Article)
                {
                    builtComponents.Add(BuildArticleObject(comp, null, ref requiresImage, includeSessionName: false));
                }
                else
                {
                    builtComponents.Add(BuildQuizObject(comp, null, null, isStandalone: false, ref requiresImage));
                }
            }

            rootPayload = new Dictionary<string, object?>
            {
                ["kind"] = "session",
                ["name"] = title,
                ["description"] = description,
                ["components"] = builtComponents
            };
        }

        var rawJson = JsonSerializer.Serialize(rootPayload, JsonOptions);
        var safeSlug = Slugify(title);
        var suggested = $"template_{safeSlug}.json";
        return Result<SessionScaffoldOutput>.Success(new SessionScaffoldOutput(rawJson, requiresImage, suggested));
    }

    private static Dictionary<string, object?> BuildArticleObject(
        PromptComponentPlan comp,
        string? standaloneTitle,
        ref bool requiresImage,
        bool includeSessionName)
    {
        var sequence = BuildInstanceSequence(comp);
        var blocks = new List<object>();
        var links = new List<object>();
        var linkIds = new List<string>();
        var selLookup = comp.Selections.Where(s => s.Enabled).ToDictionary(s => s.Topic, StringComparer.OrdinalIgnoreCase);

        foreach (var inst in sequence)
        {
            selLookup.TryGetValue(inst.Topic, out var sel);
            var modes = sel?.Modes ?? [];
            var num = inst.InstanceNumber;

            if (string.Equals(inst.Topic, SpecCatalogDefaults.VirtualLinkTopicName, StringComparison.OrdinalIgnoreCase))
            {
                var cardId = $"card-{links.Count + 1}";
                linkIds.Add(cardId);
                links.Add(new Dictionary<string, object?>
                {
                    ["id"] = cardId,
                    ["title"] = $"Reference Card {links.Count + 1}",
                    ["text"] = "Hover card explanation placeholder."
                });
                continue;
            }

            switch (inst.Topic.ToLowerInvariant())
            {
                case "image":
                    requiresImage = true;
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["kind"] = "image",
                        ["imagePath"] = "assets/placeholder.png",
                        ["caption"] = $"Figure {num} placeholder caption",
                        ["alt"] = $"Figure {num} placeholder"
                    });
                    break;

                case "callout":
                    var tone = modes.FirstOrDefault(m => m is "pearl" or "clinical" or "warning") ?? "pearl";
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["kind"] = "callout",
                        ["tone"] = tone,
                        ["title"] = $"Callout {num}",
                        ["text"] = sel?.Notes is { Length: > 0 } ? sel.Notes : $"Replace with {tone} takeaway."
                    });
                    break;

                case "table":
                    var hideable = modes.Contains("hideableCols", StringComparer.OrdinalIgnoreCase);
                    var sticky = modes.Contains("stickyFirstCol", StringComparer.OrdinalIgnoreCase);
                    var tableObj = new Dictionary<string, object?>
                    {
                        ["kind"] = "table",
                        ["title"] = $"Comparison Table {num}",
                        ["headers"] = new[] { "Feature", "Item A", "Item B" },
                        ["rows"] = new[] { new[] { "Row 1", "Value A", "Value B" } }
                    };
                    if (sticky) tableObj["stickyFirstCol"] = true;
                    if (hideable) tableObj["hideableCols"] = new[] { 2, 3 };
                    blocks.Add(tableObj);
                    break;

                default:
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["kind"] = "text",
                        ["text"] = $"## Section {num}\n\n" + (sel?.Notes is { Length: > 0 } ? sel.Notes : "Replace with study prose.")
                    });
                    break;
            }
        }

        // Article lint invariant requires >= 1 non-empty block.
        if (blocks.Count == 0)
        {
            blocks.Add(new Dictionary<string, object?>
            {
                ["kind"] = "text",
                ["text"] = "## Draft Section\n\nReplace with study prose."
            });
        }

        if (linkIds.Count > 0)
        {
            var refsLine = string.Join(", ", linkIds.Select((id, i) => $"[Reference {i + 1}](mby://card/{id})"));
            var firstTextBlock = blocks
                .OfType<Dictionary<string, object?>>()
                .FirstOrDefault(b => b.TryGetValue("kind", out var k) && string.Equals(k as string, "text", StringComparison.OrdinalIgnoreCase));
            if (firstTextBlock is not null && firstTextBlock.TryGetValue("text", out var existingObj) && existingObj is string existingText)
            {
                firstTextBlock["text"] = $"{existingText}\n\nReferences: {refsLine}.";
            }
            else
            {
                blocks.Add(new Dictionary<string, object?>
                {
                    ["kind"] = "text",
                    ["text"] = $"Hover over {refsLine} for details."
                });
            }
        }

        var article = new Dictionary<string, object?>
        {
            ["kind"] = "article",
            ["title"] = standaloneTitle ?? (string.IsNullOrWhiteSpace(comp.Title) ? "Study Article" : comp.Title),
            ["blocks"] = blocks
        };

        if (links.Count > 0)
        {
            article["links"] = links;
        }

        if (includeSessionName && !string.IsNullOrWhiteSpace(standaloneTitle))
        {
            article["title"] = standaloneTitle;
        }

        return article;
    }

    private static Dictionary<string, object?> BuildQuizObject(
        PromptComponentPlan comp,
        string? standaloneName,
        string? standaloneDescription,
        bool isStandalone,
        ref bool requiresImage)
    {
        var sequence = BuildInstanceSequence(comp);
        var questions = new List<object>();
        var selLookup = comp.Selections.Where(s => s.Enabled).ToDictionary(s => s.Topic, StringComparer.OrdinalIgnoreCase);

        foreach (var inst in sequence)
        {
            selLookup.TryGetValue(inst.Topic, out var sel);
            var modes = sel?.Modes ?? [];
            var num = inst.InstanceNumber;

            switch (inst.Topic.ToLowerInvariant())
            {
                case "flashcard":
                    var diagramMode = modes.Contains("diagram", StringComparer.OrdinalIgnoreCase);
                    if (diagramMode) requiresImage = true;
                    questions.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "flashcard",
                        ["front"] = diagramMode
                            ? new Dictionary<string, object?> { ["text"] = $"Identify structure ({num})", ["imagePath"] = "assets/placeholder.png" }
                            : new Dictionary<string, object?> { ["text"] = $"Flashcard Front {num}" },
                        ["back"] = new Dictionary<string, object?> { ["text"] = $"Flashcard Answer {num}" }
                    });
                    break;

                case "fill-blank":
                    questions.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "fill-blank",
                        ["text"] = $"Fill-in-the-blank prompt {num}: the target term is {{{{answer}}}}.",
                        ["fuzzy"] = modes.Contains("fuzzy", StringComparer.OrdinalIgnoreCase),
                        ["caseSensitive"] = modes.Contains("caseSensitive", StringComparer.OrdinalIgnoreCase),
                        ["explanation"] = $"Explanation placeholder {num}."
                    });
                    break;

                case "assertion-reason":
                    var hasBothFalse = modes.Contains("both-false", StringComparer.OrdinalIgnoreCase);
                    var scheme = hasBothFalse || modes.Contains("5opt", StringComparer.OrdinalIgnoreCase) ? "5opt" : "4opt";
                    var answer = hasBothFalse ? "both-false" : "both-explain";
                    questions.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "assertion-reason",
                        ["assertion"] = $"Assertion statement {num}.",
                        ["reason"] = $"Reason statement {num}.",
                        ["answer"] = answer,
                        ["scheme"] = scheme,
                        ["explanation"] = $"Assertion-reason explanation {num}."
                    });
                    break;

                case "spot-error":
                    questions.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "spot-error",
                        ["text"] = $"Statement {num} with [valid span] and [~~incorrect term~~ -> corrected term].",
                        ["requireFix"] = modes.Contains("requireFix", StringComparer.OrdinalIgnoreCase),
                        ["allowNoError"] = modes.Contains("allowNoError", StringComparer.OrdinalIgnoreCase),
                        ["explanation"] = $"Spot-error explanation {num}."
                    });
                    break;

                case "diff":
                    var diffMode = modes.FirstOrDefault(m => m is "fill" or "spot-wrong" or "rearrange") ?? "fill";
                    var rowCellA = diffMode switch
                    {
                        "spot-wrong" => "~~Incorrect~~ -> Correct",
                        "rearrange" => "Correct Value A",
                        _ => "{{Value A}}"
                    };
                    var rowCellB = diffMode == "fill" ? "{{Value B}}" : "Value B";
                    var diffObj = new Dictionary<string, object?>
                    {
                        ["type"] = "diff",
                        ["mode"] = diffMode,
                        ["title"] = $"Differentiation {num}",
                        ["headers"] = new[] { "Property", "Entity A", "Entity B" },
                        ["rows"] = new[] { new[] { "Property 1", rowCellA, rowCellB } },
                        ["explanation"] = $"Comparison explanation {num}."
                    };
                    if (modes.Contains("bank", StringComparer.OrdinalIgnoreCase))
                    {
                        diffObj["bank"] = true;
                    }

                    questions.Add(diffObj);
                    break;

                case "mechanism":
                    var steps = new List<Dictionary<string, object?>>
                    {
                        new() { ["orderGroup"] = 1, ["text"] = "Step 1" },
                        new() { ["orderGroup"] = 2, ["text"] = "Step 2A" }
                    };
                    if (modes.Contains("parallel", StringComparer.OrdinalIgnoreCase))
                    {
                        steps.Add(new Dictionary<string, object?> { ["orderGroup"] = 2, ["text"] = "Step 2B (parallel)" });
                    }
                    if (modes.Contains("distractors", StringComparer.OrdinalIgnoreCase))
                    {
                        steps.Add(new Dictionary<string, object?> { ["isDistractor"] = true, ["text"] = "Distractor step" });
                    }

                    questions.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "mechanism",
                        ["title"] = $"Mechanism Pathway {num}",
                        ["steps"] = steps,
                        ["explanation"] = $"Mechanism explanation {num}."
                    });
                    break;

                case "relabel":
                    requiresImage = true;
                    var relMode = modes.FirstOrDefault(m => m is "reveal" or "bank" or "recall") ?? "bank";
                    var relStyle = modes.FirstOrDefault(m => m is "solid" or "blur" or "pixelate" or "whiteout") ?? "blur";
                    questions.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "relabel",
                        ["imagePath"] = "assets/placeholder.png",
                        ["prompt"] = $"Identify labeled structures ({num}).",
                        ["mode"] = relMode,
                        ["style"] = relStyle,
                        ["boxes"] = new object[]
                        {
                            new Dictionary<string, object?> { ["label"] = "Label A", ["rect"] = new[] { 0.1, 0.1, 0.25, 0.1 } },
                            new Dictionary<string, object?> { ["label"] = "Label B", ["rect"] = new[] { 0.55, 0.4, 0.25, 0.1 } }
                        }
                    });
                    break;

                default:
                    var multi = modes.Contains("multi", StringComparer.OrdinalIgnoreCase);
                    questions.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "mcq",
                        ["text"] = $"MCQ Placeholder {num}: replace with stem.",
                        ["isMultiCorrect"] = multi,
                        ["explanation"] = $"Rationale placeholder {num}.",
                        ["options"] = multi
                            ? new object[]
                            {
                                new Dictionary<string, object?> { ["text"] = "Correct Option A", ["isCorrect"] = true },
                                new Dictionary<string, object?> { ["text"] = "Correct Option B", ["isCorrect"] = true },
                                new Dictionary<string, object?> { ["text"] = "Distractor C", ["isCorrect"] = false }
                            }
                            : new object[]
                            {
                                new Dictionary<string, object?> { ["text"] = "Correct Option A", ["isCorrect"] = true },
                                new Dictionary<string, object?> { ["text"] = "Distractor B", ["isCorrect"] = false },
                                new Dictionary<string, object?> { ["text"] = "Distractor C", ["isCorrect"] = false },
                                new Dictionary<string, object?> { ["text"] = "Distractor D", ["isCorrect"] = false }
                            }
                    });
                    break;
            }
        }

        var quiz = new Dictionary<string, object?>
        {
            ["kind"] = "quiz",
            ["quizType"] = "mixed",
            ["questions"] = questions
        };

        if (isStandalone)
        {
            quiz["name"] = standaloneName ?? (string.IsNullOrWhiteSpace(comp.Title) ? "Draft Quiz" : comp.Title);
            if (!string.IsNullOrWhiteSpace(standaloneDescription))
            {
                quiz["description"] = standaloneDescription;
            }
        }
        else
        {
            quiz["title"] = string.IsNullOrWhiteSpace(comp.Title) ? "Practice Quiz" : comp.Title;
        }

        return quiz;
    }

    private static List<OrderedSpecInstance> BuildInstanceSequence(PromptComponentPlan comp)
    {
        if (comp.OrderedItems is { Count: > 0 })
        {
            var allowedTopics = comp.Selections
                .Where(s => s.Enabled)
                .Select(s => s.Topic)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var filtered = new List<OrderedSpecInstance>();
            var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var inst in comp.OrderedItems)
            {
                if (!allowedTopics.Contains(inst.Topic))
                {
                    continue;
                }

                var nextNum = counters.TryGetValue(inst.Topic, out var cur) ? cur + 1 : 1;
                counters[inst.Topic] = nextNum;
                filtered.Add(inst with { InstanceNumber = nextNum });
            }

            if (filtered.Count > 0 || allowedTopics.Count == 0)
            {
                return filtered;
            }
        }

        var list = new List<OrderedSpecInstance>();
        foreach (var sel in comp.Selections.Where(s => s.Enabled))
        {
            var count = Math.Clamp(sel.DesiredCount ?? 1, 1, 50);
            for (var i = 1; i <= count; i++)
            {
                list.Add(new OrderedSpecInstance($"{sel.Topic}_{i}", sel.Topic, i));
            }
        }

        return list;
    }

    private static string Slugify(string raw)
    {
        var chars = raw.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray();
        var collapsed = new string(chars);
        while (collapsed.Contains("__", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("__", "_", StringComparison.Ordinal);
        }

        collapsed = collapsed.Trim('_');
        return string.IsNullOrWhiteSpace(collapsed) ? "session" : (collapsed.Length > 28 ? collapsed[..28] : collapsed);
    }
}
