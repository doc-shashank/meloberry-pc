using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.Persistence;
using Meloberry.Wrapper.Infrastructure.Workspace;
using Xunit;

namespace Meloberry.Wrapper.UnitTests;

public sealed class WorkspaceMaintenanceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_maint_test_{Guid.NewGuid():N}");

    [Fact]
    public async Task Reset_RemovesRecognizedExesRecursively_PreservesOtherFiles_AndOptionallyDeletesAgentsMd()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "data"));
        Directory.CreateDirectory(paths.AppsDirectory);
        await File.WriteAllBytesAsync(Path.Combine(paths.AppsDirectory, "custom-app.exe"), [(byte)'M', (byte)'Z', 1]);

        var ws = Path.Combine(_tempRoot, "ws");
        var subDir = Path.Combine(ws, "nested", "tools");
        Directory.CreateDirectory(subDir);

        await File.WriteAllBytesAsync(Path.Combine(ws, "mby-cli-2.0.0.exe"), [(byte)'M', (byte)'Z', 1]);
        await File.WriteAllBytesAsync(Path.Combine(subDir, "meloberry-melomg-2.1.0.exe"), [(byte)'M', (byte)'Z', 2]);
        await File.WriteAllBytesAsync(Path.Combine(subDir, "custom-app.exe"), [(byte)'M', (byte)'Z', 3]);
        await File.WriteAllBytesAsync(Path.Combine(ws, "unrelated-user.exe"), [(byte)'M', (byte)'Z', 4]);
        await File.WriteAllTextAsync(Path.Combine(ws, "AGENTS.md"), "# AGENTS");
        await File.WriteAllTextAsync(Path.Combine(subDir, "notes.md"), "keep");

        var settingsStore = new JsonSettingsStore(paths);
        await settingsStore.SaveAsync(new WrapperSettings(
            ActiveWorkspace: new ActiveWorkspaceState(ws, true, AgentsMdExportedUtc: DateTimeOffset.UtcNow)));

        var appsStore = new ManagedAppsStore(paths);
        var handler = new WorkspaceMaintenanceHandler(new WorkspaceMaintenanceService(paths, appsStore), appsStore, settingsStore);

        // First: ExecutablesOnly preserves AGENTS.md, unrelated-user.exe, and notes.md.
        var planOnly = await handler.PlanResetAsync(ws, WorkspaceResetScope.ExecutablesOnly);
        Assert.True(planOnly.IsSuccess);
        Assert.Equal(3, planOnly.Value.ExecutablesToRemove.Count);
        Assert.Null(planOnly.Value.AgentsMdToRemove);

        var execOnly = await handler.ExecuteResetAsync(ws, WorkspaceResetScope.ExecutablesOnly);
        Assert.True(execOnly.IsSuccess);
        Assert.False(execOnly.Value.ResetResult.RemovedAgentsMd);
        Assert.False(File.Exists(Path.Combine(ws, "mby-cli-2.0.0.exe")));
        Assert.False(File.Exists(Path.Combine(subDir, "meloberry-melomg-2.1.0.exe")));
        Assert.False(File.Exists(Path.Combine(subDir, "custom-app.exe")));
        Assert.True(File.Exists(Path.Combine(ws, "unrelated-user.exe")));
        Assert.True(File.Exists(Path.Combine(ws, "AGENTS.md")));
        Assert.True(File.Exists(Path.Combine(subDir, "notes.md")));

        // Second: ExecutablesAndAgentsMd removes AGENTS.md and any new exe cleanly.
        await File.WriteAllBytesAsync(Path.Combine(subDir, "mby-cli-2.0.0.exe"), [(byte)'M', (byte)'Z', 1]);
        var execBoth = await handler.ExecuteResetAsync(ws, WorkspaceResetScope.ExecutablesAndAgentsMd);
        Assert.True(execBoth.IsSuccess);
        Assert.True(execBoth.Value.ResetResult.RemovedAgentsMd);
        Assert.False(File.Exists(Path.Combine(ws, "AGENTS.md")));
        Assert.False(File.Exists(Path.Combine(subDir, "mby-cli-2.0.0.exe")));
        Assert.Null(execBoth.Value.UpdatedWorkspace?.AgentsMdExportedUtc);
    }

    [Fact]
    public async Task DetectAndUpdateOutdatedTools_FindsOlderRootAndSubfolderExes_AndReplacesWithAppsLatest()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "data2"));
        Directory.CreateDirectory(paths.AppsDirectory);
        await File.WriteAllBytesAsync(Path.Combine(paths.AppsDirectory, "mby-cli-2.0.0.exe"), [(byte)'M', (byte)'Z', 0x20]);
        await File.WriteAllBytesAsync(Path.Combine(paths.AppsDirectory, "meloberry-melomg-2.2.0.exe"), [(byte)'M', (byte)'Z', 0x22]);

        var ws = Path.Combine(_tempRoot, "ws2");
        var sub = Path.Combine(ws, "sub");
        Directory.CreateDirectory(sub);
        await File.WriteAllBytesAsync(Path.Combine(ws, "mby-cli-1.9.0.exe"), [(byte)'M', (byte)'Z', 0x19]);
        await File.WriteAllBytesAsync(Path.Combine(sub, "meloberry-melomg-2.0.0.exe"), [(byte)'M', (byte)'Z', 0x20]);

        var settingsStore = new JsonSettingsStore(paths);
        await settingsStore.SaveAsync(new WrapperSettings(ActiveWorkspace: new ActiveWorkspaceState(ws, true)));

        var appsStore = new ManagedAppsStore(paths);
        var handler = new WorkspaceMaintenanceHandler(new WorkspaceMaintenanceService(paths, appsStore), appsStore, settingsStore);

        var outdated = await handler.DetectOutdatedToolsAsync(ws);
        Assert.Equal(2, outdated.Count);
        Assert.Contains(outdated, o => o.ToolKey == "mby" && o.WorkspaceVersion == "1.9.0" && o.LatestAppVersion == "2.0.0");
        Assert.Contains(outdated, o => o.ToolKey == "melomg" && o.WorkspaceVersion == "2.0.0" && o.LatestAppVersion == "2.2.0");

        var updateRes = await handler.UpdateOutdatedToolsAsync(ws);
        Assert.True(updateRes.IsSuccess);
        Assert.False(File.Exists(Path.Combine(ws, "mby-cli-1.9.0.exe")));
        Assert.False(File.Exists(Path.Combine(sub, "meloberry-melomg-2.0.0.exe")));
        Assert.True(File.Exists(Path.Combine(ws, "mby-cli-2.0.0.exe")));
        Assert.True(File.Exists(Path.Combine(ws, "meloberry-melomg-2.2.0.exe")));

        var after = await handler.DetectOutdatedToolsAsync(ws);
        Assert.Empty(after);

        // Bug #8: unversioned mby.exe identical to apps\ copy must NOT trigger an infinite outdated loop.
        var paths3 = new WrapperDataPaths(Path.Combine(_tempRoot, "data3"));
        Directory.CreateDirectory(paths3.AppsDirectory);
        var unversionedBytes = new byte[] { (byte)'M', (byte)'Z', 0x55, 0x66 };
        await File.WriteAllBytesAsync(Path.Combine(paths3.AppsDirectory, "mby.exe"), unversionedBytes);
        var ws3 = Path.Combine(_tempRoot, "ws3");
        Directory.CreateDirectory(ws3);
        await File.WriteAllBytesAsync(Path.Combine(ws3, "mby.exe"), unversionedBytes);

        var maint3 = new WorkspaceMaintenanceService(paths3, new ManagedAppsStore(paths3));
        var outdated3 = await maint3.DetectOutdatedToolsAsync(ws3);
        Assert.True(outdated3.IsSuccess);
        Assert.Empty(outdated3.Value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
