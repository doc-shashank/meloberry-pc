using System.Net;
using System.Text;
using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.Cli;
using Meloberry.Wrapper.Infrastructure.Persistence;
using Meloberry.Wrapper.Infrastructure.Server;
using Meloberry.Wrapper.Infrastructure.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meloberry.Wrapper.UnitTests;

public sealed class WrapperCliDispatcherTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_cli_disp_{Guid.NewGuid():N}");

    [Fact]
    public async Task WrapperCliDispatcher_HandlesPrimeStatusDraftAndGuiClosedGuards()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "cli_data"));
        var ws = Path.Combine(_tempRoot, "ws_cli");
        Directory.CreateDirectory(ws);

        using var provider = BuildTestServiceProvider(paths);
        var settingsStore = provider.GetRequiredService<ISettingsStore>();
        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true)));

        var dispatcher = provider.GetRequiredService<WrapperCliDispatcher>();

        var cPrime = new CaptureConsole();
        Assert.Equal(0, await dispatcher.RunAsync(["--cli", "--workspace", ws, "prime", "--json"], cPrime));
        using (var doc = JsonDocument.Parse(cPrime.StdOut))
        {
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("mby-pc.bat", doc.RootElement.GetProperty("data").GetProperty("grammar").GetString());
        }

        var cStatus = new CaptureConsole();
        Assert.Equal(0, await dispatcher.RunAsync(["--cli", "--workspace", ws, "status", "--json"], cStatus));
        using (var doc = JsonDocument.Parse(cStatus.StdOut))
        {
            var data = doc.RootElement.GetProperty("data");
            Assert.False(data.GetProperty("guiRunning").GetBoolean());
            Assert.Equal(Path.GetFullPath(ws), Path.GetFullPath(data.GetProperty("activeWorkspace").GetString()!));
        }

        var cAddComp = new CaptureConsole();
        Assert.Equal(0, await dispatcher.RunAsync(["--cli", "--workspace", ws, "draft", "comp", "add", "quiz", "--title", "CLI Quiz", "--json"], cAddComp));
        var compId = JsonDocument.Parse(cAddComp.StdOut).RootElement.GetProperty("data").GetProperty("targetComponentId").GetString()!;

        var cAddSpec = new CaptureConsole();
        Assert.Equal(0, await dispatcher.RunAsync(["--cli", "--workspace", ws, "draft", "spec", "add", compId, "mcq", "--subtype", "multi", "--json"], cAddSpec));
        var specId = JsonDocument.Parse(cAddSpec.StdOut).RootElement.GetProperty("data").GetProperty("targetSpecId").GetString()!;

        var cSetContent = new CaptureConsole();
        Assert.Equal(0, await dispatcher.RunAsync(["--cli", "--workspace", ws, "draft", "spec", "set-content", specId, "--raw", "Pick primes?\n- [x] 2\n- [x] 3", "--json"], cSetContent));

        var cDraftGet = new CaptureConsole();
        Assert.Equal(0, await dispatcher.RunAsync(["--cli", "--workspace", ws, "draft", "get", "--json"], cDraftGet));
        Assert.Contains("CLI Quiz", cDraftGet.StdOut);
        Assert.Contains("Pick primes?", cDraftGet.StdOut);

        var cUi = new CaptureConsole();
        Assert.Equal(3, await dispatcher.RunAsync(["--cli", "--workspace", ws, "ui", "view", "organize", "--json"], cUi));
        Assert.Contains("\"code\":\"Gui.NotRunning\"", cUi.StdErr);

        var cMissingTool = new CaptureConsole();
        Assert.Equal(3, await dispatcher.RunAsync(["--cli", "--workspace", ws, "mby", "--version", "--json"], cMissingTool));
        Assert.Contains("\"code\":\"Cli.NotFound\"", cMissingTool.StdErr);
    }

    [Fact]
    public async Task ToolProxyRunner_DetectsMbyBuildOutputFromJsonAndArgs()
    {
        var ws = Path.Combine(_tempRoot, "ws_proxy");
        var webOut = Path.Combine(ws, "my_web");
        Directory.CreateDirectory(webOut);
        await File.WriteAllTextAsync(Path.Combine(webOut, "index.html"), "<html></html>");

        var escapedWebOut = webOut.Replace("\\", "\\\\");
        var jsonOut = "{\"success\":true,\"data\":{\"outputDirectory\":\"" + escapedWebOut + "\"}}";
        var detectedFromJson = ToolProxyRunner.TryResolveBuiltWebAppDirectory(["build", "Session_1", "--json"], jsonOut, ws);
        Assert.Equal(Path.GetFullPath(webOut), detectedFromJson);

        var detectedFromFlag = ToolProxyRunner.TryResolveBuiltWebAppDirectory(["build", "Session_1", "-o", "my_web"], "", ws);
        Assert.Equal(Path.GetFullPath(webOut), detectedFromFlag);

        Assert.Null(ToolProxyRunner.TryResolveBuiltWebAppDirectory(["ls", "--json"], "{}", ws));
    }

    [Fact]
    public async Task WrapperCliDispatcher_SupportsCompRename_SpecUpdate_ImageContent_AndPatchFormat()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "cli_p6"));
        var ws = Path.Combine(_tempRoot, "ws_p6");
        var imgPath = Path.Combine(ws, "assets", "heart.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imgPath)!);
        await File.WriteAllBytesAsync(imgPath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        using var provider = BuildTestServiceProvider(paths);
        var settingsStore = provider.GetRequiredService<ISettingsStore>();
        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true)));

        var dispatcher = provider.GetRequiredService<WrapperCliDispatcher>();

        Assert.Equal(0, await dispatcher.RunAsync(["--cli", "--workspace", ws, "draft", "patch", "--title", "Cardio Live", "--format", "singleMarkdown", "--json"], new CaptureConsole()));

        var cAddComp = new CaptureConsole();
        Assert.Equal(0, await dispatcher.RunAsync(["--cli", "--workspace", ws, "draft", "comp", "add", "article", "--title", "Old Name", "--json"], cAddComp));
        var compId = JsonDocument.Parse(cAddComp.StdOut).RootElement.GetProperty("data").GetProperty("targetComponentId").GetString()!;

        Assert.Equal(0, await dispatcher.RunAsync(["--cli", "--workspace", ws, "draft", "comp", "rename", compId, "--title", "Cardiac Cycle", "--json"], new CaptureConsole()));

        var cAddSpec = new CaptureConsole();
        Assert.Equal(0, await dispatcher.RunAsync(["--cli", "--workspace", ws, "draft", "spec", "add", compId, "image", "--json"], cAddSpec));
        var specId = JsonDocument.Parse(cAddSpec.StdOut).RootElement.GetProperty("data").GetProperty("targetSpecId").GetString()!;

        Assert.Equal(0, await dispatcher.RunAsync(["--cli", "--workspace", ws, "draft", "spec", "update", specId, "--subtype", "annotated", "--notes", "Highlight valves", "--modes", "zoom,caption", "--json"], new CaptureConsole()));
        Assert.Equal(0, await dispatcher.RunAsync(["--cli", "--workspace", ws, "draft", "spec", "set-content", specId, "--image", "assets/heart.png", "--caption", "Wiggers Diagram", "--alt", "Heart chambers", "--json"], new CaptureConsole()));

        var draftStore = provider.GetRequiredService<IWorkspaceDraftStore>();
        var draft = await draftStore.LoadAsync(ws);
        Assert.Equal("Cardio Live", draft.Title);
        Assert.Equal(AuthoringFormat.SingleMarkdown, draft.AuthoringFormat);
        var comp = draft.Components!.Single(c => c.Id == compId);
        Assert.Equal("Cardiac Cycle", comp.Title);
        var spec = comp.OrderedItems!.Single(s => s.Id == specId);
        Assert.Equal("annotated", spec.Subtype);
        Assert.Equal(SpecContentMode.ImageFile, spec.Content!.Mode);
        Assert.Equal("assets/heart.png", spec.Content.ImagePath);
        Assert.Equal("Wiggers Diagram", spec.Content.Caption);
        Assert.Equal("Heart chambers", spec.Content.AltText);
        var sel = comp.Selections.Single(s => s.Topic == "image");
        Assert.Equal("Highlight valves", sel.Notes);
        Assert.Equal(["zoom", "caption"], sel.Modes);

        var cPrime = new CaptureConsole();
        Assert.Equal(0, await dispatcher.RunAsync(["--cli", "--workspace", ws, "prime", "--json"], cPrime));
        Assert.Contains("rename", cPrime.StdOut);
        Assert.Contains("update", cPrime.StdOut);
        Assert.Contains("--image", cPrime.StdOut);
        Assert.Contains("--format", cPrime.StdOut);
    }

    [Fact]
    public async Task ToolProxyRunner_PostsRunningAndDoneOrErrorActivityWithExitCode()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "proxy_p6"));
        var ws = Path.Combine(_tempRoot, "ws_proxy_act");
        Directory.CreateDirectory(ws);

        var okBat = Path.Combine(ws, "fake-mby-ok.cmd");
        await File.WriteAllTextAsync(okBat, "@echo off\r\necho hello\r\nexit /b 0\r\n");
        var failBat = Path.Combine(ws, "fake-mby-fail.cmd");
        await File.WriteAllTextAsync(failBat, "@echo off\r\necho boom 1>&2\r\nexit /b 2\r\n");

        var recordedPayloads = new List<JsonElement>();
        using var listener = new HttpListener();
        using var sock = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        sock.Start();
        var port = ((IPEndPoint)sock.LocalEndpoint).Port;
        sock.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().WaitAsync(cts.Token); }
                catch { break; }

                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(cts.Token);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    lock (recordedPayloads) { recordedPayloads.Add(doc.RootElement.Clone()); }
                }

                var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes, cts.Token);
                ctx.Response.Close();
            }
        });

        var ipc = new RuntimeIpcRegistry(paths);
        await ipc.WriteCurrentAsync(port, port + 1, "test-token");

        var settingsStore = new JsonSettingsStore(paths);
        var runner = new ToolProxyRunner(new ManagedAppsStore(paths), settingsStore, new LiveGuiIpcClient(ipc));

        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true), MbyExecutableOverride: okBat));
        Assert.Equal(0, await runner.RunAsync("mby", ws, ["--version"], new CaptureConsole(), jsonMode: false));

        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true), MbyExecutableOverride: failBat));
        Assert.Equal(2, await runner.RunAsync("mby", ws, ["check"], new CaptureConsole(), jsonMode: false));

        cts.Cancel();
        listener.Stop();
        await serverTask;

        List<JsonElement> snapshot;
        lock (recordedPayloads) { snapshot = recordedPayloads.ToList(); }

        Assert.Contains(snapshot, p => p.GetProperty("action").GetString() == "activity" && p.GetProperty("state").GetString() == "running");
        Assert.Contains(snapshot, p => p.GetProperty("action").GetString() == "activity" && p.GetProperty("state").GetString() == "done" && p.GetProperty("exitCode").GetInt32() == 0);
        Assert.Contains(snapshot, p => p.GetProperty("action").GetString() == "activity" && p.GetProperty("state").GetString() == "error" && p.GetProperty("exitCode").GetInt32() == 2);
    }

    private static ServiceProvider BuildTestServiceProvider(WrapperDataPaths paths)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWrapperDataPaths>(paths);
        services.AddSingleton<IWrapperMetadataProvider>(_ => new AssemblyWrapperMetadataProvider(typeof(LiveDraftHandler).Assembly));
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IWorkspaceDraftStore, WorkspaceDraftStore>();
        services.AddSingleton<IRuntimeIpcRegistry, RuntimeIpcRegistry>();
        services.AddSingleton<IManagedAppsStore, ManagedAppsStore>();
        services.AddSingleton<IWebAppAnalysisStore, WebAppAnalysisStore>();
        services.AddSingleton<IWebAppMountRegistry, WebAppMountRegistry>();
        services.AddSingleton<IWorkspaceShimWriter, WorkspaceShimWriter>();
        services.AddSingleton<IWorkspaceInitializer, WorkspaceInitializer>();
        services.AddSingleton<ManagedAppsHandler>();
        services.AddSingleton<ManageLibraryHandler>();
        services.AddSingleton<LiveDraftHandler>();
        services.AddSingleton<IMbyCliClient, ProcessMbyCliClient>();
        services.AddSingleton<FinalizeWorkspaceHandler>();
        services.AddSingleton<IGitHubToolDownloader, DummyDownloader>();
        services.AddSingleton<LiveGuiIpcClient>();
        services.AddSingleton<ToolProxyRunner>();
        services.AddSingleton<WrapperCliDispatcher>();
        return services.BuildServiceProvider();
    }

    private sealed class DummyDownloader : IGitHubToolDownloader
    {
        public Task<Result<ToolReleaseCandidate>> CheckLatestToolAsync(string toolKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<string>> DownloadToTempFileAsync(ToolReleaseCandidate candidate, Action<DownloadProgressSnapshot> progressCallback, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class CaptureConsole(string? stdin = null) : IWrapperConsole
    {
        private readonly StringWriter _out = new();
        private readonly StringWriter _err = new();
        private readonly StringReader _in = new(stdin ?? string.Empty);

        public TextWriter Out => _out;
        public TextWriter Error => _err;
        public TextReader In => _in;
        public bool IsInputRedirected => stdin is not null;
        public string StdOut => _out.ToString();
        public string StdErr => _err.ToString();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
