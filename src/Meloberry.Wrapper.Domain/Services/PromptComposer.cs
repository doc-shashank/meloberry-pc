using System.Text;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Domain.Services;

/// <summary>
/// Pure deterministic Markdown prompt composer driving Prompt Studio, Organization blueprints, and fallbacks.
/// </summary>
public static class PromptComposer
{
    public static readonly ShoppingListSelection DefaultFiveMcqFallback = new(
        Topic: "mcq",
        Enabled: true,
        DesiredCount: 5,
        Notes: "Default fallback (no quiz types selected)",
        Modes: ["mixed"]);

    public static string FormatContainerLabel(ContainerKind container) => container switch
    {
        ContainerKind.Session => "Study Session (`Session_*`)",
        ContainerKind.StandaloneArticle => "Standalone Article (`Session_*` single article)",
        ContainerKind.StandaloneQuiz => "Standalone Quiz (`Set_*`)",
        _ => container.ToString()
    };

    public static string FormatAuthoringLabel(AuthoringFormat format) => format switch
    {
        AuthoringFormat.MarkdownDirectory => "Modular Markdown Directory (`_session.md` + numbered `.md` + quiz `.md`)",
        AuthoringFormat.SingleMarkdown => "Single Markdown File",
        AuthoringFormat.Json => "Strict CamelCase JSON (`mby apply -`)",
        _ => format.ToString()
    };

    /// <summary>Filters selections so invalid combinations (e.g., article blocks inside Standalone Quiz) are excluded.</summary>
    public static IReadOnlyList<ShoppingListSelection> FilterSelectionsForContainer(
        ContainerKind container,
        IReadOnlyList<ShoppingListSelection> selections,
        IReadOnlyDictionary<string, SpecTopicItem> catalogByTopic)
    {
        var result = new List<ShoppingListSelection>();
        foreach (var sel in selections)
        {
            if (!sel.Enabled)
            {
                continue;
            }

            if (catalogByTopic.TryGetValue(sel.Topic, out var spec) && !IsAllowedInContainer(container, spec.Category))
            {
                continue;
            }

            result.Add(sel);
        }

        return result;
    }

    public static bool IsAllowedInContainer(ContainerKind container, string category)
    {
        var norm = category.Trim().ToLowerInvariant();
        return container switch
        {
            ContainerKind.Session => true,
            ContainerKind.StandaloneArticle => norm is "block" or "container",
            ContainerKind.StandaloneQuiz => norm is "question" or "container",
            _ => true
        };
    }

    public static bool IsAllowedInComponent(ComponentKind kind, string category)
    {
        var norm = category.Trim().ToLowerInvariant();
        return kind == ComponentKind.Article ? norm == "block" : norm == "question";
    }

