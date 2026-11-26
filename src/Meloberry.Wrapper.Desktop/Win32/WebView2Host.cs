using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;
using Microsoft.Web.WebView2.Core;

namespace Meloberry.Wrapper.Desktop.Win32;

/// <summary>Hosts CoreWebView2 inside the borderless Win32 HWND client area and handles single-file loader resolution.</summary>
internal sealed class WebView2Host(
    Win32WindowHost windowHost,
    IWrapperDataPaths dataPaths,
    IAtomicFileWriter atomicFileWriter) : IDisposable
{
    private static IntPtr _loaderHandle;
    private static int _resolverRegistered;
    private CoreWebView2Controller? _controller;

    public static string EnsureLoaderDllLoaded(IWrapperDataPaths dataPaths, IAtomicFileWriter atomicFileWriter)
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
            existing = TryExtractLoader(primaryDir, atomicFileWriter) ?? TryExtractLoader(fallbackDir, atomicFileWriter)
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

    private static string? TryExtractLoader(string runtimeDir, IAtomicFileWriter atomicFileWriter)
    {
        try
        {
            Directory.CreateDirectory(runtimeDir);
            var targetPath = Path.Combine(runtimeDir, "WebView2Loader.dll");
            ExtractEmbeddedLoaderIfNeeded(targetPath, atomicFileWriter);
            return File.Exists(targetPath) ? targetPath : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task InitializeAsync(string startUrl, string viewerBaseUrl)
    {
        _ = EnsureLoaderDllLoaded(dataPaths, atomicFileWriter);
        Directory.CreateDirectory(dataPaths.WebView2UserDataDirectory);
        var env = await CoreWebView2Environment.CreateAsync(null, dataPaths.WebView2UserDataDirectory);
        _controller = await env.CreateCoreWebView2ControllerAsync(windowHost.Handle);
        _controller.DefaultBackgroundColor = windowHost.InitialWebViewBackgroundColor;
        _controller.Bounds = windowHost.GetWebViewBounds();
        _controller.AllowExternalDrop = true;

        var shellAuthority = new Uri(startUrl).GetLeftPart(UriPartial.Authority);
        var viewerAuthority = new Uri(viewerBaseUrl).GetLeftPart(UriPartial.Authority);

        windowHost.Resized += bounds =>
        {
            if (_controller is not null)
            {
                _controller.Bounds = bounds;
            }
        };

        windowHost.MaximizedChanged += isMax => PushWindowState(isMax);

        windowHost.FilesDropped += paths => DispatchNativeDrop(paths, view: null, target: null);

        _controller.CoreWebView2.WebMessageReceived += (_, e) => HandleWebMessage(e);
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

    private void HandleWebMessage(CoreWebView2WebMessageReceivedEventArgs args)
    {
        string? messageJson = null;
        try
        {
            messageJson = args.TryGetWebMessageAsString();
        }
        catch
        {
            messageJson = args.WebMessageAsJson;
        }

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
                case "native-drop":
                {
                    var view = doc.RootElement.TryGetProperty("view", out var vEl) && vEl.ValueKind == JsonValueKind.String
                        ? vEl.GetString()
                        : null;
                    var target = doc.RootElement.TryGetProperty("target", out var tEl) && tEl.ValueKind == JsonValueKind.String
                        ? tEl.GetString()
                        : null;
                    var paths = new List<string>();
                    if (args.AdditionalObjects is { Count: > 0 })
                    {
                        foreach (var obj in args.AdditionalObjects)
                        {
                            if (obj is CoreWebView2File file && !string.IsNullOrWhiteSpace(file.Path))
                            {
                                paths.Add(file.Path);
                            }
                        }
                    }

                    if (paths.Count > 0)
                    {
                        DispatchNativeDrop(paths, view, target);
                    }
                    break;
                }
            }
        }
        catch
        {
            // Ignore malformed messages from page scripts.
        }
    }

    private void DispatchNativeDrop(IReadOnlyList<string> paths, string? view, string? target)
    {
        if (_controller?.CoreWebView2 is null || paths.Count == 0)
        {
            return;
        }

        var items = new List<object>(paths.Count);
        foreach (var rawPath in paths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            var kind = Directory.Exists(rawPath)
                ? "folder"
                : (File.Exists(rawPath) ? "file" : null);
            if (kind is not null)
            {
                items.Add(new { path = rawPath, kind });
            }
        }

        if (items.Count == 0)
        {
            return;
        }

        var payloadJson = JsonSerializer.Serialize(new { view, target, items });
        _ = _controller.CoreWebView2.ExecuteScriptAsync($"window.__onNativeDrop && window.__onNativeDrop({payloadJson});");
    }

    private static void ExtractEmbeddedLoaderIfNeeded(string targetPath, IAtomicFileWriter atomicFileWriter)
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

        atomicFileWriter.WriteStream(targetPath, stream);
    }

    public void Dispose()
    {
        _controller?.Close();
        _controller = null;
    }
}
