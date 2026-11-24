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

        // Metadata-based universal import: copy real mby-cli under an arbitrary filename and verify detection!
        var realMby = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "CLI", "app", "mby-cli-2.0.0.exe"));
        if (File.Exists(realMby))
        {
            var renamed = Path.Combine(importSourceDir, "custom_renamed_cli.exe");
            File.Copy(realMby, renamed, overwrite: true);
            var importedRenamed = await store.ImportLocalExecutableAsync(renamed);
            Assert.True(importedRenamed.IsSuccess);
            Assert.Equal(ManagedAppKind.Mby, importedRenamed.Value.Kind);
            Assert.Equal("2.0.0", importedRenamed.Value.Version);
            Assert.StartsWith("mby-cli-", importedRenamed.Value.FileName, StringComparison.OrdinalIgnoreCase);
        }

        // Uninstall removes from apps\ while leaving workspace copies untouched.
        var wsCopy = Path.Combine(_tempRoot, "ws", "helper-tool.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(wsCopy)!);
        await File.WriteAllBytesAsync(wsCopy, [(byte)'M', (byte)'Z', 0x01]);

        var unRes = await store.UninstallAppAsync("custom:helper-tool.exe");
        Assert.True(unRes.IsSuccess);
        Assert.False(File.Exists(Path.Combine(paths.AppsDirectory, "helper-tool.exe")));
        Assert.True(File.Exists(wsCopy));

        var scanned = await store.ScanAppsAsync(null);
        Assert.Contains(scanned, a => a.Kind == ManagedAppKind.Mby && a.Version == "2.0.0" && a.DisplayName == "Meloberry cli");
        Assert.DoesNotContain(scanned, a => a.FileName == "helper-tool.exe");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
