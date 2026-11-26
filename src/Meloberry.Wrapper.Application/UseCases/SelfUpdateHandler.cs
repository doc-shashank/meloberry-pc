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
