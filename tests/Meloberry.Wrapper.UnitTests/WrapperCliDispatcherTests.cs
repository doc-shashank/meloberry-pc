using System.Net;
using System.Text;
using System.Text.Json;
using Meloberry.Wrapper.Application;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure;
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

    [Fact]
    public async Task WrapperCliDispatcher_PreservesFlagValues_AllowsFinalizeSwitchWithJson_AndPrintsHumanTextWhenGuiRunning()
    {
        var dataDir = Path.Combine(_tempRoot, "cli_flags_data");
        var ws = Path.Combine(_tempRoot, "cli_flags_ws");
        Directory.CreateDirectory(ws);

        using var provider = BuildFullServerProvider(dataDir);
        var settingsStore = provider.GetRequiredService<ISettingsStore>();
        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true)));

        var dispatcher = provider.GetRequiredService<WrapperCliDispatcher>();

        var cTitle = new CaptureConsole();
        Assert.Equal(0, await dispatcher.RunAsync(
            ["--cli", "--workspace", ws, "draft", "patch", "--title", "--json", "--desc", "--launch"],
            cTitle));

        var draftStore = provider.GetRequiredService<IWorkspaceDraftStore>();
        var patched = await draftStore.LoadAsync(ws);
        Assert.Equal("--json", patched.Title);
        Assert.Equal("--launch", patched.Description);
        Assert.DoesNotContain("\"success\"", cTitle.StdOut);

        var cAddComp = new CaptureConsole();
        Assert.Equal(0, await dispatcher.RunAsync(["--cli", "--workspace", ws, "draft", "comp", "add", "article", "--title", "Notes", "--json"], cAddComp));
        var compId = JsonDocument.Parse(cAddComp.StdOut).RootElement.GetProperty("data").GetProperty("targetComponentId").GetString()!;

        var cAddSpec = new CaptureConsole();
        Assert.Equal(0, await dispatcher.RunAsync(["--cli", "--workspace", ws, "draft", "spec", "add", compId, "text", "--json"], cAddSpec));
        var specId = JsonDocument.Parse(cAddSpec.StdOut).RootElement.GetProperty("data").GetProperty("targetSpecId").GetString()!;

        var cRaw = new CaptureConsole();
        Assert.Equal(0, await dispatcher.RunAsync(
            ["--cli", "--workspace", ws, "draft", "spec", "set-content", specId, "--raw", "--workspace"],
            cRaw));

        var afterRaw = await draftStore.LoadAsync(ws);
        var spec = afterRaw.Components!.Single(c => c.Id == compId).OrderedItems!.Single(s => s.Id == specId);
        Assert.Equal("--workspace", spec.Content!.RawText);

        var server = provider.GetRequiredService<LocalHttpWrapperServer>();
        var ipc = provider.GetRequiredService<IRuntimeIpcRegistry>();
        server.Start();
        try
        {
            await ipc.WriteCurrentAsync(server.Port, server.ViewerPort, server.AuthToken);
            var cLiveText = new CaptureConsole();
            Assert.Equal(0, await dispatcher.RunAsync(
                ["--cli", "--workspace", ws, "draft", "patch", "--title", "Live Human Output"],
                cLiveText));

            var textOut = cLiveText.StdOut.Trim();
            Assert.False(textOut.StartsWith('{'), $"Expected human summary text, got raw JSON: {textOut}");
            Assert.Contains("Updated draft metadata", textOut);

            var cUiView = new CaptureConsole();
            Assert.Equal(0, await dispatcher.RunAsync(["--cli", "--workspace", ws, "ui", "view", "organize"], cUiView));
            Assert.False(cUiView.StdOut.Trim().StartsWith('{'), $"Expected human summary for ui view, got: {cUiView.StdOut}");

            var cFinJson = new CaptureConsole();
            var finExit = await dispatcher.RunAsync(["--cli", "--workspace", ws, "finalize", "--allow-placeholders", "--json"], cFinJson);
            Assert.NotEqual(0, finExit);
            using (var errDoc = JsonDocument.Parse(cFinJson.StdErr))
            {
                Assert.False(errDoc.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal("Cli.NotFound", errDoc.RootElement.GetProperty("error").GetProperty("code").GetString());
            }

            var cPatchPh = new CaptureConsole();
            Assert.Equal(0, await dispatcher.RunAsync(["--cli", "--workspace", ws, "draft", "patch", "--allow-placeholders", "true", "--json"], cPatchPh));
            using (var patchDoc = JsonDocument.Parse(cPatchPh.StdOut))
            {
                Assert.True(patchDoc.RootElement.GetProperty("success").GetBoolean());
                Assert.True(patchDoc.RootElement.GetProperty("data").GetProperty("draft").GetProperty("allowPlaceholdersOnFinalize").GetBoolean());
            }
        }
        finally
        {
            ipc.CleanupCurrent();
            await server.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("details", "format")]
    [InlineData("project", "format")]
    [InlineData("finalize", "review")]
    [InlineData("format", "format")]
    [InlineData("review", "review")]
    public async Task WrapperCliDispatcher_MapsUiViewAliases_AndBroadcastsCanonicalView(string cliArg, string expectedCanonicalView)
    {
        var dataDir = Path.Combine(_tempRoot, $"cli_alias_{cliArg}");
        var ws = Path.Combine(_tempRoot, $"ws_alias_{cliArg}");
        Directory.CreateDirectory(ws);

        using var provider = BuildFullServerProvider(dataDir);
        var settingsStore = provider.GetRequiredService<ISettingsStore>();
        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true)));

        var server = provider.GetRequiredService<LocalHttpWrapperServer>();
        var sseHub = provider.GetRequiredService<SseEventHub>();
        var ipc = provider.GetRequiredService<IRuntimeIpcRegistry>();
        var dispatcher = provider.GetRequiredService<WrapperCliDispatcher>();

        server.Start();
        try
        {
            await ipc.WriteCurrentAsync(server.Port, server.ViewerPort, server.AuthToken);

            using var sseCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var sseClient = new HttpClient { BaseAddress = new Uri(server.BaseUrl), Timeout = Timeout.InfiniteTimeSpan };
            using var sseResp = await sseClient.GetAsync($"/api/events?token={server.AuthToken}", HttpCompletionOption.ResponseHeadersRead, sseCts.Token);
            Assert.Equal(HttpStatusCode.OK, sseResp.StatusCode);

            var received = new List<string>();
            var readerTask = Task.Run(async () =>
            {
                using var sr = new StreamReader(await sseResp.Content.ReadAsStreamAsync(sseCts.Token));
                while (!sseCts.IsCancellationRequested)
                {
                    var line = await sr.ReadLineAsync(sseCts.Token);
                    if (line is null) break;
                    if (line.Length > 0) received.Add(line);
                }
            }, sseCts.Token);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sseHub.SubscriberCount == 0 && sw.ElapsedMilliseconds < 1500)
            {
                await Task.Delay(20);
            }

            var con = new CaptureConsole();
            var exitCode = await dispatcher.RunAsync(["--cli", "--workspace", ws, "ui", "view", cliArg, "--json"], con);
            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(con.StdOut);
            Assert.Equal(expectedCanonicalView, doc.RootElement.GetProperty("data").GetProperty("view").GetString());

            await Task.Delay(120);
            sseCts.Cancel();
            try { await readerTask; } catch (OperationCanceledException) { }

            var joined = string.Join("\n", received);
            Assert.Contains("event: view-requested", joined);
            Assert.Contains($"\"view\":\"{expectedCanonicalView}\"", joined);
        }
        finally
        {
            ipc.CleanupCurrent();
            await server.DisposeAsync();
        }
    }

    private static ServiceProvider BuildFullServerProvider(string dataDir)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppShutdownCoordinator>(new NoopShutdown());
        services.AddSingleton<INativeDialogService>(new NoopDialogs());
        services.AddSingleton<IExternalUrlLauncher>(new NoopLauncher());
        services.AddWrapperApplication(typeof(LocalHttpWrapperServer).Assembly);
        services.AddWrapperInfrastructure(dataDir);
        return services.BuildServiceProvider();
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
