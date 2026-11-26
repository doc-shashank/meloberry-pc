using System.Text;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.Common;
using Meloberry.Wrapper.Infrastructure.Workspace;
using Xunit;

namespace Meloberry.Wrapper.UnitTests;

public sealed class Phase3ConsolidationTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_phase3_test_{Guid.NewGuid():N}");

    [Fact]
    public async Task AtomicFileOps_And_AtomicFileWriter_WriteCopyStreamCleanlyWithoutLeftoverTemps()
    {
        var writer = new AtomicFileWriter();
        var subDir = Path.Combine(_tempRoot, "nested", "dir");
        var textTarget = Path.Combine(subDir, "notes.txt");
        var bytesTarget = Path.Combine(subDir, "blob.bin");
        var copyTarget = Path.Combine(subDir, "copied.bin");
        var streamTarget = Path.Combine(subDir, "streamed.txt");

        await writer.WriteAllTextAsync(textTarget, "first");
        await writer.WriteAllTextAsync(textTarget, "second");
        Assert.Equal("second", await File.ReadAllTextAsync(textTarget));

        await writer.WriteAllBytesAsync(bytesTarget, [1, 2, 3, 4]);
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(bytesTarget));

        writer.CopyFile(bytesTarget, copyTarget, ".mbytmp");
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(copyTarget));

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("stream-payload"));
        writer.WriteStream(streamTarget, ms);
        Assert.Equal("stream-payload", await File.ReadAllTextAsync(streamTarget));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AtomicFileOps.WriteAllTextAsync(Path.Combine(subDir, "cancelled.txt"), "data", cts.Token));

        var leftoverTemps = Directory.GetFiles(subDir, "*.tmp")
            .Concat(Directory.GetFiles(subDir, "*.mbytmp"))
            .ToList();
        Assert.Empty(leftoverTemps);
        Assert.False(File.Exists(Path.Combine(subDir, "cancelled.txt")));
    }

    [Fact]
    public async Task FileIdentity_AreIdenticalAsync_ComparesLengthsAndSha256()
    {
        Directory.CreateDirectory(_tempRoot);
        var a = Path.Combine(_tempRoot, "a.bin");
        var bSame = Path.Combine(_tempRoot, "b_same.bin");
        var cSameLenDiff = Path.Combine(_tempRoot, "c_diff.bin");
        var dDiffLen = Path.Combine(_tempRoot, "d_short.bin");

        await File.WriteAllBytesAsync(a, [10, 20, 30, 40]);
        await File.WriteAllBytesAsync(bSame, [10, 20, 30, 40]);
        await File.WriteAllBytesAsync(cSameLenDiff, [10, 20, 30, 41]);
        await File.WriteAllBytesAsync(dDiffLen, [10, 20]);

        Assert.True(await FileIdentity.AreIdenticalAsync(a, bSame));
        Assert.False(await FileIdentity.AreIdenticalAsync(a, cSameLenDiff));
        Assert.False(await FileIdentity.AreIdenticalAsync(a, dDiffLen));
        Assert.False(await FileIdentity.AreIdenticalAsync(a, Path.Combine(_tempRoot, "missing.bin")));
    }

    [Fact]
    public void SemverComparer_ExtractVersion_HandlesExePrereleaseAndBuildMetadata()
    {
        Assert.Equal("2.0.0", SemverComparer.ExtractVersion("mby-cli-2.0.0.exe"));
        Assert.Equal("2.0.0-rc.1", SemverComparer.ExtractVersion("mby-cli-2.0.0-rc.1.exe"));
        Assert.Equal("1.2.3", SemverComparer.ExtractVersion("1.2.3+abc123"));
        Assert.Null(SemverComparer.ExtractVersion(null));
        Assert.Null(SemverComparer.ExtractVersion("mby.exe"));
    }

    [Fact]
    public void SpecCatalogDefaults_GetCategoryForTopic_MapsBlocksQuestionsContainersAndUnknown()
    {
        Assert.Equal("block", SpecCatalogDefaults.GetCategoryForTopic("text"));
        Assert.Equal("block", SpecCatalogDefaults.GetCategoryForTopic("LINK"));
        Assert.Equal("question", SpecCatalogDefaults.GetCategoryForTopic("mcq"));
        Assert.Equal("question", SpecCatalogDefaults.GetCategoryForTopic("relabel"));
        Assert.Equal("container", SpecCatalogDefaults.GetCategoryForTopic("session"));
        Assert.Equal("other", SpecCatalogDefaults.GetCategoryForTopic("unknown-topic"));
    }

    [Fact]
    public async Task ManagedAppsStore_ResolveToolExecutableAsync_PrefersOverrideThenWorkspaceEvenWhenVersionsDiffer()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "data"));
        Directory.CreateDirectory(paths.AppsDirectory);
        var wsDir = Path.Combine(_tempRoot, "ws");
        Directory.CreateDirectory(wsDir);

        var appsMby = Path.Combine(paths.AppsDirectory, "mby-cli-2.0.0.exe");
        var wsMby = Path.Combine(wsDir, "mby-cli-1.9.0.exe");
        var overrideMby = Path.Combine(_tempRoot, "custom-mby.exe");

        await File.WriteAllBytesAsync(appsMby, [(byte)'M', (byte)'Z', 0x01]);
        await File.WriteAllBytesAsync(wsMby, [(byte)'M', (byte)'Z', 0x02]);
        await File.WriteAllBytesAsync(overrideMby, [(byte)'M', (byte)'Z', 0x03]);

        var store = new ManagedAppsStore(paths);

        // 1. Explicit override wins over both workspace and apps\.
        Assert.Equal(Path.GetFullPath(overrideMby), await store.ResolveToolExecutableAsync("mby", wsDir, overrideMby));

        // 2. Workspace binary wins over apps\ even when versions differ (mby-cli-1.9.0.exe vs mby-cli-2.0.0.exe).
        Assert.Equal(Path.GetFullPath(wsMby), await store.ResolveToolExecutableAsync("mby", wsDir, null));

        // 3. Falls back to apps\ when workspace is absent or has no matching tool.
        var emptyWs = Path.Combine(_tempRoot, "empty_ws");
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
