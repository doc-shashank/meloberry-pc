using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Desktop.Win32;

/// <summary>Posts continuations onto the STA Win32 window message pump.</summary>
internal sealed class Win32SynchronizationContext(Win32WindowHost host) : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) =>
        host.EnqueueCallback(() => d(state));
}

/// <summary>Owns the borderless ExtendContent Win32 HWND window, placement persistence, DPI awareness, and shutdown.</summary>
internal sealed class Win32WindowHost : IAppShutdownCoordinator
{
    public const string WindowClassName = "MeloberryPcWrapperWindowClass";

    private readonly ConcurrentQueue<Action> _uiQueue = new();
    private readonly Win32Native.WndProc _wndProcDelegate;
    private IntPtr _hwnd;
    private IntPtr _backgroundBrush;
    private bool _initialMaximized = true;
    private volatile bool _isDestroyed;

    public event Action<Rectangle>? Resized;
    public event Action<bool>? MaximizedChanged;
    public event Action<IReadOnlyList<string>>? FilesDropped;

    public Win32WindowHost()
    {
        _wndProcDelegate = WindowProc;
    }

    public IntPtr Handle => _hwnd;
    public bool IsMaximized => _hwnd != IntPtr.Zero && Win32Native.IsZoomed(_hwnd);
    public (string FileName, string Arguments)? PendingPostShutdownLaunch { get; private set; }
    public WindowPlacementState? LastCapturedPlacement { get; private set; }
    public Color InitialWebViewBackgroundColor { get; private set; } = Color.FromArgb(0xFD, 0xF6, 0xF9);

    public void Create(
        string title,
        string themeMode = "berry",
        WindowPlacementState? savedPlacement = null,
        int width = 1320,
        int height = 860)
    {
        Win32Native.SetProcessDpiAwarenessContext(Win32Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        var (surfaceColorRef, webBgColor, isDarkTheme) = ResolveThemeColors(themeMode);
        InitialWebViewBackgroundColor = webBgColor;
        _backgroundBrush = Win32Native.CreateSolidBrush(surfaceColorRef);

        var hInstance = Win32Native.GetModuleHandleW(null);
        var appIcon = Win32Native.LoadImageW(
            hInstance,
            new IntPtr(Win32Native.IDI_APPLICATION),
            Win32Native.IMAGE_ICON,
            0,
            0,
            Win32Native.LR_DEFAULTSIZE | Win32Native.LR_SHARED);
        if (appIcon == IntPtr.Zero)
        {
            appIcon = Win32Native.LoadIconW(IntPtr.Zero, new IntPtr(Win32Native.IDI_APPLICATION));
        }

        var arrowCursor = Win32Native.LoadCursorW(IntPtr.Zero, new IntPtr(Win32Native.IDC_ARROW));

        var wc = new Win32Native.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Win32Native.WNDCLASSEXW>(),
            style = 0x0003,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInstance,
            hIcon = appIcon,
            hIconSm = appIcon,
            hCursor = arrowCursor,
            hbrBackground = _backgroundBrush,
            lpszClassName = WindowClassName
        };
        Win32Native.RegisterClassExW(ref wc);

        var (x, y, winW, winH, startMaximized) = ResolveInitialPlacement(savedPlacement, width, height);
        _initialMaximized = startMaximized;

        _hwnd = Win32Native.CreateWindowExW(
            0,
            WindowClassName,
            title,
            Win32Native.WS_OVERLAPPEDWINDOW,
            x,
            y,
            winW,
            winH,
            IntPtr.Zero,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            if (_backgroundBrush != IntPtr.Zero)
            {
                Win32Native.DeleteObject(_backgroundBrush);
                _backgroundBrush = IntPtr.Zero;
            }

            throw new InvalidOperationException("Failed to create Win32 host window.");
        }

        var useDark = isDarkTheme ? 1 : 0;
        _ = Win32Native.DwmSetWindowAttribute(_hwnd, Win32Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));

        var margins = new Win32Native.MARGINS { cxLeftWidth = 0, cxRightWidth = 0, cyTopHeight = 1, cyBottomHeight = 0 };
        _ = Win32Native.DwmExtendFrameIntoClientArea(_hwnd, ref margins);