    /// <summary>Returns effective components, enforcing component scoping and 5-MCQ fallback for empty quizzes.</summary>
    public static IReadOnlyList<PromptComponentPlan> ResolveEffectiveComponents(
        PromptDraft draft,
        IReadOnlyDictionary<string, SpecTopicItem> catalogByTopic)
    {
        if (draft.Components is { Count: > 0 })
        {
            var resolved = new List<PromptComponentPlan>();
            foreach (var comp in draft.Components)
            {
                if (draft.Container == ContainerKind.StandaloneArticle && comp.Kind != ComponentKind.Article)
                {
                    continue;
                }

                if (draft.Container == ContainerKind.StandaloneQuiz && comp.Kind != ComponentKind.Quiz)
                {
                    continue;
                }

                var validSelections = comp.Selections
                    .Where(s => s.Enabled && (!catalogByTopic.TryGetValue(s.Topic, out var meta) || IsAllowedInComponent(comp.Kind, meta.Category)))
                    .ToList();

                if (comp.Kind == ComponentKind.Quiz && validSelections.Count == 0)
                {
                    validSelections.Add(DefaultFiveMcqFallback);
                }

                resolved.Add(comp with { Selections = validSelections });
            }

            if (resolved.Count > 0)
            {
                return resolved;
            }
        }

        // Legacy flat Selections compatibility + StandaloneQuiz 5-MCQ fallback.
        var active = FilterSelectionsForContainer(draft.Container, draft.Selections, catalogByTopic).ToList();
        if (draft.Container == ContainerKind.StandaloneQuiz && active.Count == 0)
        {
            active.Add(DefaultFiveMcqFallback);
        }

        if (draft.Container == ContainerKind.StandaloneArticle)
        {
            return [new PromptComponentPlan("a1", ComponentKind.Article, draft.Title, active)];
        }

        if (draft.Container == ContainerKind.StandaloneQuiz)
        {
            return [new PromptComponentPlan("q1", ComponentKind.Quiz, draft.Title, active)];
        }

        var articleSel = active.Where(s => catalogByTopic.TryGetValue(s.Topic, out var m) && m.Category.Equals("block", StringComparison.OrdinalIgnoreCase)).ToList();
        var quizSel = active.Where(s => catalogByTopic.TryGetValue(s.Topic, out var m) && m.Category.Equals("question", StringComparison.OrdinalIgnoreCase)).ToList();
        var list = new List<PromptComponentPlan>();
        if (articleSel.Count > 0)
        {
            list.Add(new PromptComponentPlan("a1", ComponentKind.Article, "Study Article", articleSel));
        }
        if (quizSel.Count > 0)
        {
            list.Add(new PromptComponentPlan("q1", ComponentKind.Quiz, "Practice Quiz", quizSel));
        }
        if (list.Count == 0)
        {
            list.Add(new PromptComponentPlan("a1", ComponentKind.Article, "Study Article", []));
        }

        return list;
    }

    public static ComposedPrompt Compose(PromptDraft draft, IReadOnlyList<SpecTopicItem> catalog)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(catalog);

        var catalogByTopic = catalog.ToDictionary(s => s.Topic, StringComparer.OrdinalIgnoreCase);
        var components = ResolveEffectiveComponents(draft, catalogByTopic);
        var allSelections = components.SelectMany(c => c.Selections).ToList();

        var sb = new StringBuilder();
        var title = string.IsNullOrWhiteSpace(draft.Title) ? "Untitled Study Module" : draft.Title.Trim();
        var mbyInvocation = FormatCliInvocation(draft.MbyExecutablePath, draft.WorkingDirectory, "mby");
        var melomgInvocation = FormatCliInvocation(draft.MelomgExecutablePath, draft.WorkingDirectory, "melomg");

