using System.Text;
using System.Text.RegularExpressions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Domain.Services;

/// <summary>
/// Assembles a live `PromptDraft` + loaded `.md` contents into a modular `mby` Markdown directory
/// (`_session.md` + ordered component `.md` files) using data-driven directive smart-wrapping.
/// </summary>
public static partial class WorkspaceMarkdownAssembler
{
    [GeneratedRegex(@"!\[[^\]]*\]\((?:<([^>\r\n]+)>|([^)\s""]+))(?:\s+""[^""]*"")?\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownImageRegex();

    private static readonly HashSet<string> ImageCapableDirectiveTopics = new(StringComparer.OrdinalIgnoreCase)
        { "relabel", "diagram-explore", "mcq", "flashcard", "spot-error", "numeric", "case" };

    [GeneratedRegex(@"(?:image|imagePath|masks|masksPath)\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DirectiveAssetAttrRegex();

    [GeneratedRegex(@"^\s*(?:image|imagePath|masks|masksPath)\s*:\s*""?([^""\r\n]+)""?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex YamlAssetLineRegex();

    [GeneratedRegex(@"^\s*(?:image|imagePath)\s*:\s*""?([^""\r\n]+)""?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex YamlImageLineRegex();

    public static Result<AssembledWorkspaceFiles> Assemble(
        PromptDraft draft,
        IReadOnlyDictionary<string, string> loadedMarkdownFiles,
        bool allowPlaceholders)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(loadedMarkdownFiles);

        var title = string.IsNullOrWhiteSpace(draft.Title) ? "Study Module" : draft.Title.Trim();
        var slug = Slugify(title, "study_module");
        var components = ResolveActiveComponents(draft);
        if (components.Count == 0)
        {
            return Result<AssembledWorkspaceFiles>.Failure(
                WrapperErrors.InvalidPromptDraft("Cannot finalize an empty draft. Add an Article or Quiz component first."));
        }

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var referencedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingSpecs = new List<MissingSpecContentItem>();
        var placeholdersUsed = new List<string>();
        var requiresPlaceholderImg = false;
        var componentFileOrder = new List<string>();

        for (var i = 0; i < components.Count; i++)
        {
            var comp = components[i];
            var compTitle = string.IsNullOrWhiteSpace(comp.Title)
                ? (comp.Kind == ComponentKind.Quiz ? $"Quiz {i + 1}" : $"Article {i + 1}")
                : comp.Title.Trim();
            var compSlug = Slugify(compTitle, comp.Kind == ComponentKind.Quiz ? $"quiz_{i + 1}" : $"article_{i + 1}");
            var fileName = components.Count == 1 && draft.Container != ContainerKind.Session
                ? $"{compSlug}.md"
                : $"{i + 1:D2}_{compSlug}.md";

            componentFileOrder.Add(fileName);
            var md = AssembleComponentMarkdown(
                comp,
                compTitle,
                compSlug,
                draft,
                loadedMarkdownFiles,
                allowPlaceholders,
                referencedAssets,
                missingSpecs,
                placeholdersUsed,
                ref requiresPlaceholderImg);
            files[fileName] = md;
        }

        if (draft.Container == ContainerKind.Session)
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine("kind: session");
            sb.AppendLine($"name: {YamlQuote(title)}");
            if (!string.IsNullOrWhiteSpace(draft.Description))
            {
                sb.AppendLine($"description: {YamlQuote(draft.Description.Trim())}");
            }
            sb.AppendLine("order:");
            foreach (var f in componentFileOrder)
            {
                sb.AppendLine($"  - {f}");
            }
            sb.AppendLine("---");
            files["_session.md"] = sb.ToString();
        }

        return Result<AssembledWorkspaceFiles>.Success(new AssembledWorkspaceFiles(
            Slug: slug,
            Files: files,
            ReferencedAssetPaths: referencedAssets.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            MissingContentSpecs: missingSpecs,
            PlaceholderSpecsUsed: placeholdersUsed,
            RequiresPlaceholderImage: requiresPlaceholderImg));
    }

    private static string AssembleComponentMarkdown(
        PromptComponentPlan comp,
        string compTitle,
        string compRef,
        PromptDraft draft,
        IReadOnlyDictionary<string, string> loadedMarkdownFiles,
        bool allowPlaceholders,
        HashSet<string> referencedAssets,
        List<MissingSpecContentItem> missingSpecs,
        List<string> placeholdersUsed,
        ref bool requiresPlaceholderImg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine(comp.Kind == ComponentKind.Quiz ? "kind: quiz" : "kind: article");
        if (comp.Kind == ComponentKind.Quiz)
        {
            sb.AppendLine("quizType: mixed");
            if (draft.Container == ContainerKind.StandaloneQuiz)
            {
                sb.AppendLine($"name: {YamlQuote(compTitle)}");
                if (!string.IsNullOrWhiteSpace(draft.Description))
                {
                    sb.AppendLine($"description: {YamlQuote(draft.Description.Trim())}");
                }
            }
            else
            {
                sb.AppendLine($"title: {YamlQuote(compTitle)}");
            }
        }
        else
        {
            sb.AppendLine($"title: {YamlQuote(compTitle)}");
        }
        sb.AppendLine($"ref: {compRef}");
        sb.AppendLine("---");
        sb.AppendLine();

        var instances = ExpandOrderedInstances(comp);
        if (instances.Count == 0)
        {
            if (allowPlaceholders)
            {
                var fallback = comp.Kind == ComponentKind.Quiz
                    ? BuildPlaceholderBody("mcq", null, 1, ref requiresPlaceholderImg)
                    : "## Draft Section\n\nPlaceholder content.";
                sb.AppendLine(fallback);
            }
            else
            {
                missingSpecs.Add(new MissingSpecContentItem(comp.Id, compTitle, $"{comp.Id}_empty", comp.Kind == ComponentKind.Quiz ? "quiz" : "article"));
            }

            return sb.ToString();
        }

        var chunks = new List<string>();
        var linkCounter = 0;

        foreach (var inst in instances)
        {
            if (inst.Content?.Mode == SpecContentMode.ImageFile)
            {
                var topicLower = inst.Topic.Trim().ToLowerInvariant();
                var hasImg = !string.IsNullOrWhiteSpace(inst.Content.ImagePath);
                var needsBody = topicLower != "image" && string.IsNullOrWhiteSpace(inst.Content.RawText);

                if (!hasImg || (needsBody && !allowPlaceholders))
                {
                    if (!allowPlaceholders)
                    {
                        missingSpecs.Add(new MissingSpecContentItem(comp.Id, compTitle, inst.Id, inst.Topic));
                        continue;
                    }

                    placeholdersUsed.Add(inst.Id);
                    if (!hasImg)
                    {
                        chunks.Add(BuildPlaceholderBody(inst.Topic, inst.Subtype, inst.InstanceNumber, ref requiresPlaceholderImg));
                        continue;
                    }
                }

                var relImg = inst.Content.ImagePath!.Trim().Replace('\\', '/');
                string formattedImg;
                if (needsBody)
                {
                    placeholdersUsed.Add(inst.Id);
                    formattedImg = BuildImageDirectivePlaceholder(topicLower, inst.Subtype, relImg, inst.InstanceNumber);
                }
                else
                {
                    formattedImg = FormatImageContent(inst, relImg, inst.Content.Caption, inst.Content.AltText, inst.Content.RawText, ref requiresPlaceholderImg);
                }
                CollectReferencedAssets(formattedImg, referencedAssets);
                TryAddRelativeAsset(relImg, referencedAssets);
                chunks.Add(formattedImg);
                continue;
            }

            var resolvedContent = ResolveSpecText(inst, loadedMarkdownFiles);
            if (string.IsNullOrWhiteSpace(resolvedContent))
            {
                if (!allowPlaceholders)
                {
                    missingSpecs.Add(new MissingSpecContentItem(comp.Id, compTitle, inst.Id, inst.Topic));
                    continue;
                }

                placeholdersUsed.Add(inst.Id);
                chunks.Add(BuildPlaceholderBody(inst.Topic, inst.Subtype, inst.InstanceNumber, ref requiresPlaceholderImg));
                continue;
            }

            var wrapped = SmartWrapContent(inst, resolvedContent, ref linkCounter, ref requiresPlaceholderImg);
            CollectReferencedAssets(wrapped, referencedAssets);
            chunks.Add(wrapped);
        }

        sb.Append(string.Join("\n\n", chunks));
        sb.AppendLine();
        return sb.ToString();
    }

    private static string? ResolveSpecText(OrderedSpecInstance inst, IReadOnlyDictionary<string, string> loadedMarkdownFiles)
    {
        if (inst.Content is null || inst.Content.Mode == SpecContentMode.None)
        {
            return null;
        }

        string? raw = inst.Content.Mode switch
        {
            SpecContentMode.RawText => inst.Content.RawText,
            SpecContentMode.MarkdownFile when !string.IsNullOrWhiteSpace(inst.Content.MarkdownFilePath) =>
                loadedMarkdownFiles.TryGetValue(inst.Content.MarkdownFilePath.Replace('\\', '/'), out var md) ? md : null,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.TrimStart('\uFEFF').Trim();
    }

    private static string FormatMarkdownImageTarget(string relPath) =>
        relPath.Any(c => char.IsWhiteSpace(c) || c is '(' or ')') ? $"<{relPath}>" : relPath;

    private static string FormatImageContent(
        OrderedSpecInstance inst,
        string relImg,
        string? caption,
        string? altText,
        string? rawBody,
        ref bool requiresPlaceholderImg)
    {
        var topic = inst.Topic.Trim().ToLowerInvariant();
        if (topic == "image")
        {
            var safeAlt = (altText ?? string.Empty).Replace("[", string.Empty, StringComparison.Ordinal)
                .Replace("]", string.Empty, StringComparison.Ordinal)
                .Replace('\r', ' ').Replace('\n', ' ').Trim();
            var safeCap = (caption ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace('\r', ' ').Replace('\n', ' ').Trim();
            var target = FormatMarkdownImageTarget(relImg);
            return string.IsNullOrWhiteSpace(safeCap)
                ? $"![{safeAlt}]({target})"
                : $"![{safeAlt}]({target} \"{safeCap}\")";
        }

        var body = (rawBody ?? string.Empty).TrimStart('\uFEFF').Trim();
        if (StartsWithOuterDirective(body, topic))
        {
            return InjectImageIntoPrefencedDirective(body, relImg);
        }

        var attrs = BuildDefaultDirectiveAttributes(topic, inst.Subtype, relImg, ref requiresPlaceholderImg);
        var header = attrs.Count == 0 ? $":::{topic}" : $":::{topic}{{{string.Join(" ", attrs)}}}";
        return string.IsNullOrWhiteSpace(body) ? $"{header}\n:::" : $"{header}\n{body}\n:::";
    }

    private static bool StartsWithOuterDirective(string body, string topic)
    {
        if (!body.StartsWith(":::", StringComparison.Ordinal))
        {
            return false;
        }

        var nlIdx = body.IndexOf('\n');
        var firstLine = (nlIdx >= 0 ? body[..nlIdx] : body).TrimEnd('\r').Trim();
        var afterColons = firstLine.TrimStart(':');
        if (!afterColons.StartsWith(topic, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (afterColons.Length == topic.Length)
        {
            return true;
        }

        var boundary = afterColons[topic.Length];
        return boundary is '{' or ' ' or '\t';
    }

    private static bool IsInnerSubDirective(string content)
    {
        var nlIdx = content.IndexOf('\n');
        var firstLine = (nlIdx >= 0 ? content[..nlIdx] : content).TrimEnd('\r').Trim();
        return firstLine.Equals(":::front", StringComparison.OrdinalIgnoreCase) ||
               firstLine.Equals(":::back", StringComparison.OrdinalIgnoreCase);
    }

    private static string InjectImageIntoPrefencedDirective(string body, string relImg)
    {
        var nlIdx = body.IndexOf('\n');
        var firstLine = nlIdx >= 0 ? body[..nlIdx] : body;
        var rest = nlIdx >= 0 ? body[nlIdx..] : string.Empty;
        var cleanFirst = firstLine.TrimEnd('\r');
        if (cleanFirst.Contains("image=", StringComparison.OrdinalIgnoreCase))
        {
            return body;
        }

        var braceClose = cleanFirst.LastIndexOf('}');
        string updatedFirst;
        if (braceClose > 0)
        {
            var inner = cleanFirst[..braceClose].TrimEnd();
            var sep = inner.EndsWith('{') ? string.Empty : " ";
            updatedFirst = $"{inner}{sep}image=\"{relImg}\"}}";
        }
        else
        {
            updatedFirst = $"{cleanFirst.TrimEnd()}{{image=\"{relImg}\"}}";
        }

        var suffix = firstLine.EndsWith('\r') ? "\r" : string.Empty;
        return $"{updatedFirst}{suffix}{rest}";
    }

    private static string SmartWrapContent(
        OrderedSpecInstance inst,
        string content,
        ref int linkCounter,
        ref bool requiresPlaceholderImg)
    {
        var topic = inst.Topic.Trim().ToLowerInvariant();

        // Already pre-fenced mby directive or standalone Markdown image (or raw JSON) -> pass verbatim.
        if ((content.StartsWith(":::", StringComparison.Ordinal) && !IsInnerSubDirective(content)) ||
            content.StartsWith("{", StringComparison.Ordinal) ||
            (topic == "image" && content.StartsWith("![", StringComparison.Ordinal)))
        {
            return content;
        }

        if (topic == "text")
        {
            return content;
        }

        if (topic == "image")
        {
            if (!content.Contains('\n'))
            {
                return $"![]({FormatMarkdownImageTarget(content)})";
            }

            return content;
        }

        if (topic == "link")
        {
            linkCounter++;
            return $":::link{{id=\"card-{linkCounter}\" title=\"Reference {linkCounter}\"}}\n{content}\n:::";
        }

        string? explicitImage = null;
        if (ImageCapableDirectiveTopics.Contains(topic))
        {
            var m = YamlImageLineRegex().Match(content);
            if (m.Success)
            {
                explicitImage = m.Groups[1].Value.Trim().Replace('\\', '/');
                content = YamlImageLineRegex().Replace(content, string.Empty, 1).Trim();
            }
        }

        var attrs = BuildDefaultDirectiveAttributes(topic, inst.Subtype, explicitImage, ref requiresPlaceholderImg);
        var header = attrs.Count == 0
            ? $":::{topic}"
            : $":::{topic}{{{string.Join(" ", attrs)}}}";
        return $"{header}\n{content}\n:::";
    }

    private static List<string> BuildDefaultDirectiveAttributes(
        string topic,
        string? subtype,
        string? explicitImage,
        ref bool requiresPlaceholderImg)
    {
        var attrs = new List<string>();
        switch (topic)
        {
            case "callout":
                attrs.Add($"tone=\"{(subtype is "pearl" or "clinical" or "warning" ? subtype : "pearl")}\"");
                break;
            case "table":
                if (string.Equals(subtype, "stickyFirstCol", StringComparison.OrdinalIgnoreCase)) attrs.Add("stickyFirstCol=true");
                if (string.Equals(subtype, "hideableCols", StringComparison.OrdinalIgnoreCase)) attrs.Add("hideableCols=\"2\"");
                break;
            case "reveal":
                if (string.Equals(subtype, "showFirst", StringComparison.OrdinalIgnoreCase)) attrs.Add("showFirst=true");
                break;
            case "mcq":
                if (string.Equals(subtype, "multi", StringComparison.OrdinalIgnoreCase)) attrs.Add("multi=true");
                break;
            case "fill-blank":
                if (string.Equals(subtype, "fuzzy", StringComparison.OrdinalIgnoreCase)) attrs.Add("fuzzy=true");
                if (string.Equals(subtype, "caseSensitive", StringComparison.OrdinalIgnoreCase)) attrs.Add("caseSensitive=true");
                break;
            case "assertion-reason":
                attrs.Add($"answer=\"{(string.Equals(subtype, "both-false", StringComparison.OrdinalIgnoreCase) ? "both-false" : "both-explain")}\"");
                if (subtype is "4opt" or "5opt") attrs.Add($"scheme=\"{subtype}\"");
                break;
            case "spot-error":
                if (string.Equals(subtype, "requireFix", StringComparison.OrdinalIgnoreCase)) attrs.Add("requireFix=true");
                if (string.Equals(subtype, "allowNoError", StringComparison.OrdinalIgnoreCase)) attrs.Add("allowNoError=true");
                break;
            case "diff":
                var diffMode = subtype is "fill" or "spot-wrong" or "rearrange" ? subtype : "fill";
                attrs.Add($"mode=\"{diffMode}\"");
                if (string.Equals(subtype, "bank", StringComparison.OrdinalIgnoreCase)) attrs.Add("bank=true");
                break;
            case "relabel":
                if (string.IsNullOrWhiteSpace(explicitImage)) requiresPlaceholderImg = true;
                attrs.Add($"image=\"{(string.IsNullOrWhiteSpace(explicitImage) ? "assets/placeholder.png" : explicitImage)}\"");
                attrs.Add($"mode=\"{(subtype is "reveal" or "bank" or "recall" ? subtype : "bank")}\"");
                attrs.Add($"style=\"{(subtype is "solid" or "blur" or "pixelate" or "whiteout" ? subtype : "blur")}\"");
                break;
            case "diagram-explore":
                if (string.IsNullOrWhiteSpace(explicitImage)) requiresPlaceholderImg = true;
                attrs.Add($"image=\"{(string.IsNullOrWhiteSpace(explicitImage) ? "assets/placeholder.png" : explicitImage)}\"");
                break;
        }

        if (!string.IsNullOrWhiteSpace(explicitImage) && topic is not ("relabel" or "diagram-explore"))
        {
            attrs.Add($"image=\"{explicitImage}\"");
        }

        return attrs;
    }

    private static string BuildImageDirectivePlaceholder(string topic, string? subtype, string relImg, int num)
    {
        var dummy = false;
        var attrs = BuildDefaultDirectiveAttributes(topic, subtype, relImg, ref dummy);
        if (topic == "numeric")
        {
            attrs.Insert(0, "tolerance=\"5%\"");
            attrs.Insert(0, "answer=42");
        }
        else if (topic == "case")
        {
            attrs.Insert(0, $"title=\"Case Vignette {num}\"");
        }

        var header = attrs.Count == 0 ? $":::{topic}" : $":::{topic}{{{string.Join(" ", attrs)}}}";
        var body = topic switch
        {
            "relabel" => "prompt: Identify labels.\nboxes:\n  - label: \"Structure A\"\n    rect: [0.1, 0.1, 0.25, 0.1]",
            "diagram-explore" => "- Landmark A: Placeholder annotation.",
            "mcq" => $"MCQ Placeholder {num}: replace with stem.\n- [x] Correct Option\n- [ ] Distractor Option",
            "flashcard" => $":::front\nFlashcard Front {num}\n:::back\nFlashcard Answer {num}",
            "spot-error" => $"In [context {num}], the value is [~~wrong~~ -> right].",
            "numeric" => $"Numeric question placeholder {num}.",
            "case" => $"Clinical vignette stem placeholder {num}.\n::::mcq\nPrimary finding?\n- [x] Finding A\n- [ ] Finding B\n::::",
            _ => $"Placeholder for {topic} #{num}."
        };
        return $"{header}\n{body}\n:::";
    }

    private static string BuildPlaceholderBody(string topic, string? subtype, int num, ref bool requiresPlaceholderImg)
    {
        var t = topic.Trim().ToLowerInvariant();
        if (t == "text") return $"## Section {num}\n\nPlaceholder prose content.";
        if (t == "image")
        {
            requiresPlaceholderImg = true;
            return $"![Placeholder Figure {num}](assets/placeholder.png \"Figure {num}\")";
        }
        if (t == "link")
        {
            return $":::link{{id=\"card-{num}\" title=\"Reference Card {num}\"}}\nPlaceholder hover note.\n:::";
        }

        return t switch
        {
            "callout" => $":::callout{{tone=\"{(subtype is "pearl" or "clinical" or "warning" ? subtype : "pearl")}\" title=\"Key Point {num}\"}}\nPlaceholder takeaway.\n:::",
            "table" => $":::table{{title=\"Table {num}\"}}\n| Feature | Entity A | Entity B |\n| --- | --- | --- |\n| Property | Value A | Value B |\n:::",
            "diagram-explore" => BuildImagePlaceholderDirective("diagram-explore", $"Interactive Diagram {num}", "- Landmark A: Placeholder annotation.", ref requiresPlaceholderImg),
            "reveal" => $":::reveal{{title=\"Step Reveal {num}\"}}\n> Step 1\nPlaceholder reveal step.\n:::",
            "mcq" => $":::mcq{{{(string.Equals(subtype, "multi", StringComparison.OrdinalIgnoreCase) ? "multi=true" : "")}}}\nMCQ Placeholder {num}: replace with stem.\n- [x] Correct Option\n- [ ] Distractor Option\n:::",
            "flashcard" => $":::flashcard\n:::front\nFlashcard Front {num}\n:::back\nFlashcard Answer {num}\n:::",
            "fill-blank" => $":::fill-blank{{fuzzy=true}}\nPlaceholder term {num}: {{{{answer}}}}.\n:::",
            "assertion-reason" => $":::assertion-reason{{answer=\"both-explain\" scheme=\"{(subtype == "5opt" ? "5opt" : "4opt")}\"}}\nA: Assertion placeholder {num}.\nR: Reason placeholder {num}.\n:::",
            "spot-error" => $":::spot-error\nIn [context {num}], the value is [~~wrong~~ -> right].\n:::",
            "diff" => $":::diff{{mode=\"{(subtype is "spot-wrong" or "rearrange" ? subtype : "fill")}\"}}\n| Property | A | B |\n| --- | --- | --- |\n| Item 1 | {{{{Val A}}}} | {{{{Val B}}}} |\n:::",
            "mechanism" => $":::mechanism{{title=\"Pathway {num}\"}}\n1. Initial trigger\n2. Downstream effect\n:::",
            "relabel" => BuildImagePlaceholderDirective("relabel", $"mode=\"bank\" style=\"blur\"", "prompt: Identify labels.\nboxes:\n  - label: \"Structure A\"\n    rect: [0.1, 0.1, 0.25, 0.1]", ref requiresPlaceholderImg),
            "classify" => $":::classify{{title=\"Classify {num}\"}}\n[Group A]\n- Item 1\n[Group B]\n- Item 2\n:::",
            "match" => $":::match{{title=\"Match {num}\"}}\n- Term 1 :: Definition 1\n- Term 2 :: Definition 2\n:::",
            "numeric" => $":::numeric{{answer=42 tolerance=\"5%\"}}\nNumeric question placeholder {num}.\n:::",
            "case" => $":::case{{title=\"Case Vignette {num}\"}}\nClinical vignette stem placeholder {num}.\n::::mcq\nPrimary finding?\n- [x] Finding A\n- [ ] Finding B\n::::\n::::numeric{{answer=10}}\nExpected value?\n::::\n:::",
            _ => $":::{t}\nPlaceholder for {t} #{num}.\n:::"
        };
    }

    private static string BuildImagePlaceholderDirective(string topic, string extraAttrs, string body, ref bool requiresPlaceholderImg)
    {
        requiresPlaceholderImg = true;
        var attrs = topic == "diagram-explore"
            ? $"image=\"assets/placeholder.png\" title={YamlQuote(extraAttrs)}"
            : $"image=\"assets/placeholder.png\" {extraAttrs}";
        return $":::{topic}{{{attrs}}}\n{body}\n:::";
    }

    private static void CollectReferencedAssets(string markdown, HashSet<string> collected)
    {
        foreach (Match m in MarkdownImageRegex().Matches(markdown))
        {
            var val = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            TryAddRelativeAsset(val, collected);
        }

        foreach (Match m in DirectiveAssetAttrRegex().Matches(markdown))
        {
            TryAddRelativeAsset(m.Groups[1].Value, collected);
        }

        foreach (Match m in YamlAssetLineRegex().Matches(markdown))
        {
            TryAddRelativeAsset(m.Groups[1].Value, collected);
        }
    }

    private static void TryAddRelativeAsset(string raw, HashSet<string> collected)
    {
        var trimmed = raw.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("mby://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("assets/placeholder.png", StringComparison.OrdinalIgnoreCase) ||
            Path.IsPathRooted(trimmed))
        {
            return;
        }

        collected.Add(trimmed.Replace('\\', '/').TrimStart('.', '/'));
    }

    private static IReadOnlyList<PromptComponentPlan> ResolveActiveComponents(PromptDraft draft)
    {
        var comps = draft.Components ?? [];
        if (comps.Count == 0 && draft.Selections.Count > 0)
        {
            var isQuiz = draft.Container == ContainerKind.StandaloneQuiz;
            comps = [new PromptComponentPlan(isQuiz ? "q1" : "a1", isQuiz ? ComponentKind.Quiz : ComponentKind.Article, draft.Title, draft.Selections)];
        }

        return draft.Container switch
        {
            ContainerKind.StandaloneArticle => comps.Where(c => c.Kind == ComponentKind.Article).Take(1).ToList(),
            ContainerKind.StandaloneQuiz => comps.Where(c => c.Kind == ComponentKind.Quiz).Take(1).ToList(),
            _ => comps.ToList()
        };
    }

    private static List<OrderedSpecInstance> ExpandOrderedInstances(PromptComponentPlan comp)
    {
        if (comp.OrderedItems is { Count: > 0 })
        {
            return comp.OrderedItems.ToList();
        }

        return SpecOrderExpander.Expand(comp, useGlobalIndexInId: false);
    }

    private static string YamlQuote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Slugify(string raw, string fallback)
    {
        var chars = raw.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var collapsed = new string(chars);
        while (collapsed.Contains("__", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("__", "_", StringComparison.Ordinal);
        }

        collapsed = collapsed.Trim('_');
        return string.IsNullOrWhiteSpace(collapsed) ? fallback : (collapsed.Length > 28 ? collapsed[..28] : collapsed);
    }
}
