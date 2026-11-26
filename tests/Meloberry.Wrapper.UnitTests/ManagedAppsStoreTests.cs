using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.Workspace;
using Xunit;

namespace Meloberry.Wrapper.UnitTests;

public sealed class ManagedAppsStoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_apps_test_{Guid.NewGuid():N}");

    [Fact]
    public async Task ScanImportAndUninstall_RecognizesMetadataOnRenamedExe_AndPreservesWorkspaceOnUninstall()
    {
        var paths = new WrapperDataPaths(_tempRoot);
        var store = new ManagedAppsStore(paths);
        var importSourceDir = Path.Combine(_tempRoot, "imports");
        Directory.CreateDirectory(importSourceDir);

        var badExe = Path.Combine(importSourceDir, "not-pe.exe");
        await File.WriteAllTextAsync(badExe, "not a binary");
        var badRes = await store.ImportLocalExecutableAsync(badExe);
        Assert.True(badRes.IsFailure);
        Assert.Equal("Update.VerificationFailed", badRes.Error!.Code);

        var oldMby = Path.Combine(importSourceDir, "mby-cli-1.9.0.exe");
        await File.WriteAllBytesAsync(oldMby, [(byte)'M', (byte)'Z', 0x90]);
        var oldRes = await store.ImportLocalExecutableAsync(oldMby);
        Assert.True(oldRes.IsSuccess);

        var customExe = Path.Combine(importSourceDir, "helper-tool.exe");
        await File.WriteAllBytesAsync(customExe, [(byte)'M', (byte)'Z', 0x01]);
        Assert.True((await store.ImportLocalExecutableAsync(customExe)).IsSuccess);

        var newTemp = Path.Combine(paths.AppsDirectory, "download.tmp");
        await File.WriteAllBytesAsync(newTemp, [(byte)'M', (byte)'Z', 0x02]);
        var updated = await store.SaveDownloadedToolAsync("mby", "mby-cli-2.0.0.exe", newTemp);
        Assert.True(updated.IsSuccess);
        Assert.False(File.Exists(Path.Combine(paths.AppsDirectory, "mby-cli-1.9.0.exe")));
        Assert.True(File.Exists(Path.Combine(paths.AppsDirectory, "mby-cli-2.0.0.exe")));

        var installedMbyFileName = "mby-cli-2.0.0.exe";
        var installedMbyVersion = "2.0.0";

        // Metadata-based universal import: copy real mby-cli under an arbitrary filename and verify passive PE detection!
        var realMby = TestRepoPaths.FindMbyExecutable();
        if (!string.IsNullOrWhiteSpace(realMby) && File.Exists(realMby))
        {
            var expectedVersion = System.Text.RegularExpressions.Regex.Match(
                Path.GetFileNameWithoutExtension(realMby),
                @"\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?").Value;
            Assert.False(string.IsNullOrWhiteSpace(expectedVersion));

            var renamed = Path.Combine(importSourceDir, "custom_renamed_cli.exe");
            File.Copy(realMby, renamed, overwrite: true);
            var importedRenamed = await store.ImportLocalExecutableAsync(renamed);
            Assert.True(importedRenamed.IsSuccess);
            Assert.Equal(ManagedAppKind.Mby, importedRenamed.Value.Kind);
            Assert.Equal(expectedVersion, importedRenamed.Value.Version);
            Assert.StartsWith("mby-cli-", importedRenamed.Value.FileName, StringComparison.OrdinalIgnoreCase);

            installedMbyFileName = importedRenamed.Value.FileName;
            installedMbyVersion = importedRenamed.Value.Version;
        }

        // Bug #4: failed SaveDownloadedToolAsync must NOT delete the existing working binary.
        var missingTempResult = await store.SaveDownloadedToolAsync("mby", "mby-cli-9.9.9.exe", Path.Combine(_tempRoot, "missing.tmp"));
        Assert.True(missingTempResult.IsFailure);
        Assert.True(File.Exists(Path.Combine(paths.AppsDirectory, installedMbyFileName)));

        // Uninstall removes from apps\ while leaving workspace copies untouched.
        var wsCopy = Path.Combine(_tempRoot, "ws", "helper-tool.exe");
        var wsUninstalledMby = Path.Combine(_tempRoot, "ws", "mby.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(wsCopy)!);
        await File.WriteAllBytesAsync(wsCopy, [(byte)'M', (byte)'Z', 0x01]);
        await File.WriteAllBytesAsync(wsUninstalledMby, [(byte)'M', (byte)'Z', 0x02]);

        var unRes = await store.UninstallAppAsync("custom:helper-tool.exe");
        Assert.True(unRes.IsSuccess);
        Assert.False(File.Exists(Path.Combine(paths.AppsDirectory, "helper-tool.exe")));
        Assert.True(File.Exists(wsCopy));

        var scanned = await store.ScanAppsAsync(Path.Combine(_tempRoot, "ws"));
        Assert.Contains(scanned, a => a.Kind == ManagedAppKind.Mby && a.Version == installedMbyVersion && a.DisplayName == "Meloberry cli" && a.WorkspaceFileName == "mby.exe");
        Assert.DoesNotContain(scanned, a => a.FileName == "helper-tool.exe");

        // Bug #9: when mby is uninstalled from apps\ but mby.exe is present in workspace, report real filename.
        await store.UninstallAppAsync("mby");
        var scannedNoInstall = await store.ScanAppsAsync(Path.Combine(_tempRoot, "ws"));
        var mbyEntry = scannedNoInstall.Single(a => a.Kind == ManagedAppKind.Mby);
        Assert.False(mbyEntry.IsInstalled);
        Assert.True(mbyEntry.IsPresentInWorkspace);
        Assert.Equal("mby.exe", mbyEntry.FileName);
        Assert.Equal("mby.exe", mbyEntry.WorkspaceFileName);
    }

    [Fact]
    public async Task ManagedAppsStore_ResolveToolExecutableAsync_PrefersOverrideThenWorkspaceEvenWhenVersionsDiffer()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "data_resolve"));
        Directory.CreateDirectory(paths.AppsDirectory);
        var wsDir = Path.Combine(_tempRoot, "ws_resolve");
        Directory.CreateDirectory(wsDir);

        var appsMby = Path.Combine(paths.AppsDirectory, "mby-cli-2.0.0.exe");
        var wsMby = Path.Combine(wsDir, "mby-cli-1.9.0.exe");
        var overrideMby = Path.Combine(_tempRoot, "custom-mby.exe");

        await File.WriteAllBytesAsync(appsMby, [(byte)'M', (byte)'Z', 0x01]);
        await File.WriteAllBytesAsync(wsMby, [(byte)'M', (byte)'Z', 0x02]);
        await File.WriteAllBytesAsync(overrideMby, [(byte)'M', (byte)'Z', 0x03]);

        var store = new ManagedAppsStore(paths);

        Assert.Equal(Path.GetFullPath(overrideMby), await store.ResolveToolExecutableAsync("mby", wsDir, overrideMby));
        Assert.Equal(Path.GetFullPath(wsMby), await store.ResolveToolExecutableAsync("mby", wsDir, null));

        var emptyWs = Path.Combine(_tempRoot, "empty_ws_resolve");
        Directory.CreateDirectory(emptyWs);
        Assert.Equal(Path.GetFullPath(appsMby), await store.ResolveToolExecutableAsync("mby", emptyWs, null));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
