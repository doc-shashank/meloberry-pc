using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using Meloberry.Wrapper.Application.Abstractions;

namespace Meloberry.Wrapper.Desktop.Win32;

/// <summary>Posts continuations onto the STA Win32 window message pump.</summary>
internal sealed class Win32SynchronizationContext(Win32WindowHost host) : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) =>
        host.EnqueueCallback(() => d(state));
}

/// <summary>Owns the borderless ExtendContent Win32 HWND window, maximized startup, DPI awareness, and shutdown.</summary>
internal sealed class Win32WindowHost : IAppShutdownCoordinator
{
    public const string WindowClassName = "MeloberryPcWrapperWindowClass";

    private readonly ConcurrentQueue<Action> _uiQueue = new();
    private readonly Win32Native.WndProc _wndProcDelegate;
    private IntPtr _hwnd;
    private volatile bool _isDestroyed;

    public event Action<Size>? Resized;
    public event Action<bool>? MaximizedChanged;
    public event Action<IReadOnlyList<string>>? FilesDropped;

    public Win32WindowHost()
    {
        _wndProcDelegate = WindowProc;
    }

    public IntPtr Handle => _hwnd;
    public bool IsMaximized => _hwnd != IntPtr.Zero && Win32Native.IsZoomed(_hwnd);
    public (string FileName, string Arguments)? PendingPostShutdownLaunch { get; private set; }

    public void Create(string title, int width = 1320, int height = 860)
    {
        Win32Native.SetProcessDpiAwarenessContext(Win32Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        var hInstance = Win32Native.GetModuleHandleW(null);

        var wc = new Win32Native.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Win32Native.WNDCLASSEXW>(),
            style = 0x0003,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInstance,
            lpszClassName = WindowClassName
        };
        Win32Native.RegisterClassExW(ref wc);

        var screenW = Win32Native.GetSystemMetrics(Win32Native.SM_CXSCREEN);
        var screenH = Win32Native.GetSystemMetrics(Win32Native.SM_CYSCREEN);
        var x = Math.Max(40, (screenW - width) / 2);
        var y = Math.Max(40, (screenH - height) / 2);

        _hwnd = Win32Native.CreateWindowExW(
            0,
            WindowClassName,
            title,
            Win32Native.WS_OVERLAPPEDWINDOW,
            x,
            y,
            width,
            height,
            IntPtr.Zero,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create Win32 host window.");
        }

        var useDark = 1;
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
        Win32Native.ShowWindow(_hwnd, Win32Native.SW_SHOWMAXIMIZED);
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

    public Rectangle GetClientBounds()
    {
        if (_hwnd == IntPtr.Zero || !Win32Native.GetClientRect(_hwnd, out var rect))
        {
            return new Rectangle(0, 0, 1280, 800);
        }

        return new Rectangle(0, 0, Math.Max(1, rect.right - rect.left), Math.Max(1, rect.bottom - rect.top));
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
                    var frameX = dpi > 0
                        ? Win32Native.GetSystemMetricsForDpi(Win32Native.SM_CXFRAME, dpi) + Win32Native.GetSystemMetricsForDpi(Win32Native.SM_CXPADDEDBORDER, dpi)
                        : Win32Native.GetSystemMetrics(Win32Native.SM_CXFRAME) + Win32Native.GetSystemMetrics(Win32Native.SM_CXPADDEDBORDER);
                    var frameY = dpi > 0
                        ? Win32Native.GetSystemMetricsForDpi(Win32Native.SM_CYFRAME, dpi) + Win32Native.GetSystemMetricsForDpi(Win32Native.SM_CXPADDEDBORDER, dpi)
                        : Win32Native.GetSystemMetrics(Win32Native.SM_CYFRAME) + Win32Native.GetSystemMetrics(Win32Native.SM_CXPADDEDBORDER);

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

            case Win32Native.WM_APP_INVOKE:
                while (_uiQueue.TryDequeue(out var act))
                {
                    act();
                }

                return IntPtr.Zero;

            case Win32Native.WM_SIZE:
                if (Win32Native.GetClientRect(hWnd, out var rect))
                {
                    Resized?.Invoke(new Size(Math.Max(1, rect.right - rect.left), Math.Max(1, rect.bottom - rect.top)));
                }

                MaximizedChanged?.Invoke(Win32Native.IsZoomed(hWnd));
                return IntPtr.Zero;

            case Win32Native.WM_DROPFILES:
                HandleDropFiles(wParam);
                return IntPtr.Zero;

            case Win32Native.WM_DESTROY:
                _isDestroyed = true;
                _hwnd = IntPtr.Zero;
                while (_uiQueue.TryDequeue(out var pending))
                {
                    try { pending(); } catch { }
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
}
