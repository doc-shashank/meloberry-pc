using System.Text.RegularExpressions;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Updates persisted user settings and custom `.mbypreset` workflow/component presets.</summary>
public sealed partial class SettingsAndPresetsHandler(
    ISettingsStore settingsStore,
    IPresetFileStore presetFileStore)
{
    private static readonly HashSet<string> AllowedThemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "berry", "light", "dark", "paper"
    };

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,98}[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubIdentifierRegex();

    public static bool IsValidGitHubIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return !trimmed.Contains("..", StringComparison.Ordinal) && GitHubIdentifierRegex().IsMatch(trimmed);
    }

    public async Task<WrapperSettings> GetSettingsAsync(CancellationToken ct = default) =>
        await settingsStore.LoadAsync(ct).ConfigureAwait(false);

    public async Task<Result<WrapperSettings>> UpdateSettingsAsync(
        string? themeMode,
        string? mbyOverride,
        string? melomgOverride,
        string? defaultBuildDir,
        string? selfUpdateOwner,
        string? selfUpdateRepo,
        bool? overrideWebAppTheme = null,
        CancellationToken ct = default)
    {
        var owner = selfUpdateOwner?.Trim() is { Length: > 0 } o ? o : null;
        var repo = selfUpdateRepo?.Trim() is { Length: > 0 } r ? r : null;
        if ((owner is not null && !IsValidGitHubIdentifier(owner)) ||
            (repo is not null && !IsValidGitHubIdentifier(repo)))
        {
            return Result<WrapperSettings>.Failure(new Error(
                "Settings.InvalidGitHubRepo",
                "GitHub owner and repository names may only contain letters, numbers, '.', '-', or '_'.",
                ErrorType.Validation));
        }

        return await settingsStore.UpdateAsync(current =>
        {
            var theme = !string.IsNullOrWhiteSpace(themeMode) && AllowedThemes.Contains(themeMode)
                ? themeMode.ToLowerInvariant()
                : current.ThemeMode;

            return current with
            {
                ThemeMode = theme,
                MbyExecutableOverride = mbyOverride?.Trim() is { Length: > 0 } m ? m : null,
                MelomgExecutableOverride = melomgOverride?.Trim() is { Length: > 0 } ml ? ml : null,
                DefaultBuildOutputDir = defaultBuildDir?.Trim() is { Length: > 0 } bd ? bd : null,
                SelfUpdateRepoOwner = owner,
                SelfUpdateRepoName = repo,
                OverrideWebAppTheme = overrideWebAppTheme ?? current.OverrideWebAppTheme
            };
        }, ct).ConfigureAwait(false);
    }

    public async Task<Result<WrapperSettings>> UpdateOverrideWebAppThemeAsync(bool overrideWebAppTheme, CancellationToken ct = default) =>
        await settingsStore.UpdateAsync(current => current with { OverrideWebAppTheme = overrideWebAppTheme }, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<PromptPreset>> GetAllPresetsAsync(CancellationToken ct = default)
    {
        var filePresets = await presetFileStore.LoadWorkflowPresetsAsync(ct).ConfigureAwait(false);
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);

        var fileIds = filePresets.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var legacyExtras = (settings.CustomPresets ?? [])
            .Where(p => !fileIds.Contains(p.Id))
            .Select(p => p with { IsBuiltIn = false });

        return BuiltInPresets.All
            .Concat(filePresets)
            .Concat(legacyExtras)
            .ToList();
    }

    public async Task<Result<PromptPreset>> SaveCustomPresetAsync(PromptPreset preset, CancellationToken ct = default)
    {
        var id = string.IsNullOrWhiteSpace(preset.Id) ? $"custom-{Guid.NewGuid():N}"[..16] : preset.Id.Trim();
        var clean = preset with { Id = id, IsBuiltIn = false };
        return await presetFileStore.SaveWorkflowPresetAsync(clean, ct).ConfigureAwait(false);
    }

    public async Task<Result<(PromptPreset Preset, string SavedPath)>> SaveWorkflowPresetToFileAsync(
        string filePath,
        PromptPreset preset,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Result<(PromptPreset, string)>.Failure(WrapperErrors.InvalidPresetFile("Target preset file path is empty."));
        }

        var fallbackName = Path.GetFileNameWithoutExtension(filePath).Replace('_', ' ').Replace('-', ' ').Trim();
        var cleanName = !string.IsNullOrWhiteSpace(preset.Name)
            ? preset.Name.Trim()
            : (string.IsNullOrWhiteSpace(fallbackName) ? "Custom Preset" : fallbackName);
        var id = string.IsNullOrWhiteSpace(preset.Id) ? $"custom-{Guid.NewGuid():N}"[..16] : preset.Id.Trim();
        var clean = preset with { Id = id, Name = cleanName, IsBuiltIn = false };

        return await presetFileStore.SaveWorkflowPresetToFileAsync(filePath, clean, ct).ConfigureAwait(false);
    }

    public async Task<Result> DeleteCustomPresetAsync(string presetId, CancellationToken ct = default)
    {
        var fileRes = await presetFileStore.DeleteWorkflowPresetAsync(presetId, ct).ConfigureAwait(false);

        var removedFromLegacy = false;
        var legacyRes = await settingsStore.UpdateAsync(current =>
        {
            var existing = current.CustomPresets ?? [];
            var remaining = existing.Where(p => !string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (remaining.Count == existing.Count)
            {
                return current;
            }

            removedFromLegacy = true;
            return current with { CustomPresets = remaining };
        }, ct).ConfigureAwait(false);

        if (legacyRes.IsFailure)
        {
            return Result.Failure(legacyRes.Error!);
        }

        if (fileRes.IsSuccess || removedFromLegacy)
        {
            return Result.Success();
        }

        return fileRes;
    }

    public async Task<Result<PromptPreset>> ImportWorkflowPresetFileAsync(string filePath, CancellationToken ct = default)
    {
        var readRes = await presetFileStore.ReadPresetFileAsync(filePath, ct).ConfigureAwait(false);
        if (readRes.IsFailure)
        {
            return Result<PromptPreset>.Failure(readRes.Error!);
        }

        var file = readRes.Value;
        if (file.Scope != PresetFileScope.Workflow || file.WorkflowPreset is null)
        {
            return Result<PromptPreset>.Failure(WrapperErrors.InvalidPresetFile(
                "Selected preset file is not a whole-workflow preset."));
        }

        var imported = file.WorkflowPreset with
        {
            Id = string.IsNullOrWhiteSpace(file.WorkflowPreset.Id)
                ? $"custom-{Guid.NewGuid():N}"[..16]
                : file.WorkflowPreset.Id,
            Name = string.IsNullOrWhiteSpace(file.WorkflowPreset.Name) ? file.Name : file.WorkflowPreset.Name,
            IsBuiltIn = false
        };

        return await SaveCustomPresetAsync(imported, ct).ConfigureAwait(false);
    }

    public async Task<Result<string>> SaveComponentPresetFileAsync(
        string filePath,
        PromptComponentPlan component,
        string? name = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(component);
        var effectiveName = !string.IsNullOrWhiteSpace(name)
            ? name.Trim()
            : (!string.IsNullOrWhiteSpace(component.Title) ? component.Title.Trim() : $"{component.Kind} Preset");

        var envelope = new MbyPresetFile(
            FormatVersion: 1,
            Scope: PresetFileScope.Component,
            Name: effectiveName,
            Description: $"Saved {component.Kind} component preset",
            ComponentPreset: component with { Title = effectiveName });

        return await presetFileStore.WritePresetFileAsync(filePath, envelope, ct).ConfigureAwait(false);
    }

    public async Task<Result<PromptComponentPlan>> LoadComponentPresetFileAsync(
        string filePath,
        ComponentKind expectedKind,
        CancellationToken ct = default)
    {
        var readRes = await presetFileStore.ReadPresetFileAsync(filePath, ct).ConfigureAwait(false);
        if (readRes.IsFailure)
        {
            return Result<PromptComponentPlan>.Failure(readRes.Error!);
        }

        var file = readRes.Value;
        if (file.Scope != PresetFileScope.Component || file.ComponentPreset is null)
        {
            return Result<PromptComponentPlan>.Failure(WrapperErrors.InvalidPresetFile(
                "Selected preset file is not a single-component (Article/Quiz) preset."));
        }

        if (file.ComponentPreset.Kind != expectedKind)
        {
            return Result<PromptComponentPlan>.Failure(WrapperErrors.InvalidPresetFile(
                $"Selected preset is a '{file.ComponentPreset.Kind}' preset and cannot be loaded into a '{expectedKind}' card."));
        }

        return Result<PromptComponentPlan>.Success(file.ComponentPreset);
    }
}