        sb.AppendLine($"# Meloberry Authoring Task: {title}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(draft.WorkingDirectory))
        {
            sb.AppendLine($"- **Working Directory**: `{draft.WorkingDirectory}`");
        }

        sb.AppendLine($"- **Target Container**: {FormatContainerLabel(draft.Container)}");
        sb.AppendLine($"- **Recommended Format**: {FormatAuthoringLabel(draft.AuthoringFormat)}");
        sb.AppendLine($"- **Title / Name**: `{title}`");
        if (!string.IsNullOrWhiteSpace(draft.Description))
        {
            sb.AppendLine($"- **Description**: {draft.Description.Trim()}");
        }

        sb.AppendLine($"- **`mby` CLI**: `{mbyInvocation}` (run with `$env:MBY_AGENT='1'` or `--json`)");
        if (draft.UseMelomgForPdfs)
        {
            sb.AppendLine($"- **`melomg` CLI**: `{melomgInvocation}`");
        }

        sb.AppendLine();
        sb.AppendLine("## Organization & Component Blueprint");
        sb.AppendLine();

        var articleNum = 0;
        var quizNum = 0;
        for (var i = 0; i < components.Count; i++)
        {
            var comp = components[i];
            var badge = comp.Kind == ComponentKind.Article ? $"A{++articleNum}" : $"Q{++quizNum}";
            var compTitle = string.IsNullOrWhiteSpace(comp.Title) ? comp.Kind.ToString() : comp.Title.Trim();
            sb.AppendLine($"### {i + 1}. [{badge}] {comp.Kind}: {compTitle}");
            sb.AppendLine();

            if (comp.Selections.Count == 0)
            {
                sb.AppendLine("_Use clear structure suited to this component._");
            }
            else
            {
                sb.AppendLine("| Spec Topic | Qty | Modes | Instructions |");
                sb.AppendLine("| :--- | :--- | :--- | :--- |");
                foreach (var item in comp.Selections)
                {
                    var qty = item.DesiredCount is > 0 ? item.DesiredCount.Value.ToString() : "Auto";
                    var modes = item.Modes is { Count: > 0 }
                        ? string.Join(", ", item.Modes.Select(m => $"`{m}`"))
                        : "—";
                    var notes = string.IsNullOrWhiteSpace(item.Notes) ? "—" : item.Notes.Trim().Replace("|", "\\|");
                    sb.AppendLine($"| `{item.Topic}` | {qty} | {modes} | {notes} |");
                }
            }

            if (comp.OrderedItems is { Count: > 0 })
            {
                var seq = string.Join(" -> ", comp.OrderedItems.Select(o => $"`{o.Topic} #{o.InstanceNumber}`"));
                sb.AppendLine();
                sb.AppendLine($"- **Exact Sequence**: {seq}");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Schema & Playbook Contract");
        sb.AppendLine();

        if (draft.AgentsMdExported)
        {
            sb.AppendLine("- **Workspace `AGENTS.md` is ready**: read `./AGENTS.md` directly—exact tool paths, lint rules, and live schemas are already embedded there, so do not waste turns running `prime` or `spec`.");
        }
        else if (draft.LazyCliDiscovery)
        {
            var topicsToQuery = allSelections
                .Select(s => s.Topic)
                .Where(t => !string.Equals(t, SpecCatalogDefaults.VirtualLinkTopicName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var topicArgs = topicsToQuery.Count > 0 ? string.Join(" ", topicsToQuery) : "session article quiz";
            sb.AppendLine($"- **Lazy CLI Discovery**: Before authoring, inspect exact schemas via `{mbyInvocation} spec {topicArgs} --json` (and `{mbyInvocation} prime --json` if needed). Never guess field names.");
        }

        if (draft.EmbedInlineSchemas && allSelections.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Embedded Compact Signatures");
            foreach (var topic in allSelections.Select(s => s.Topic).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (catalogByTopic.TryGetValue(topic, out var spec) && !string.IsNullOrWhiteSpace(spec.CompactSignature))
                {
                    sb.AppendLine($"- **`{spec.Topic}`** ({spec.Command}):");
                    sb.AppendLine("```text");
                    sb.AppendLine(spec.CompactSignature.Trim());
                    sb.AppendLine("```");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Source Study Material");
        sb.AppendLine();

        var hasAttachments = draft.Attachments.Count > 0 || (draft.CopiedFolders is { Count: > 0 });
        if (draft.Attachments.Count > 0)
        {
            sb.AppendLine("### Source Files");
            foreach (var att in draft.Attachments)
            {
                var rel = ToWorkspaceRelativeDisplay(att.FilePath, draft.WorkingDirectory);
                sb.AppendLine($"- `{rel}` ({att.Kind})");
            }

            sb.AppendLine();
        }

        if (draft.CopiedFolders is { Count: > 0 })
        {
            sb.AppendLine("### Copied Workspace Subfolders");
            foreach (var folder in draft.CopiedFolders)
            {
                sb.AppendLine($"- `./{folder}/`");
            }

            sb.AppendLine();
        }

        var hasRelabel = allSelections.Any(s => string.Equals(s.Topic, "relabel", StringComparison.OrdinalIgnoreCase));

        if (draft.UseMelomgForPdfs)
        {
            sb.AppendLine("### PDF Figure & Label Masking Workflow (`melomg`)");
            sb.AppendLine($"1. `{melomgInvocation} ingest <pdfPath> --retain-db --offline --quiet` -> inspect `anomalies[]`/`suspectPages[]`.");
            sb.AppendLine($"2. Extract visual figures/diagrams/charts (tables/equations excluded): `{melomgInvocation} extract <docId> all -o ./assets --quality high --manifest --quiet`.");
            if (hasRelabel)
            {
                sb.AppendLine($"3. For relabeling/visual flashcards, export masked versions (`-o ./assets/masked --mask-all` or `--mask-match \"...\"`) and unmasked answers (`-o ./assets/ans`).");
            }

            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(draft.SourceText))
        {
            sb.AppendLine("### Study Notes");
            sb.AppendLine("```text");
            sb.AppendLine(draft.SourceText.Trim());
            sb.AppendLine("```");
            sb.AppendLine();
        }
        else if (!hasAttachments)
        {
            sb.AppendLine("_No raw study notes pasted; synthesize high-yield material matching the title and description._");
            sb.AppendLine();
        }

        sb.AppendLine("## Verification & Deliverables");
        sb.AppendLine($"1. Author strict valid content in `{FormatAuthoringLabel(draft.AuthoringFormat)}`.");
        sb.AppendLine($"2. Validate and compile to `./dist_web`: `{mbyInvocation} build <input> -o ./dist_web --verify --json`.");
        sb.AppendLine($"3. Verify headless telemetry (`{mbyInvocation} analyze ./dist_web --headless --json`) and export `.mbypkg` if persisting (`{mbyInvocation} pkg export <wsId> android ./out.mbypkg --json`).");

        var markdown = sb.ToString().TrimEnd() + Environment.NewLine;
        return new ComposedPrompt(markdown, markdown.Length, AgentsMdComposer.EstimateTokens(markdown));
    }

    /// <summary>Converts composed Markdown prompt to clean readable plain text.</summary>
    public static string ToPlainText(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var outLines = new List<string>(lines.Length);
        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("| :--", StringComparison.Ordinal))
            {
                continue;
            }

            var line = rawLine;
            if (trimmed.StartsWith('#'))
            {
                line = trimmed.TrimStart('#').Trim();
            }
            else if (trimmed.StartsWith('|') && trimmed.EndsWith('|'))
            {
                var cells = trimmed.Trim('|').Split('|').Select(c => c.Trim());
                line = string.Join(" | ", cells);
            }

            line = line.Replace("**", string.Empty, StringComparison.Ordinal)
                       .Replace("`", string.Empty, StringComparison.Ordinal);
            outLines.Add(line);
        }

        return string.Join(Environment.NewLine, outLines).TrimEnd() + Environment.NewLine;
    }

    private static string FormatCliInvocation(string? exePath, string? workingDir, string fallbackName)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return fallbackName;
        }

        if (!string.IsNullOrWhiteSpace(workingDir))
        {
            var fullExe = Path.GetFullPath(exePath);
            var fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDir)) + Path.DirectorySeparatorChar;
            if (fullExe.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase))
            {
                return ".\\" + Path.GetRelativePath(workingDir, fullExe);
            }
        }

        return exePath;
    }

    private static string ToWorkspaceRelativeDisplay(string filePath, string? workingDir)
    {
        if (string.IsNullOrWhiteSpace(workingDir) || string.IsNullOrWhiteSpace(filePath))
        {
            return filePath;
        }

        try
        {
            var fullFile = Path.GetFullPath(filePath);
            var fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDir)) + Path.DirectorySeparatorChar;
            if (fullFile.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase))
            {
                return "./" + Path.GetRelativePath(workingDir, fullFile).Replace('\\', '/');
            }
        }
        catch
        {
            // Keep original path on malformed inputs.
        }

        return filePath;
    }
}