        Win32Native.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            Win32Native.SWP_FRAMECHANGED | Win32Native.SWP_NOMOVE | Win32Native.SWP_NOSIZE | Win32Native.SWP_NOZORDER);

        Win32Native.DragAcceptFiles(_hwnd, true);
    }

    public void Show()
    {
        Win32Native.ShowWindow(_hwnd, _initialMaximized ? Win32Native.SW_SHOWMAXIMIZED : Win32Native.SW_SHOWNORMAL);
        Win32Native.UpdateWindow(_hwnd);
    }

    public void BeginDrag()
    {
        EnqueueCallback(() =>
        {
            if (_hwnd != IntPtr.Zero)
            {
                Win32Native.ReleaseCapture();
                _ = Win32Native.SendMessageW(_hwnd, Win32Native.WM_NCLBUTTONDOWN, new IntPtr(Win32Native.HTCAPTION), IntPtr.Zero);
            }
        });
    }

    public void Minimize()
    {
        EnqueueCallback(() =>
        {
            if (_hwnd != IntPtr.Zero)
            {
                Win32Native.ShowWindow(_hwnd, Win32Native.SW_MINIMIZE);
            }
        });
    }

    public void ToggleMaximize()
    {
        EnqueueCallback(() =>
        {
            if (_hwnd != IntPtr.Zero)
            {
                var cmd = Win32Native.IsZoomed(_hwnd) ? Win32Native.SW_RESTORE : Win32Native.SW_SHOWMAXIMIZED;
                Win32Native.ShowWindow(_hwnd, cmd);
            }
        });
    }

    public Rectangle GetWebViewBounds()
    {
        if (_hwnd == IntPtr.Zero || !Win32Native.GetClientRect(_hwnd, out var rect))
        {
            return new Rectangle(0, 0, 1280, 800);
        }

        var w = Math.Max(1, rect.right - rect.left);
        var h = Math.Max(1, rect.bottom - rect.top);
        if (Win32Native.IsZoomed(_hwnd))
        {
            return new Rectangle(0, 0, w, h);
        }

        var topInset = GetTopResizeBorderPx(Win32Native.GetDpiForWindow(_hwnd));
        return new Rectangle(0, topInset, w, Math.Max(1, h - topInset));
    }

    public void EnqueueCallback(Action action)
    {
        if (_isDestroyed)
        {
            return;
        }

        _uiQueue.Enqueue(action);
        var hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
        {
            _ = Win32Native.PostMessageW(hwnd, Win32Native.WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public Task<T> InvokeOnUiThreadAsync<T>(Func<T> func, CancellationToken ct = default)
    {
        if (_isDestroyed || _hwnd == IntPtr.Zero)
        {
            return Task.FromException<T>(new ObjectDisposedException(nameof(Win32WindowHost)));
        }

        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(ct);
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ctr = ct.Register(() => tcs.TrySetCanceled(ct));
        _ = tcs.Task.ContinueWith(
            static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
            ctr,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _uiQueue.Enqueue(() =>
        {
            if (_isDestroyed)
            {
                tcs.TrySetException(new ObjectDisposedException(nameof(Win32WindowHost)));
                return;
            }

            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }

            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        var hwnd = _hwnd;
        if (_isDestroyed || hwnd == IntPtr.Zero || !Win32Native.PostMessageW(hwnd, Win32Native.WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero))
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(Win32WindowHost)));
        }

        return tcs.Task;
    }

    public void RequestShutdown()
    {
        EnqueueCallback(() =>
        {
            if (_hwnd != IntPtr.Zero)
            {
                Win32Native.DestroyWindow(_hwnd);
            }
            else
            {
                Win32Native.PostQuitMessage(0);
            }
        });
    }

    public void RequestShutdownAndLaunch(string executablePath, string arguments)
    {
        PendingPostShutdownLaunch = (executablePath, arguments);
        _ = Task.Delay(250).ContinueWith(_ => RequestShutdown(), TaskScheduler.Default);
    }

    public void RunMessageLoop()
    {
        while (Win32Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32Native.TranslateMessage(ref msg);
            Win32Native.DispatchMessageW(ref msg);
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Native.WM_NCCALCSIZE:
                if (wParam != IntPtr.Zero && lParam != IntPtr.Zero)
                {
                    var nc = Marshal.PtrToStructure<Win32Native.NCCALCSIZE_PARAMS>(lParam);
                    var dpi = Win32Native.GetDpiForWindow(hWnd);
                    var frameX = GetFrameX(dpi);
                    var frameY = GetFrameY(dpi);

                    nc.rgrc0.left += frameX;
                    nc.rgrc0.right -= frameX;
                    nc.rgrc0.bottom -= frameY;
                    if (Win32Native.IsZoomed(hWnd))
                    {
                        nc.rgrc0.top += frameY;
                    }

                    Marshal.StructureToPtr(nc, lParam, fDeleteOld: false);
                    return IntPtr.Zero;
                }

                return Win32Native.DefWindowProcW(hWnd, msg, wParam, lParam);

            case Win32Native.WM_NCHITTEST:
                if (!Win32Native.IsZoomed(hWnd) && Win32Native.GetWindowRect(hWnd, out var winRect))
                {
                    var ptX = unchecked((short)((long)lParam & 0xFFFF));
                    var ptY = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
                    var dpi = Win32Native.GetDpiForWindow(hWnd);
                    var borderX = GetFrameX(dpi);
                    var borderY = Math.Max(GetFrameY(dpi), GetTopResizeBorderPx(dpi));

                    if (ptY >= winRect.top && ptY < winRect.top + borderY && ptX >= winRect.left && ptX < winRect.right)
                    {
                        if (ptX < winRect.left + borderX)
                        {
                            return new IntPtr(Win32Native.HTTOPLEFT);
                        }

                        if (ptX >= winRect.right - borderX)
                        {
                            return new IntPtr(Win32Native.HTTOPRIGHT);
                        }

                        return new IntPtr(Win32Native.HTTOP);
                    }
                }

                return Win32Native.DefWindowProcW(hWnd, msg, wParam, lParam);

            case Win32Native.WM_GETMINMAXINFO:
                if (lParam != IntPtr.Zero)
                {
                    var mmi = Marshal.PtrToStructure<Win32Native.MINMAXINFO>(lParam);
                    var dpi = Win32Native.GetDpiForWindow(hWnd);
                    var scale = (dpi > 0 ? dpi : 96u) / 96.0;
                    mmi.ptMinTrackSize.x = (int)Math.Round(640.0 * scale);
                    mmi.ptMinTrackSize.y = (int)Math.Round(480.0 * scale);
                    Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
                    return IntPtr.Zero;
                }

                return Win32Native.DefWindowProcW(hWnd, msg, wParam, lParam);

            case Win32Native.WM_APP_INVOKE:
                while (_uiQueue.TryDequeue(out var act))
                {
                    act();
                }

                return IntPtr.Zero;

            case Win32Native.WM_SIZE:
                Resized?.Invoke(GetWebViewBounds());
                MaximizedChanged?.Invoke(Win32Native.IsZoomed(hWnd));
                return IntPtr.Zero;

            case Win32Native.WM_DROPFILES:
                HandleDropFiles(wParam);
                return IntPtr.Zero;

            case Win32Native.WM_CLOSE:
                CaptureWindowPlacement(hWnd);
                return Win32Native.DefWindowProcW(hWnd, msg, wParam, lParam);

            case Win32Native.WM_DESTROY:
                CaptureWindowPlacement(hWnd);
                _isDestroyed = true;
                _hwnd = IntPtr.Zero;
                while (_uiQueue.TryDequeue(out var pending))
                {
                    try { pending(); } catch { }
                }

                if (_backgroundBrush != IntPtr.Zero)
                {
                    Win32Native.DeleteObject(_backgroundBrush);
                    _backgroundBrush = IntPtr.Zero;
                }

                Win32Native.PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return Win32Native.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    private void HandleDropFiles(IntPtr hDrop)
    {
        try
        {
            var count = Win32Native.DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
            if (count > 0)
            {
                var paths = new List<string>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    var len = Win32Native.DragQueryFileW(hDrop, i, null, 0);
                    var buf = new char[len + 1];
                    _ = Win32Native.DragQueryFileW(hDrop, i, buf, (uint)buf.Length);
                    var path = new string(buf, 0, (int)len);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        paths.Add(path);
                    }
                }

                if (paths.Count > 0)
                {
                    FilesDropped?.Invoke(paths);
                }
            }
        }
        finally
        {
            Win32Native.DragFinish(hDrop);
        }
    }

    private void CaptureWindowPlacement(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        var wp = new Win32Native.WINDOWPLACEMENT
        {
            length = (uint)Marshal.SizeOf<Win32Native.WINDOWPLACEMENT>()
        };
        if (!Win32Native.GetWindowPlacement(hWnd, ref wp))
        {
            return;
        }

        var normal = wp.rcNormalPosition;
        var w = Math.Max(480, normal.right - normal.left);
        var h = Math.Max(360, normal.bottom - normal.top);
        LastCapturedPlacement = new WindowPlacementState(
            X: normal.left,
            Y: normal.top,
            Width: w,
            Height: h,
            IsMaximized: Win32Native.IsZoomed(hWnd));
    }

    private static (int X, int Y, int Width, int Height, bool StartMaximized) ResolveInitialPlacement(
        WindowPlacementState? saved,
        int defaultWidth,
        int defaultHeight)
    {
        if (saved is { Width: >= 480, Height: >= 360 })
        {
            var candidate = new Win32Native.RECT
            {
                left = saved.X,
                top = saved.Y,
                right = saved.X + saved.Width,
                bottom = saved.Y + saved.Height
            };

            var onMonitor = Win32Native.MonitorFromRect(ref candidate, Win32Native.MONITOR_DEFAULTTONULL);
            var targetMonitor = onMonitor != IntPtr.Zero
                ? onMonitor
                : Win32Native.MonitorFromRect(ref candidate, Win32Native.MONITOR_DEFAULTTONEAREST);

            if (targetMonitor != IntPtr.Zero)
            {
                var mi = new Win32Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Win32Native.MONITORINFO>() };
                if (Win32Native.GetMonitorInfoW(targetMonitor, ref mi))
                {
                    var work = mi.rcWork;
                    var workW = Math.Max(640, work.right - work.left);
                    var workH = Math.Max(480, work.bottom - work.top);
                    var w = Math.Clamp(saved.Width, 640, workW);
                    var h = Math.Clamp(saved.Height, 480, workH);
                    var x = onMonitor != IntPtr.Zero
                        ? Math.Clamp(saved.X, work.left - w + 120, work.right - 120)
                        : Math.Clamp(saved.X, work.left, Math.Max(work.left, work.right - w));
                    var y = Math.Clamp(saved.Y, work.top, Math.Max(work.top, work.bottom - 48));
                    return (x, y, w, h, saved.IsMaximized);
                }
            }

            return (saved.X, saved.Y, saved.Width, saved.Height, saved.IsMaximized);
        }

        var screenW = Win32Native.GetSystemMetrics(Win32Native.SM_CXSCREEN);
        var screenH = Win32Native.GetSystemMetrics(Win32Native.SM_CYSCREEN);
        var defX = Math.Max(40, (screenW - defaultWidth) / 2);
        var defY = Math.Max(40, (screenH - defaultHeight) / 2);
        return (defX, defY, defaultWidth, defaultHeight, true);
    }

    private static (uint SurfaceColorRef, Color WebViewBgColor, bool IsDark) ResolveThemeColors(string? themeMode) =>
        (themeMode ?? "berry").Trim().ToLowerInvariant() switch
        {
            "dark" => (0x00241B18u, Color.FromArgb(0x0F, 0x11, 0x17), true),
            "paper" => (0x00F8FDFFu, Color.FromArgb(0xF7, 0xF3, 0xE9), false),
            "light" => (0x00FFFFFFu, Color.FromArgb(0xF6, 0xF8, 0xFA), false),
            _ => (0x00FFFFFFu, Color.FromArgb(0xFD, 0xF6, 0xF9), false)
        };

    private static int GetFrameX(uint dpi) => dpi > 0
        ? Win32Native.GetSystemMetricsForDpi(Win32Native.SM_CXFRAME, dpi) + Win32Native.GetSystemMetricsForDpi(Win32Native.SM_CXPADDEDBORDER, dpi)
        : Win32Native.GetSystemMetrics(Win32Native.SM_CXFRAME) + Win32Native.GetSystemMetrics(Win32Native.SM_CXPADDEDBORDER);

    private static int GetFrameY(uint dpi) => dpi > 0
        ? Win32Native.GetSystemMetricsForDpi(Win32Native.SM_CYFRAME, dpi) + Win32Native.GetSystemMetricsForDpi(Win32Native.SM_CXPADDEDBORDER, dpi)
        : Win32Native.GetSystemMetrics(Win32Native.SM_CYFRAME) + Win32Native.GetSystemMetrics(Win32Native.SM_CXPADDEDBORDER);

    private static int GetTopResizeBorderPx(uint dpi) =>
        Math.Max(4, (int)Math.Round(5.0 * (dpi > 0 ? dpi : 96u) / 96.0));
}
