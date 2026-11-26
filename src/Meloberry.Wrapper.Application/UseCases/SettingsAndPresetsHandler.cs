using System.Text.RegularExpressions;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Updates persisted user settings and custom shopping-list presets.</summary>
public sealed partial class SettingsAndPresetsHandler(ISettingsStore settingsStore)
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
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        return BuiltInPresets.All.Concat(settings.CustomPresets ?? []).ToList();
    }

    public async Task<Result<PromptPreset>> SaveCustomPresetAsync(PromptPreset preset, CancellationToken ct = default)
    {
        var id = string.IsNullOrWhiteSpace(preset.Id) ? $"custom-{Guid.NewGuid():N}"[..16] : preset.Id;
        var clean = preset with { Id = id, IsBuiltIn = false };
        var res = await settingsStore.UpdateAsync(current =>
        {
            var list = (current.CustomPresets ?? [])
                .Where(p => !string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                .Append(clean)
                .ToList();
            return current with { CustomPresets = list };
        }, ct).ConfigureAwait(false);

        return res.IsSuccess ? Result<PromptPreset>.Success(clean) : Result<PromptPreset>.Failure(res.Error!);
    }

    public async Task<Result> DeleteCustomPresetAsync(string presetId, CancellationToken ct = default)
    {
        var res = await settingsStore.UpdateAsync(current =>
        {
            var existing = current.CustomPresets ?? [];
            var remaining = existing.Where(p => !string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (remaining.Count == existing.Count)
            {
                return Result<WrapperSettings>.Failure(Domain.Errors.WrapperErrors.PresetNotFound(presetId));
            }

            return Result<WrapperSettings>.Success(current with { CustomPresets = remaining });
        }, ct).ConfigureAwait(false);

        return res.IsSuccess ? Result.Success() : Result.Failure(res.Error!);
    }
}
