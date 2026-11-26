using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;

namespace Meloberry.Wrapper.UnitTests;

internal sealed class NoopShutdown : IAppShutdownCoordinator
{
    public void RequestShutdown() { }
    public void RequestShutdownAndLaunch(string executablePath, string arguments) { }
}

internal sealed class NoopDialogs : INativeDialogService
{
    public Task<string?> PickFolderAsync(string title, string? initialDir = null, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> PickFilesAsync(
        string title,
        string filterLabel,
        string filterPattern,
        bool allowMultiple = true,
        string? initialDir = null,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string?> SaveFileAsync(
        string title,
        string defaultFileName,
        string filterLabel,
        string filterExtension,
        string? initialDir = null,
        CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}

internal sealed class NoopLauncher : IExternalUrlLauncher
{
    public Result OpenAllowlistedUrl(string url) => Result.Success();
    public Result OpenFolder(string folderPath) => Result.Success();
}
