using System.Text;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Domain.Services;

/// <summary>
/// Composes Wrapper-specific workspace context (staged files/folders, custom executables,
/// live `mby-pc.bat` protocol, and `melomg` guidance) appended to `mby prime --format agents-md`.
/// </summary>
public static class WrapperAgentsAppendixComposer
{
    public static string ComposeAppendix(WrapperAgentsAppendixInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var sb = new StringBuilder();
        var melomgCmd = input.UsesWorkspaceShim
            ? ".\\mby-pc.bat melomg"
            : FormatCliCommand(input.WorkspaceMelomgFileName, "melomg");

        var hasWorkspaceExtras = input.ExtraWorkspaceExecutables.Any(x => !string.IsNullOrWhiteSpace(x))
            || input.CopiedFiles.Count > 0
            || input.CopiedFolders.Count > 0
            || (input.ExternalAttachments is { Count: > 0 });

        if (hasWorkspaceExtras)
        {
            sb.AppendLine("## Wrapper Staged Inputs & Custom Tools");
            sb.AppendLine();
            sb.AppendLine($"- **Workspace Root**: `{input.WorkspacePath}`");

            foreach (var extraExe in input.ExtraWorkspaceExecutables.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                sb.AppendLine($"- **Additional Tool**: `{FormatCliCommand(extraExe, extraExe)}`");
            }

            if (input.CopiedFiles.Count > 0)
            {
                sb.AppendLine("- **Workspace Files (root)**: " + string.Join(", ", input.CopiedFiles.Select(f => $"`./{f}`")));
            }

            if (input.CopiedFolders.Count > 0)
            {
                sb.AppendLine("- **Workspace Subfolders**: " + string.Join(", ", input.CopiedFolders.Select(f => $"`./{f}/`")));
            }

            if (input.ExternalAttachments is { Count: > 0 })
            {
                sb.AppendLine("- **External Reference Files**: " + string.Join(", ", input.ExternalAttachments.Select(f => $"`{f}`")));
            }

            sb.AppendLine();
        }

        if (input.WorkflowMode == WorkflowMode.LiveAgent)
        {
            sb.AppendLine("## Live GUI Control Protocol (`mby-pc.bat`)");
            sb.AppendLine();
            sb.AppendLine("1. **Configure Metadata (Step 2 - Details)**: `.\\mby-pc.bat ui view details --json` -> `.\\mby-pc.bat draft patch --title \"...\" --desc \"...\" --container session|article|quiz --json`");
            sb.AppendLine("2. **Organize Cards & Specs (Step 3 - Organize)**: `.\\mby-pc.bat ui view organize --json` -> `.\\mby-pc.bat draft comp add|rename|move|rm` & `.\\mby-pc.bat draft spec add|update|clone|move|move-to|rm`");
            sb.AppendLine("3. **Populate Spec Content Live**: `.\\mby-pc.bat draft spec set-content <specId> --raw \"...\" --json` (or `--file ./notes.md` or `--image assets/fig.png --caption \"...\"`)");
            sb.AppendLine("4. **Compile & Open Viewer (Step 4 -> Step 5)**: `.\\mby-pc.bat finalize --json` (builds via `mby batch -` and opens Step 5 Viewer automatically)");
            sb.AppendLine("> **Rule:** Prefer `draft` + `finalize` over calling `mby apply`/`mby build` directly so the GUI updates in real time.");
            sb.AppendLine();
        }
        else if (input.UsesWorkspaceShim)
        {
            sb.AppendLine("## Live Workspace Control (`mby-pc.bat`)");
            sb.AppendLine();
            sb.AppendLine("- `.\\mby-pc.bat status --json`, `.\\mby-pc.bat draft get --json`, `.\\mby-pc.bat draft spec set-content <specId> --raw \"...\" --json`, `.\\mby-pc.bat finalize --json`.");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(input.MelomgSpecsText) || !string.IsNullOrWhiteSpace(input.WorkspaceMelomgFileName))
        {
            sb.AppendLine("## `melomg` PDF Extraction & Label Masking Reference");
            sb.AppendLine();
            sb.AppendLine($"- **`melomg` CLI**: `{melomgCmd}` (categories: `figure`, `diagram`, `chart`; pass `--offline` when offline and `--retain-db` before `extract`/`mask`).");
            sb.AppendLine($"- **Workflow**: `{melomgCmd} ingest ./source.pdf --retain-db --offline --quiet` -> `{melomgCmd} extract <docId> all -o ./assets --quality high --manifest --quiet`.");
            if (!string.IsNullOrWhiteSpace(input.MelomgSpecsText))
            {
                var melFence = TokenEstimator.MakeCodeFence(input.MelomgSpecsText);
                sb.AppendLine($"{melFence}text");
                sb.AppendLine(input.MelomgSpecsText.Trim());
                sb.AppendLine(melFence);
            }
            else
            {
                sb.AppendLine($"Run `{melomgCmd} spec extract mask --json` if needed for full parameter flags.");
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    public static string CombineWithBaseAgentsMd(string baseAgentsMd, WrapperAgentsAppendixInput appendixInput)
    {
        var baseText = (baseAgentsMd ?? string.Empty).TrimEnd();
        var appendix = ComposeAppendix(appendixInput);
        if (string.IsNullOrWhiteSpace(appendix))
        {
            return baseText + Environment.NewLine;
        }

        return string.IsNullOrWhiteSpace(baseText)
            ? appendix + Environment.NewLine
            : $"{baseText}{Environment.NewLine}{Environment.NewLine}{appendix}{Environment.NewLine}";
    }

    public static string FormatCliCommand(string? pathOrFileName, string fallback)
    {
        if (string.IsNullOrWhiteSpace(pathOrFileName))
        {
            return fallback;
        }

        var trimmed = pathOrFileName.Trim();
        if (trimmed.StartsWith(".\\", StringComparison.Ordinal) || trimmed.StartsWith("./", StringComparison.Ordinal))
        {
            return trimmed;
        }

        if (Path.IsPathRooted(trimmed))
        {
            return trimmed.Contains(' ') ? $"& \"{trimmed}\"" : trimmed;
        }

        return $".\\{trimmed}";
    }
}
