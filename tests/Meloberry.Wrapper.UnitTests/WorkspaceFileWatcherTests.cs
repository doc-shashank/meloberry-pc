using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.Persistence;
using Meloberry.Wrapper.Infrastructure.Server;
using Meloberry.Wrapper.Infrastructure.Workspace;
using Xunit;

namespace Meloberry.Wrapper.UnitTests;

public sealed class WorkspaceFileWatcherTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_watcher_test_{Guid.NewGuid():N}");

    public WorkspaceFileWatcherTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task Watcher_DetectsExecutableAndAgentsMdCreationAndDeletion()
    {
        var dataPaths = new WrapperDataPaths(Path.Combine(_tempRoot, "data"));
        Directory.CreateDirectory(dataPaths.AppsDirectory);
        var wsDir = Path.Combine(_tempRoot, "workspace");
        Directory.CreateDirectory(wsDir);

        using var watcher = new WorkspaceFileWatcherService(dataPaths);
        watcher.UpdateActiveWorkspace(wsDir);

        var createdTcs = new TaskCompletionSource<WorkspaceFileChangeEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deletedTcs = new TaskCompletionSource<WorkspaceFileChangeEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        watcher.Changed += ev =>
        {
            if (ev.Scope == "workspace" && ev.FileName.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase))
            {
                if (ev.ChangeKind == "created")
                {
                    createdTcs.TrySetResult(ev);
                }
                else if (ev.ChangeKind == "deleted")
                {
                    deletedTcs.TrySetResult(ev);
                }
            }
        };

        var agentsFile = Path.Combine(wsDir, "AGENTS.md");
        await File.WriteAllTextAsync(agentsFile, "# Test");

        var createdEv = await createdTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("created", createdEv.ChangeKind);
        Assert.Equal("AGENTS.md", createdEv.FileName);

        File.Delete(agentsFile);
        var deletedEv = await deletedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("deleted", deletedEv.ChangeKind);

        var appDeletedTcs = new TaskCompletionSource<WorkspaceFileChangeEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += ev =>
        {
            if (ev.Scope == "apps" && ev.FileName.Equals("mby-cli-test.exe", StringComparison.OrdinalIgnoreCase) && ev.ChangeKind == "deleted")
            {
                appDeletedTcs.TrySetResult(ev);
            }
        };

        var dummyExe = Path.Combine(dataPaths.AppsDirectory, "mby-cli-test.exe");
        await File.WriteAllBytesAsync(dummyExe, [0x4D, 0x5A]);
        await Task.Delay(350);
        File.Delete(dummyExe);

        var appDeletedEv = await appDeletedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("apps", appDeletedEv.Scope);
        Assert.Equal("deleted", appDeletedEv.ChangeKind);
    }

    [Fact]
    public async Task Watcher_SuppressScope_DropsInternalEventsAndResumesAfterGracePeriod()
    {
        var dataPaths = new WrapperDataPaths(Path.Combine(_tempRoot, "data_suppress"));
        Directory.CreateDirectory(dataPaths.AppsDirectory);
        var wsDir = Path.Combine(_tempRoot, "workspace_suppress");
        Directory.CreateDirectory(wsDir);

        using var watcher = new WorkspaceFileWatcherService(dataPaths);
        watcher.UpdateActiveWorkspace(wsDir);

        var observed = new List<WorkspaceFileChangeEvent>();
        var resumedTcs = new TaskCompletionSource<WorkspaceFileChangeEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        watcher.Changed += ev =>
        {
            lock (observed)
            {
                observed.Add(ev);
            }

            if (ev.Scope == "workspace" && ev.FileName.Equals("mby-cli-after.exe", StringComparison.OrdinalIgnoreCase))
            {
                resumedTcs.TrySetResult(ev);
            }
        };

        using (watcher.SuppressScope("workspace", postDisposeGraceMs: 250))
        {
            await File.WriteAllBytesAsync(Path.Combine(wsDir, "mby-cli-init.exe"), [0x4D, 0x5A]);
            await Task.Delay(150);
        }

        // Inside grace window right after Dispose: should still be suppressed.
        await File.WriteAllTextAsync(Path.Combine(wsDir, "AGENTS.md"), "# Suppressed");
        await Task.Delay(350);

        lock (observed)
        {
            Assert.DoesNotContain(observed, e => e.FileName.Equals("mby-cli-init.exe", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(observed, e => e.FileName.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase));
        }

        // After grace window expires, external changes emit normally.
        await File.WriteAllBytesAsync(Path.Combine(wsDir, "mby-cli-after.exe"), [0x4D, 0x5A]);
        var resumedEv = await resumedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("created", resumedEv.ChangeKind);
    }

    [Fact]
    public async Task Watcher_DetectsHiddenCliSignalFile_AndHandlerDeduplicatesSignalIds()
    {
        var dataPaths = new WrapperDataPaths(Path.Combine(_tempRoot, "data_signal"));
        Directory.CreateDirectory(dataPaths.AppsDirectory);
        var wsDir = Path.Combine(_tempRoot, "workspace_signal");
        var webDir = Path.Combine(wsDir, "out_web");
        Directory.CreateDirectory(webDir);
        await File.WriteAllTextAsync(Path.Combine(webDir, "index.html"), "<html><body>WebApp</body></html>");
        await File.WriteAllTextAsync(Path.Combine(webDir, "app.js"), "console.log('ready');");

        using var watcher = new WorkspaceFileWatcherService(dataPaths);
        watcher.UpdateActiveWorkspace(wsDir);

        var signalTcs = new TaskCompletionSource<WorkspaceFileChangeEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += ev =>
        {
            if (ev.Scope == "signal" && ev.FileName.Equals(MbyCliSignal.SignalFileName, StringComparison.OrdinalIgnoreCase))
            {
                signalTcs.TrySetResult(ev);
            }
        };

        var signalPath = Path.Combine(wsDir, MbyCliSignal.SignalFileName);
        var tmpPath = Path.Combine(wsDir, MbyCliSignal.SignalFileName + ".tmp");
        var signalText = "version=1\nevent=webapp_built\nid=build-42\npath=out_web\ntitle=Out Web\n";

        using (watcher.SuppressScope("workspace", postDisposeGraceMs: 400))
        {
            await File.WriteAllTextAsync(tmpPath, signalText);
            File.Move(tmpPath, signalPath, overwrite: true);
        }

        var signalEv = await signalTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("signal", signalEv.Scope);

        var settingsStore = new JsonSettingsStore(dataPaths);
        await settingsStore.UpdateAsync(s => s with { ActiveWorkspace = new ActiveWorkspaceState(wsDir, true) });
        var libHandler = new ManageLibraryHandler(
            new WebAppMountRegistry(),
            new WebAppAnalysisStore(),
            new WorkspaceInitializer(dataPaths, new WebAppAnalysisStore()),
            settingsStore);
        var signalHandler = new ProcessCliSignalHandler(settingsStore, libHandler);

        var first = await signalHandler.ProcessFileAsync(signalPath);
        Assert.True(first.IsSuccess);
        Assert.NotNull(first.Value);
        Assert.Equal("Out Web", first.Value!.Title);

        var duplicate = await signalHandler.ProcessFileAsync(signalPath);
        Assert.True(duplicate.IsSuccess);
        Assert.Null(duplicate.Value);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore temp cleanup failures.
        }
    }
}
