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
