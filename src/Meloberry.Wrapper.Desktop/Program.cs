using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using Meloberry.Wrapper.Application;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Desktop.Win32;
using Meloberry.Wrapper.Infrastructure;
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
        if (args.Contains("--verify-loader", StringComparer.OrdinalIgnoreCase))
        {
            var dataPaths = new WrapperDataPaths();
            _ = WebView2Host.EnsureLoaderDllLoaded(dataPaths);
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
                Win32Native.ShowWindow(existing, Win32Native.SW_RESTORE);
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
            var server = provider.GetRequiredService<LocalHttpWrapperServer>();
            var webViewHost = provider.GetRequiredService<WebView2Host>();

            try
            {
                server.Start();
                windowHost.Create($"{metadata.ProductName} v{metadata.Version}");
                windowHost.Show();

                windowHost.EnqueueCallback(async () =>
                {
                    try
                    {
                        await webViewHost.InitializeAsync(server.BaseUrl, server.ViewerBaseUrl);
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
                });

                windowHost.RunMessageLoop();
            }
            finally
            {
                webViewHost.Dispose();
                pendingLaunch = windowHost.PendingPostShutdownLaunch;
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
}
