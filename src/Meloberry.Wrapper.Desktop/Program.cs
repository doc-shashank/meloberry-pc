using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using Meloberry.Wrapper.Application;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Desktop.Cli;
using Meloberry.Wrapper.Desktop.Win32;
using Meloberry.Wrapper.Infrastructure;
using Meloberry.Wrapper.Infrastructure.Cli;
using Meloberry.Wrapper.Infrastructure.Common;
using Meloberry.Wrapper.Infrastructure.Server;
using Meloberry.Wrapper.Infrastructure.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;

[assembly: SupportedOSPlatform("windows")]

namespace Meloberry.Wrapper.Desktop;

internal static class Program
{
    private const string SingleInstanceMutexName = "MeloberryPcSingleInstanceMutex";

    [STAThread]
    private static int Main(string[] args)
    {
        TryPersistExePointer();

        if (args.Contains("--cli", StringComparer.OrdinalIgnoreCase))
        {
            ConsoleBridge.AttachParentIfInteractive();
            return RunHeadlessCli(args);
        }

        if (args.Contains("--verify-loader", StringComparer.OrdinalIgnoreCase))
        {
            var dataPaths = new WrapperDataPaths();
            _ = WebView2Host.EnsureLoaderDllLoaded(dataPaths, new AtomicFileWriter());
            _ = CoreWebView2Environment.CompareBrowserVersions("1.0.0.0", "1.0.0.0");
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return 0;
        }

        var instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            instanceMutex.Dispose();
            var existing = Win32Native.FindWindowW(Win32WindowHost.WindowClassName, null);
            if (existing != IntPtr.Zero)
            {
                if (Win32Native.IsIconic(existing))
                {
                    Win32Native.ShowWindow(existing, Win32Native.SW_RESTORE);
                }

                Win32Native.SetForegroundWindow(existing);
            }

            return 0;
        }

        (string FileName, string Arguments)? pendingLaunch = null;
        try
        {
            var windowHost = new Win32WindowHost();
            SynchronizationContext.SetSynchronizationContext(new Win32SynchronizationContext(windowHost));

            var services = new ServiceCollection();
            services.AddSingleton(windowHost);
            services.AddSingleton<IAppShutdownCoordinator>(windowHost);
            services.AddSingleton<INativeDialogService, NativeFileDialogs>();
            services.AddSingleton<IExternalUrlLauncher, ExternalUrlLauncher>();
            services.AddSingleton<WebView2Host>();
            services.AddWrapperApplication(Assembly.GetExecutingAssembly());
            services.AddWrapperInfrastructure();

            using var provider = services.BuildServiceProvider();
            var metadata = provider.GetRequiredService<IWrapperMetadataProvider>().GetMetadata();
            var settingsStore = provider.GetRequiredService<ISettingsStore>();
            var initialSettings = settingsStore.LoadAsync().GetAwaiter().GetResult();
            var server = provider.GetRequiredService<LocalHttpWrapperServer>();
            var runtimeIpc = provider.GetRequiredService<IRuntimeIpcRegistry>();
            var webViewHost = provider.GetRequiredService<WebView2Host>();

            try
            {
                server.Start();
                runtimeIpc.WriteCurrentAsync(server.Port, server.ViewerPort, server.AuthToken).GetAwaiter().GetResult();
                windowHost.Create(
                    $"{metadata.ProductName} v{metadata.Version}",
                    initialSettings.ThemeMode,
                    initialSettings.WindowPlacement);
                windowHost.Show();

                windowHost.EnqueueCallback(() => _ = InitializeWebViewOnUiThreadAsync(
                    webViewHost,
                    windowHost,
                    server.BaseUrl,
                    server.ViewerBaseUrl));

                windowHost.RunMessageLoop();
            }
            finally
            {
                runtimeIpc.CleanupCurrent();
                webViewHost.Dispose();
                pendingLaunch = windowHost.PendingPostShutdownLaunch;
                if (windowHost.LastCapturedPlacement is { } placement)
                {
                    try
                    {
                        settingsStore.UpdateAsync(s => s with { WindowPlacement = placement }).GetAwaiter().GetResult();
                    }
                    catch
                    {
                        // Best-effort placement persistence on exit.
                    }
                }
            }
        }
        finally
        {
            try { instanceMutex.ReleaseMutex(); } catch { }
            instanceMutex.Dispose();
        }

        if (pendingLaunch is { } launch && File.Exists(launch.FileName))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.GetFullPath(launch.FileName),
                    Arguments = launch.Arguments,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Best-effort installer handoff after shutdown.
            }
        }

        return 0;
    }

    private static void TryPersistExePointer()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return;
            }

            var dataPaths = new WrapperDataPaths();
            AtomicFileOps.WriteAllTextAsync(dataPaths.ExePointerFilePath, exePath).GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore read-only AppData edge cases during startup.
        }
    }

    private static int RunHeadlessCli(string[] args)
    {
        try
        {
            var services = new ServiceCollection();
            services.AddWrapperApplication(Assembly.GetExecutingAssembly());
            services.AddWrapperInfrastructure();

            using var provider = services.BuildServiceProvider();
            var dispatcher = provider.GetRequiredService<WrapperCliDispatcher>();
            return dispatcher.RunAsync(args, new SystemWrapperConsole(), LaunchGuiForWorkspaceAsync).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal CLI error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<bool> LaunchGuiForWorkspaceAsync(string workspaceDir)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Directory.Exists(workspaceDir) ? workspaceDir : Environment.CurrentDirectory,
            UseShellExecute = true
        });

        var ipc = new RuntimeIpcRegistry(new WrapperDataPaths());
        for (var i = 0; i < 40; i++)
        {
            if (ipc.TryReadActive() is not null)
            {
                return true;
            }
            await Task.Delay(150).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task InitializeWebViewOnUiThreadAsync(
        WebView2Host webViewHost,
        Win32WindowHost windowHost,
        string baseUrl,
        string viewerBaseUrl)
    {
        try
        {
            await webViewHost.InitializeAsync(baseUrl, viewerBaseUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Win32Native.MessageBoxW(
                windowHost.Handle,
                "Microsoft Edge WebView2 Runtime is required to run Meloberry PC.\n\nPlease install Evergreen WebView2 Runtime from developer.microsoft.com/en-us/microsoft-edge/webview2/ and relaunch.",
                "Meloberry PC — Missing WebView2 Runtime",
                Win32Native.MB_OK | Win32Native.MB_ICONERROR);
            windowHost.RequestShutdown();
        }
        catch (Exception ex)
        {
            Win32Native.MessageBoxW(
                windowHost.Handle,
                $"Failed to initialize Meloberry PC:\n\n{ex.Message}",
                "Meloberry PC Error",
                Win32Native.MB_OK | Win32Native.MB_ICONERROR);
            windowHost.RequestShutdown();
        }
    }
}
