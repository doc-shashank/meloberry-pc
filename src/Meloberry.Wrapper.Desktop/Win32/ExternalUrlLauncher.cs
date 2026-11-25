using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Desktop.Win32;

/// <summary>Launches allowlisted Meloberry HTTPS links and Windows Explorer folder windows via ShellExecuteW.</summary>
internal sealed class ExternalUrlLauncher(Win32WindowHost windowHost) : IExternalUrlLauncher
{
    public Result OpenAllowlistedUrl(string url)
    {
        if (!EcosystemCatalog.TryValidateAllowlistedUrl(url, out var uri) || uri is null)
        {
            return Result.Failure(WrapperErrors.InvalidUrl(url));
        }

        var res = Win32Native.ShellExecuteW(windowHost.Handle, "open", uri.AbsoluteUri, null, null, Win32Native.SW_SHOW);
        return res.ToInt64() > 32
            ? Result.Success()
            : Result.Failure(WrapperErrors.IoFailure($"ShellExecuteW failed to open URL '{url}'."));
    }

    public Result OpenFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return Result.Failure(WrapperErrors.InvalidPath(folderPath));
        }

        string full;
        try
        {
            full = Path.GetFullPath(folderPath.Trim());
        }
        catch
        {
            return Result.Failure(WrapperErrors.InvalidPath(folderPath));
        }

        if (!Directory.Exists(full))
        {
            return Result.Failure(WrapperErrors.InvalidWorkspaceDir(full));
        }

        var res = Win32Native.ShellExecuteW(windowHost.Handle, "open", "explorer.exe", $"\"{full}\"", null, Win32Native.SW_SHOW);
        return res.ToInt64() > 32
            ? Result.Success()
            : Result.Failure(WrapperErrors.IoFailure($"Failed to open Explorer for '{full}'."));
    }
}
