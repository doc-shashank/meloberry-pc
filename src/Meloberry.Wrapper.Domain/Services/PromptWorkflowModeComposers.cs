using System.Text;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Domain.Services;

/// <summary>Deterministic renderers for Direct (Build Readiness) and LiveAgent workflow modes.</summary>
internal static class PromptWorkflowModeComposers
{
    public static ComposedPrompt ComposeDirectReadiness(
        PromptDraft draft,
        IReadOnlyList<PromptComponentPlan> components)
    {
        var title = string.IsNullOrWhiteSpace(draft.Title) ? "Untitled Study Module" : draft.Title.Trim();
        var sb = new StringBuilder();
        var (totalSpecs, filledSpecs, emptySpecs) = CountSpecReadiness(components);

        sb.AppendLine($"# Build Readiness: {title}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(draft.WorkingDirectory))
        {
            sb.AppendLine($"- **Active Workspace**: `{draft.WorkingDirectory}`");
        }

        sb.AppendLine($"- **Target Container**: {PromptComposer.FormatContainerLabel(draft.Container)}");
        sb.AppendLine($"- **Blueprint**: {components.Count} components, {totalSpecs} specs ({filledSpecs} filled, {emptySpecs} empty)");
        sb.AppendLine($"- **Placeholders on Finalize**: {(draft.AllowPlaceholdersOnFinalize ? "Allowed" : "Blocked (fill every empty spec before finalizing)")}");
        sb.AppendLine();
        sb.AppendLine("## Component Checklist");
        sb.AppendLine();

        var articleNum = 0;
        var quizNum = 0;
        for (var i = 0; i < components.Count; i++)
        {
            var comp = components[i];
            var badge = comp.Kind == ComponentKind.Article ? $"A{++articleNum}" : $"Q{++quizNum}";
            var compTitle = string.IsNullOrWhiteSpace(comp.Title) ? comp.Kind.ToString() : comp.Title.Trim();
            sb.AppendLine($"### {i + 1}. [{badge}] {comp.Kind}: {compTitle}");

            if (comp.OrderedItems is { Count: > 0 })
            {
                foreach (var item in comp.OrderedItems)
                {
                    var label = string.IsNullOrWhiteSpace(item.Subtype)
                        ? $"`{item.Topic} #{item.InstanceNumber}`"
                        : $"`{item.Topic}:{item.Subtype} #{item.InstanceNumber}`";
                    var status = FormatContentBadge(item.Topic, item.Content);
                    sb.AppendLine($"- {label} (`{item.Id}`): **{status}**");
                }
            }
            else if (comp.Selections.Count > 0)
            {
                foreach (var sel in comp.Selections)
                {
                    var qty = sel.DesiredCount is > 0 ? sel.DesiredCount.Value.ToString() : "1";
                    sb.AppendLine($"- `{sel.Topic}` x{qty}: **Empty**");
                }
            }
            else
            {
                sb.AppendLine("- _No specs added yet._");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Next Action");
        sb.AppendLine();
        sb.AppendLine("Click **Finalize & Build WebApp** in Review (or run `.\\mby-pc.bat finalize --json`) to assemble modular Markdown, compile via `mby batch -`, and launch Step 5 Viewer.");

        var markdown = sb.ToString().TrimEnd() + Environment.NewLine;
        return new ComposedPrompt(markdown, markdown.Length, AgentsMdComposer.EstimateTokens(markdown));
    }

    public static ComposedPrompt ComposeLiveAgentPrompt(
        PromptDraft draft,
        IReadOnlyList<PromptComponentPlan> components)
    {
        var title = string.IsNullOrWhiteSpace(draft.Title) ? "Untitled Study Module" : draft.Title.Trim();
        var sb = new StringBuilder();
        var (totalSpecs, filledSpecs, emptySpecs) = CountSpecReadiness(components);

        sb.AppendLine($"# Live Agent Session: {title}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(draft.WorkingDirectory))
        {
            sb.AppendLine($"- **Workspace Root**: `{draft.WorkingDirectory}`");
        }

        sb.AppendLine($"- **Target Container**: {PromptComposer.FormatContainerLabel(draft.Container)}");
        sb.AppendLine($"- **Live Readiness**: {components.Count} components, {totalSpecs} specs ({filledSpecs} filled, {emptySpecs} empty)");
        if (!string.IsNullOrWhiteSpace(draft.Description))
        {
            sb.AppendLine($"- **Description**: {draft.Description.Trim()}");
        }

        sb.AppendLine();
        sb.AppendLine("## Live GUI Control Protocol (`mby-pc.bat`)");
        sb.AppendLine();
        sb.AppendLine("Drive Meloberry PC visually so the user sees every step live in the desktop app:");
        sb.AppendLine("1. **Inspect & Discover**:");
        sb.AppendLine("   - `.\\mby-pc.bat prime --json` | `.\\mby-pc.bat status --json` | `.\\mby-pc.bat draft get --json`");
        sb.AppendLine("2. **Configure Metadata (Step 2 - Format)**:");
        sb.AppendLine("   - `.\\mby-pc.bat ui view format --json`");
        sb.AppendLine("   - `.\\mby-pc.bat draft patch --title \"...\" --desc \"...\" --container session|article|quiz --json`");
        sb.AppendLine("3. **Organize Cards & Specs (Step 3 - Organize)**:");
        sb.AppendLine("   - `.\\mby-pc.bat ui view organize --json`");
        sb.AppendLine("   - `.\\mby-pc.bat draft comp add <article|quiz> --title \"...\" --json` | `draft comp rename <compId> --title \"...\"` | `move <compId> <idx>` | `rm <compId>`");
        sb.AppendLine("   - `.\\mby-pc.bat draft spec add <compId> <topic> [--subtype S] [--qty N] --json` | `draft spec update <specId> [--subtype S] [--notes \"...\"]` | `move <specId> <idx>` | `rm <specId>`");
        sb.AppendLine("4. **Populate Spec Content Live**:");
        sb.AppendLine("   - Text/Directives: `.\\mby-pc.bat draft spec set-content <specId> --raw \"...\" --json` (or `--file ./notes.md`)");
        sb.AppendLine("   - Figures/Images: extract via `.\\mby-pc.bat melomg extract <pdf> \"Figure 1.1\" -o ./assets --retain-db --offline --json`, then attach via `.\\mby-pc.bat draft spec set-content <specId> --image assets/fig.png --caption \"...\" --json`");
        sb.AppendLine("5. **Compile & Open Viewer (Step 4 -> Step 5)**:");
        sb.AppendLine("   - `.\\mby-pc.bat finalize --json` (builds via `mby batch -` and opens Step 5 Viewer automatically)");
        sb.AppendLine();
        sb.AppendLine("> **Rule:** Prefer `draft` + `finalize` over calling `mby apply`/`mby build` directly so the GUI updates in real time.");
        sb.AppendLine();
        sb.AppendLine("## Current Organized Components & Specs");
        sb.AppendLine();

        var articleNum = 0;
        var quizNum = 0;
        for (var i = 0; i < components.Count; i++)
        {
            var comp = components[i];
            var badge = comp.Kind == ComponentKind.Article ? $"A{++articleNum}" : $"Q{++quizNum}";
            var compTitle = string.IsNullOrWhiteSpace(comp.Title) ? comp.Kind.ToString() : comp.Title.Trim();
            sb.AppendLine($"### {i + 1}. [{badge}] {comp.Kind}: {compTitle} (`{comp.Id}`)");

            if (comp.OrderedItems is { Count: > 0 })
            {
                foreach (var item in comp.OrderedItems)
                {
                    var subtypeTag = string.IsNullOrWhiteSpace(item.Subtype) ? string.Empty : $":{item.Subtype}";
                    var contentStatus = FormatContentBadge(item.Topic, item.Content);
                    sb.AppendLine($"- `{item.Id}` -> `{item.Topic}{subtypeTag} #{item.InstanceNumber}` — **{contentStatus}**");
                }
            }
            else if (comp.Selections.Count > 0)
            {
                foreach (var sel in comp.Selections)
                {
                    sb.AppendLine($"- `{sel.Topic}` x{sel.DesiredCount ?? 1} — **Empty**");
                }
            }

            sb.AppendLine();
        }

        if (draft.Attachments.Count > 0 || (draft.CopiedFolders is { Count: > 0 }) || !string.IsNullOrWhiteSpace(draft.SourceText) || draft.UseMelomgForPdfs)
        {
            sb.AppendLine("## Study Material & Tools");
            sb.AppendLine();
            foreach (var att in draft.Attachments)
            {
                sb.AppendLine($"- `{PromptComposer.ToWorkspaceRelativeDisplay(att.FilePath, draft.WorkingDirectory)}` ({att.Kind})");
            }

            if (draft.CopiedFolders is { Count: > 0 })
            {
                foreach (var folder in draft.CopiedFolders)
                {
                    sb.AppendLine($"- `./{folder}/`");
                }
            }

            if (draft.UseMelomgForPdfs)
            {
                sb.AppendLine("- Use `.\\mby-pc.bat melomg extract <pdfOrDocId> \"Figure X.Y\" -o ./assets --retain-db --offline --json` for PDF figures/labels.");
            }

            if (!string.IsNullOrWhiteSpace(draft.SourceText))
            {
                var fence = AgentsMdComposer.MakeCodeFence(draft.SourceText);
                sb.AppendLine();
                sb.AppendLine("### Notes");
                sb.AppendLine($"{fence}text");
                sb.AppendLine(draft.SourceText.Trim());
                sb.AppendLine(fence);
            }
        }

        var markdown = sb.ToString().TrimEnd() + Environment.NewLine;
        return new ComposedPrompt(markdown, markdown.Length, AgentsMdComposer.EstimateTokens(markdown));
    }

    private static (int Total, int Filled, int Empty) CountSpecReadiness(IReadOnlyList<PromptComponentPlan> components)
    {
        var total = 0;
        var filled = 0;
        foreach (var comp in components)
        {
            if (comp.OrderedItems is { Count: > 0 })
            {
                foreach (var item in comp.OrderedItems)
                {
                    total++;
                    if (IsContentFilled(item.Topic, item.Content))
                    {
                        filled++;
                    }
                }
            }
            else if (comp.Selections.Count > 0)
            {
                total += comp.Selections.Sum(s => Math.Max(1, s.DesiredCount ?? 1));
            }
        }

        return (total, filled, Math.Max(0, total - filled));
    }

    private static bool IsContentFilled(string topic, SpecContentSource? content) =>
        content is not null && content.Mode switch
        {
            SpecContentMode.RawText => !string.IsNullOrWhiteSpace(content.RawText),
            SpecContentMode.MarkdownFile => !string.IsNullOrWhiteSpace(content.MarkdownFilePath),
            SpecContentMode.ImageFile => !string.IsNullOrWhiteSpace(content.ImagePath) &&
                                         (string.Equals(topic, "image", StringComparison.OrdinalIgnoreCase) ||
                                          !string.IsNullOrWhiteSpace(content.RawText)),
            _ => false
        };

    private static string FormatContentBadge(string topic, SpecContentSource? content) =>
        content?.Mode switch
        {
            SpecContentMode.RawText when !string.IsNullOrWhiteSpace(content.RawText) => "RawText",
            SpecContentMode.MarkdownFile when !string.IsNullOrWhiteSpace(content.MarkdownFilePath) => $"MarkdownFile (`{content.MarkdownFilePath}`)",
            SpecContentMode.ImageFile when !string.IsNullOrWhiteSpace(content.ImagePath) &&
                                           (string.Equals(topic, "image", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(content.RawText))
                => $"Image (`{content.ImagePath}`)",
            SpecContentMode.ImageFile when !string.IsNullOrWhiteSpace(content.ImagePath)
                => $"Image only — needs body (`{content.ImagePath}`)",
            _ => "Empty"
        };
}
