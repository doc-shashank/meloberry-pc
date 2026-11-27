using System.Net;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Domain.Services;

namespace Meloberry.Wrapper.Infrastructure.Server.Endpoints;

/// <summary>Handles `/api/specs`, `/api/prompt/*`, and `/api/presets*` routes.</summary>
public sealed class PromptEndpoints(
    ISettingsStore settingsStore,
    IWrapperDataPaths dataPaths,
    INativeDialogService dialogs,
    LoadSpecCatalogHandler specCatalog,
    PromptHandlers promptHandlers,
    SessionTemplateHandler templateHandler,
    SettingsAndPresetsHandler settingsPresets)
{
    public async Task<bool> TryHandleAsync(
        HttpListenerRequest req,
        HttpListenerResponse resp,
        string method,
        string path,
        CancellationToken ct)
    {
        switch (path, method)
        {
            case ("/api/specs", "GET"):
            {
                var refresh = string.Equals(req.QueryString["refresh"], "true", StringComparison.OrdinalIgnoreCase);
                var (specs, specIcons, isLive, activeMbyPath, hasCliIcons) = await specCatalog.LoadWithIconsAsync(refresh, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteJsonAsync(resp, 200, new
                {
                    specs,
                    specIcons,
                    isLive,
                    hasCliIcons,
                    activeMbyPath
                }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/prompt/compose", "POST"):
            {
                var draft = await HttpEnvelope.ReadJsonAsync<PromptDraft>(req, ct).ConfigureAwait(false);
                if (draft is null)
                {
                    await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid PromptDraft payload.", ct).ConfigureAwait(false);
                    return true;
                }

                var composed = await promptHandlers.ComposeAsync(draft, ct).ConfigureAwait(false);
                var plainText = PromptComposer.ToPlainText(composed.Markdown);
                await HttpEnvelope.WriteJsonAsync(resp, 200, new
                {
                    composed.Markdown,
                    composed.CharacterCount,
                    composed.ApproximateTokens,
                    plainText
                }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/prompt/raw-json", "POST"):
            {
                var draft = await HttpEnvelope.ReadJsonAsync<PromptDraft>(req, ct).ConfigureAwait(false);
                if (draft is null)
                {
                    await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid PromptDraft payload.", ct).ConfigureAwait(false);
                    return true;
                }

                var res = await templateHandler.BuildRawJsonAsync(draft, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/prompt/save-json", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<SaveJsonApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null || string.IsNullOrWhiteSpace(body.RawJson))
                {
                    await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid raw JSON payload.", ct).ConfigureAwait(false);
                    return true;
                }

                var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
                var saveTarget = await dialogs.SaveFileAsync(
                    "Export Raw Session JSON",
                    body.DefaultName ?? "template_session.json",
                    "JSON File (*.json)",
                    "json",
                    settings.ActiveWorkspace?.WorkspacePath,
                    ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(saveTarget))
                {
                    await HttpEnvelope.WriteJsonAsync(resp, 200, new { cancelled = true }, ct).ConfigureAwait(false);
                    return true;
                }

                var res = await promptHandlers.SavePromptAsync(saveTarget, body.RawJson, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/prompt/generate-template", "POST"):
            {
                var draft = await HttpEnvelope.ReadJsonAsync<PromptDraft>(req, ct).ConfigureAwait(false);
                if (draft is null)
                {
                    await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid PromptDraft payload.", ct).ConfigureAwait(false);
                    return true;
                }

                var res = await templateHandler.GenerateDraftTemplateAsync(draft, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/prompt/save", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<SavePromptApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null)
                {
                    await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid save prompt payload.", ct).ConfigureAwait(false);
                    return true;
                }

                var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
                var saveTarget = await dialogs.SaveFileAsync(
                    "Save Prompt Markdown",
                    "PROMPT.md",
                    "Markdown File (*.md)",
                    "md",
                    settings.ActiveWorkspace?.WorkspacePath,
                    ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(saveTarget))
                {
                    await HttpEnvelope.WriteJsonAsync(resp, 200, new { cancelled = true }, ct).ConfigureAwait(false);
                    return true;
                }

                var res = await promptHandlers.SavePromptAsync(saveTarget, body.Markdown ?? string.Empty, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/presets", "GET"):
            {
                var presets = await settingsPresets.GetAllPresetsAsync(ct).ConfigureAwait(false);
                await HttpEnvelope.WriteJsonAsync(resp, 200, new { presets }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/presets", "POST"):
            {
                var preset = await HttpEnvelope.ReadJsonAsync<PromptPreset>(req, ct).ConfigureAwait(false);
                if (preset is null)
                {
                    await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid preset.", ct).ConfigureAwait(false);
                    return true;
                }

                var res = await settingsPresets.SaveCustomPresetAsync(preset, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/presets", "DELETE"):
            {
                var id = req.QueryString["id"] ?? string.Empty;
                var res = await settingsPresets.DeleteCustomPresetAsync(id, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/presets/workflow/save", "POST"):
            {
                var preset = await HttpEnvelope.ReadJsonAsync<PromptPreset>(req, ct).ConfigureAwait(false);
                if (preset is null)
                {
                    await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid preset.", ct).ConfigureAwait(false);
                    return true;
                }

                Directory.CreateDirectory(dataPaths.PresetsDirectory);
                var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
                var initialDir = !string.IsNullOrWhiteSpace(settings.ActiveWorkspace?.WorkspacePath) &&
                    Directory.Exists(settings.ActiveWorkspace.WorkspacePath)
                    ? settings.ActiveWorkspace.WorkspacePath
                    : dataPaths.PresetsDirectory;

                var safeSlug = SlugifyFileName(preset.Name, "workflow_preset");
                var saveTarget = await dialogs.SaveFileAsync(
                    "Save Workflow Preset",
                    $"{safeSlug}.mbypreset",
                    "Meloberry Preset",
                    "mbypreset",
                    initialDir,
                    ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(saveTarget))
                {
                    await HttpEnvelope.WriteJsonAsync(resp, 200, new { cancelled = true }, ct).ConfigureAwait(false);
                    return true;
                }

                var fileNameTitle = Path.GetFileNameWithoutExtension(saveTarget).Replace('_', ' ').Replace('-', ' ').Trim();
                var effectiveName = !string.IsNullOrWhiteSpace(fileNameTitle)
                    ? fileNameTitle
                    : preset.Name;

                var res = await settingsPresets.SaveWorkflowPresetToFileAsync(
                    saveTarget,
                    preset with { Name = effectiveName },
                    ct).ConfigureAwait(false);
                if (res.IsFailure)
                {
                    await HttpEnvelope.WriteErrorAsync(resp, res.Error!, ct).ConfigureAwait(false);
                    return true;
                }

                await HttpEnvelope.WriteJsonAsync(resp, 200, new
                {
                    preset = res.Value.Preset,
                    savedPath = res.Value.SavedPath
                }, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/presets/import-workflow", "POST"):
            {
                Directory.CreateDirectory(dataPaths.PresetsDirectory);
                var files = await dialogs.PickFilesAsync(
                    "Load Workflow Preset",
                    "Meloberry Preset",
                    "*.mbypreset",
                    allowMultiple: false,
                    initialDir: dataPaths.PresetsDirectory,
                    ct).ConfigureAwait(false);
                if (files.Count == 0 || string.IsNullOrWhiteSpace(files[0]))
                {
                    await HttpEnvelope.WriteJsonAsync(resp, 200, new { cancelled = true }, ct).ConfigureAwait(false);
                    return true;
                }

                var res = await settingsPresets.ImportWorkflowPresetFileAsync(files[0], ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/presets/component/save", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<SaveComponentPresetApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null || body.Component is null)
                {
                    await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid component preset payload.", ct).ConfigureAwait(false);
                    return true;
                }

                Directory.CreateDirectory(dataPaths.ComponentPresetsDirectory);
                var rawName = string.IsNullOrWhiteSpace(body.SuggestedName) ? body.Component.Title : body.SuggestedName;
                var safeSlug = SlugifyFileName(rawName, body.Component.Kind.ToString().ToLowerInvariant());
                var saveTarget = await dialogs.SaveFileAsync(
                    "Save Card Preset",
                    $"{safeSlug}.mbypreset",
                    "Meloberry Preset",
                    "mbypreset",
                    dataPaths.ComponentPresetsDirectory,
                    ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(saveTarget))
                {
                    await HttpEnvelope.WriteJsonAsync(resp, 200, new { cancelled = true }, ct).ConfigureAwait(false);
                    return true;
                }

                var res = await settingsPresets.SaveComponentPresetFileAsync(saveTarget, body.Component, rawName, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            case ("/api/presets/component/load", "POST"):
            {
                var body = await HttpEnvelope.ReadJsonAsync<LoadComponentPresetApiRequest>(req, ct).ConfigureAwait(false);
                if (body is null)
                {
                    await HttpEnvelope.WriteJsonErrorAsync(resp, 400, "Request.Invalid", "Invalid component preset load request.", ct).ConfigureAwait(false);
                    return true;
                }

                Directory.CreateDirectory(dataPaths.ComponentPresetsDirectory);
                var files = await dialogs.PickFilesAsync(
                    $"Load {body.ExpectedKind} Preset",
                    "Meloberry Preset",
                    "*.mbypreset",
                    allowMultiple: false,
                    initialDir: dataPaths.ComponentPresetsDirectory,
                    ct).ConfigureAwait(false);
                if (files.Count == 0 || string.IsNullOrWhiteSpace(files[0]))
                {
                    await HttpEnvelope.WriteJsonAsync(resp, 200, new { cancelled = true }, ct).ConfigureAwait(false);
                    return true;
                }

                var res = await settingsPresets.LoadComponentPresetFileAsync(files[0], body.ExpectedKind, ct).ConfigureAwait(false);
                await HttpEnvelope.WriteResultAsync(resp, res, ct).ConfigureAwait(false);
                return true;
            }

            default:
                return false;
        }
    }

    private static string SlugifyFileName(string? raw, string fallback)
    {
        var chars = (raw ?? string.Empty).ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray();
        var collapsed = new string(chars);
        while (collapsed.Contains("__", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("__", "_", StringComparison.Ordinal);
        }

        collapsed = collapsed.Trim('_');
        return string.IsNullOrWhiteSpace(collapsed) ? fallback : (collapsed.Length > 32 ? collapsed[..32] : collapsed);
    }
}
