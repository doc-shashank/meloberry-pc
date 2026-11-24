using Meloberry.Wrapper.Application.Abstractions;

namespace Meloberry.Wrapper.Infrastructure.Workspace;

/// <summary>Resolves %LOCALAPPDATA%\Meloberry\ data directories (or %MBY_WRAPPER_DATA% in tests/sandbox).</summary>
public sealed class WrapperDataPaths : IWrapperDataPaths
{
    public WrapperDataPaths(string? customBaseDir = null)
    {
        var envOverride = Environment.GetEnvironmentVariable("MBY_WRAPPER_DATA");
        var root = !string.IsNullOrWhiteSpace(customBaseDir)
            ? customBaseDir
            : !string.IsNullOrWhiteSpace(envOverride)
                ? envOverride
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Meloberry");

        BaseDirectory = Path.GetFullPath(root);
        AppsDirectory = Path.Combine(BaseDirectory, "apps");
        UpdatesDirectory = Path.Combine(BaseDirectory, "updates");
        WebView2UserDataDirectory = Path.Combine(BaseDirectory, "webview2-userdata");
        SettingsFilePath = Path.Combine(BaseDirectory, "wrapper-settings.json");
    }

    public string BaseDirectory { get; }
    public string AppsDirectory { get; }
    public string UpdatesDirectory { get; }
    public string WebView2UserDataDirectory { get; }
    public string SettingsFilePath { get; }
}
