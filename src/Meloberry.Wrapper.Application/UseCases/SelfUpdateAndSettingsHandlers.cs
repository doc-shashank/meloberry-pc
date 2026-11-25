using System.Text.RegularExpressions;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Application.UseCases;

/// <summary>Checks GitHub Releases for newer Inno Setup installers and coordinates clean self-updates.</summary>
public sealed class SelfUpdateHandler(
    IGitHubSelfUpdater updater,
    IWrapperMetadataProvider metadataProvider,
    ISettingsStore settingsStore,
    IAppShutdownCoordinator shutdownCoordinator)
{
    private AppUpdateCheckResult? _lastCheck;
    private DownloadProgressSnapshot? _lastProgress;

    public DownloadProgressSnapshot? GetStatus() => _lastProgress;

    public async Task<Result<AppUpdateCheckResult>> CheckAsync(CancellationToken ct = default)
    {
        var meta = metadataProvider.GetMetadata();
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var owner = string.IsNullOrWhiteSpace(settings.SelfUpdateRepoOwner) ? meta.SelfUpdateRepoOwner : settings.SelfUpdateRepoOwner!;
        var repo = string.IsNullOrWhiteSpace(settings.SelfUpdateRepoName) ? meta.SelfUpdateRepoName : settings.SelfUpdateRepoName!;

        _lastProgress = new DownloadProgressSnapshot("self-update", "checking", $"Checking {owner}/{repo} for updates...");
        var check = await updater.CheckForUpdateAsync(owner, repo, meta.Version, ct).ConfigureAwait(false);
        if (check.IsFailure)
        {
            _lastProgress = new DownloadProgressSnapshot("self-update", "error", check.Error!.Description, IsCompleted: true, IsError: true);
            return check;
        }

        _lastCheck = check.Value;
        _lastProgress = new DownloadProgressSnapshot("self-update", "checked", check.Value.StatusMessage, IsCompleted: true);
        return check;
    }

    public async Task<Result<string>> DownloadAndInstallAsync(CancellationToken ct = default)
    {
        var meta = metadataProvider.GetMetadata();
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var owner = string.IsNullOrWhiteSpace(settings.SelfUpdateRepoOwner) ? meta.SelfUpdateRepoOwner : settings.SelfUpdateRepoOwner!;
        var repo = string.IsNullOrWhiteSpace(settings.SelfUpdateRepoName) ? meta.SelfUpdateRepoName : settings.SelfUpdateRepoName!;
        var expectedSlug = $"{owner}/{repo}";

        if (_lastCheck is null ||
            !_lastCheck.UpdateAvailable ||
            !string.Equals(_lastCheck.RepoSlug, expectedSlug, StringComparison.OrdinalIgnoreCase))
        {
            var recheck = await CheckAsync(ct).ConfigureAwait(false);
            if (recheck.IsFailure)
            {
                return Result<string>.Failure(recheck.Error!);
            }

            _lastCheck = recheck.Value;
        }

        if (!_lastCheck.UpdateAvailable || string.IsNullOrWhiteSpace(_lastCheck.DownloadUrl))
        {
            return Result<string>.Failure(new Error("Update.NoneAvailable", _lastCheck.StatusMessage, ErrorType.Conflict));
        }

        _lastProgress = new DownloadProgressSnapshot("self-update", "downloading", $"Downloading {_lastCheck.AssetFileName}...", 0, _lastCheck.SizeBytes);
        var dl = await updater.DownloadInstallerAsync(_lastCheck, snap => _lastProgress = snap, ct).ConfigureAwait(false);
        if (dl.IsFailure)
        {
            _lastProgress = new DownloadProgressSnapshot("self-update", "error", dl.Error!.Description, IsCompleted: true, IsError: true);
            return dl;
        }

        if (!File.Exists(dl.Value))
        {
            var missingErr = new Error("Update.InstallerMissing", $"Installer executable not found: {dl.Value}", ErrorType.Io);
            _lastProgress = new DownloadProgressSnapshot("self-update", "error", missingErr.Description, IsCompleted: true, IsError: true);
            return Result<string>.Failure(missingErr);
        }

        _lastProgress = new DownloadProgressSnapshot("self-update", "launching", "Launching Inno Setup updater and closing Meloberry PC...", IsCompleted: true);
        shutdownCoordinator.RequestShutdownAndLaunch(dl.Value, "/SP- /SILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS");
        return Result<string>.Success(dl.Value);
    }
}

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

        var current = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var theme = !string.IsNullOrWhiteSpace(themeMode) && AllowedThemes.Contains(themeMode)
            ? themeMode.ToLowerInvariant()
            : current.ThemeMode;

        var updated = current with
        {
            ThemeMode = theme,
            MbyExecutableOverride = mbyOverride?.Trim() is { Length: > 0 } m ? m : null,
            MelomgExecutableOverride = melomgOverride?.Trim() is { Length: > 0 } ml ? ml : null,
            DefaultBuildOutputDir = defaultBuildDir?.Trim() is { Length: > 0 } bd ? bd : null,
            SelfUpdateRepoOwner = owner,
            SelfUpdateRepoName = repo,
            OverrideWebAppTheme = overrideWebAppTheme ?? current.OverrideWebAppTheme
        };

        var res = await settingsStore.SaveAsync(updated, ct).ConfigureAwait(false);
        return res.IsSuccess ? Result<WrapperSettings>.Success(updated) : Result<WrapperSettings>.Failure(res.Error!);
    }

    public async Task<Result<WrapperSettings>> UpdateOverrideWebAppThemeAsync(bool overrideWebAppTheme, CancellationToken ct = default)
    {
        var current = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var updated = current with { OverrideWebAppTheme = overrideWebAppTheme };
        var res = await settingsStore.SaveAsync(updated, ct).ConfigureAwait(false);
        return res.IsSuccess ? Result<WrapperSettings>.Success(updated) : Result<WrapperSettings>.Failure(res.Error!);
    }

    public async Task<IReadOnlyList<PromptPreset>> GetAllPresetsAsync(CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        return BuiltInPresets.All.Concat(settings.CustomPresets ?? []).ToList();
    }

    public async Task<Result<PromptPreset>> SaveCustomPresetAsync(PromptPreset preset, CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var id = string.IsNullOrWhiteSpace(preset.Id) ? $"custom-{Guid.NewGuid():N}"[..16] : preset.Id;
        var clean = preset with { Id = id, IsBuiltIn = false };
        var list = (settings.CustomPresets ?? [])
            .Where(p => !string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
            .Append(clean)
            .ToList();

        var res = await settingsStore.SaveAsync(settings with { CustomPresets = list }, ct).ConfigureAwait(false);
        return res.IsSuccess ? Result<PromptPreset>.Success(clean) : Result<PromptPreset>.Failure(res.Error!);
    }

    public async Task<Result> DeleteCustomPresetAsync(string presetId, CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var existing = settings.CustomPresets ?? [];
        var remaining = existing.Where(p => !string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (remaining.Count == existing.Count)
        {
            return Result.Failure(Domain.Errors.WrapperErrors.PresetNotFound(presetId));
        }

        return await settingsStore.SaveAsync(settings with { CustomPresets = remaining }, ct).ConfigureAwait(false);
    }
}
