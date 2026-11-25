using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;
using Microsoft.Web.WebView2.Core;

namespace Meloberry.Wrapper.Desktop.Win32;

/// <summary>Hosts CoreWebView2 inside the borderless Win32 HWND client area and handles single-file loader resolution.</summary>
internal sealed class WebView2Host(Win32WindowHost windowHost, IWrapperDataPaths dataPaths) : IDisposable
{
    private static IntPtr _loaderHandle;
    private static int _resolverRegistered;
    private CoreWebView2Controller? _controller;

    public static string EnsureLoaderDllLoaded(IWrapperDataPaths dataPaths)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "WebView2Loader.dll"),
            Path.Combine(AppContext.BaseDirectory, "WebView2Loader.dll")
        };

        var existing = candidates.FirstOrDefault(File.Exists);
        if (existing is null)
        {
            var primaryDir = Path.Combine(dataPaths.BaseDirectory, "runtime", "win-x64");
            var fallbackDir = Path.Combine(Path.GetTempPath(), "Meloberry", "runtime", "win-x64");
            existing = TryExtractLoader(primaryDir) ?? TryExtractLoader(fallbackDir)
                ?? throw new DllNotFoundException("Unable to extract WebView2Loader.dll to runtime cache.");
        }

        if (_loaderHandle == IntPtr.Zero)
        {
            _loaderHandle = NativeLibrary.Load(existing);
        }

        if (Interlocked.CompareExchange(ref _resolverRegistered, 1, 0) == 0)
        {
            NativeLibrary.SetDllImportResolver(
                typeof(CoreWebView2Environment).Assembly,
                static (libraryName, _, _) =>
                    libraryName.Equals("WebView2Loader.dll", StringComparison.OrdinalIgnoreCase) ||
                    libraryName.Equals("WebView2Loader", StringComparison.OrdinalIgnoreCase)
                        ? _loaderHandle
                        : IntPtr.Zero);
        }

        var folder = Path.GetDirectoryName(existing)!;
        CoreWebView2Environment.SetLoaderDllFolderPath(folder);
        return existing;
    }

    private static string? TryExtractLoader(string runtimeDir)
    {
        try
        {
            Directory.CreateDirectory(runtimeDir);
            var targetPath = Path.Combine(runtimeDir, "WebView2Loader.dll");
            ExtractEmbeddedLoaderIfNeeded(targetPath);
            return File.Exists(targetPath) ? targetPath : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task InitializeAsync(string startUrl, string viewerBaseUrl)
    {
        _ = EnsureLoaderDllLoaded(dataPaths);
        Directory.CreateDirectory(dataPaths.WebView2UserDataDirectory);
        var env = await CoreWebView2Environment.CreateAsync(null, dataPaths.WebView2UserDataDirectory);
        _controller = await env.CreateCoreWebView2ControllerAsync(windowHost.Handle);
        _controller.Bounds = windowHost.GetClientBounds();
        _controller.AllowExternalDrop = false;

        var shellAuthority = new Uri(startUrl).GetLeftPart(UriPartial.Authority);
        var viewerAuthority = new Uri(viewerBaseUrl).GetLeftPart(UriPartial.Authority);

        windowHost.Resized += size =>
        {
            if (_controller is not null)
            {
                _controller.Bounds = new System.Drawing.Rectangle(0, 0, size.Width, size.Height);
            }
        };

        windowHost.MaximizedChanged += isMax => PushWindowState(isMax);

        windowHost.FolderDropped += path =>
        {
            if (_controller is not null)
            {
                var jsonPath = JsonSerializer.Serialize(path);
                _ = _controller.CoreWebView2.ExecuteScriptAsync($"window.__onWin32Drop && window.__onWin32Drop({jsonPath});");
            }
        };

        _controller.CoreWebView2.WebMessageReceived += (_, e) => HandleWebMessage(e.TryGetWebMessageAsString());
        _controller.CoreWebView2.NavigationCompleted += (_, _) => PushWindowState(windowHost.IsMaximized);

        _controller.CoreWebView2.NavigationStarting += (_, e) =>
        {
            var allowed = Uri.TryCreate(e.Uri, UriKind.Absolute, out var navUri) &&
                string.Equals(navUri.GetLeftPart(UriPartial.Authority), shellAuthority, StringComparison.OrdinalIgnoreCase);
            if (!allowed)
            {
                e.Cancel = true;
            }
        };

        _controller.CoreWebView2.FrameNavigationStarting += (_, e) =>
        {
            if (string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var allowed = Uri.TryCreate(e.Uri, UriKind.Absolute, out var frameUri) &&
                string.Equals(frameUri.GetLeftPart(UriPartial.Authority), viewerAuthority, StringComparison.OrdinalIgnoreCase) &&
                frameUri.AbsolutePath.StartsWith("/viewer/", StringComparison.OrdinalIgnoreCase);
            if (!allowed)
            {
                e.Cancel = true;
            }
        };

        _controller.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;

        _controller.CoreWebView2.Settings.AreDevToolsEnabled = false;
        _controller.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _controller.CoreWebView2.Settings.AreHostObjectsAllowed = false;
        _controller.CoreWebView2.Navigate(startUrl);
    }

    private void PushWindowState(bool isMaximized)
    {
        if (_controller?.CoreWebView2 is null)
        {
            return;
        }

        var flag = isMaximized ? "true" : "false";
        _ = _controller.CoreWebView2.ExecuteScriptAsync($"window.__onWindowState && window.__onWindowState({{ maximized: {flag} }});");
    }

    private void HandleWebMessage(string? messageJson)
    {
        if (string.IsNullOrWhiteSpace(messageJson))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(messageJson);
            if (!doc.RootElement.TryGetProperty("type", out var typeEl))
            {
                return;
            }

            switch (typeEl.GetString())
            {
                case "window.drag":
                    windowHost.BeginDrag();
                    break;
                case "window.minimize":
                    windowHost.Minimize();
                    break;
                case "window.toggleMaximize":
                    windowHost.ToggleMaximize();
                    break;
                case "window.close":
                    windowHost.RequestShutdown();
                    break;
            }
        }
        catch
        {
            // Ignore malformed messages from page scripts.
        }
    }

    private static void ExtractEmbeddedLoaderIfNeeded(string targetPath)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.Equals("WebView2Loader.dll", StringComparison.OrdinalIgnoreCase) ||
                                 n.EndsWith(".WebView2Loader.dll", StringComparison.OrdinalIgnoreCase));
        if (resName is null)
        {
            throw new FileNotFoundException("Embedded WebView2Loader.dll resource was not found.");
        }

        using var stream = asm.GetManifestResourceStream(resName)!;
        if (File.Exists(targetPath) && new FileInfo(targetPath).Length == stream.Length)
        {
            return;
        }

        var tmp = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = File.Create(tmp))
            {
                stream.CopyTo(fs);
            }

            File.Move(tmp, targetPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { }
            }

            throw;
        }
    }

    public void Dispose()
    {
        _controller?.Close();
        _controller = null;
    }
}
