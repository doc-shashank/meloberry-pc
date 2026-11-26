using System.Text;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.Common;
using Meloberry.Wrapper.Infrastructure.Persistence;
using Meloberry.Wrapper.Infrastructure.Workspace;
using Xunit;

namespace Meloberry.Wrapper.UnitTests;

public sealed class WorkspaceInitializerTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mby_ws_test_{Guid.NewGuid():N}");

    [Fact]
    public async Task InitializeAsync_CopiesExesAndFilesToRoot_PreservesFolderSubdir_AndNeverDeletesExisting()
    {
        var dataDir = Path.Combine(_tempRoot, "data");
        var paths = new WrapperDataPaths(dataDir);
        Directory.CreateDirectory(paths.AppsDirectory);
        var mbyFake = Path.Combine(paths.AppsDirectory, "mby-cli-2.0.0.exe");
        await File.WriteAllBytesAsync(mbyFake, [(byte)'M', (byte)'Z', 0x90, 0x00]);

        var srcStage = Path.Combine(_tempRoot, "sources");
        var chapterDir = Path.Combine(srcStage, "Chapter02");
        Directory.CreateDirectory(Path.Combine(chapterDir, "figs"));
        var looseFile = Path.Combine(srcStage, "notes.txt");
        await File.WriteAllTextAsync(looseFile, "Action potential notes");
        await File.WriteAllTextAsync(Path.Combine(chapterDir, "slides.md"), "# Slides");
        await File.WriteAllBytesAsync(Path.Combine(chapterDir, "figs", "a.png"), [1, 2, 3]);

        var workspace = Path.Combine(_tempRoot, "ActiveWorkspace");
        Directory.CreateDirectory(workspace);
        var preExisting = Path.Combine(workspace, "keep_me.txt");
        await File.WriteAllTextAsync(preExisting, "original user file");

        var initializer = new WorkspaceInitializer(paths, new WebAppAnalysisStore());
        var staged = new List<StagedCopyItem>
        {
            new(looseFile, StagedCopyKind.File, "notes.txt", "notes.txt"),
            new(chapterDir, StagedCopyKind.Folder, "Chapter02", "Chapter02")
        };

        var res = await initializer.InitializeAsync(
            workspace,
            ["mby-cli-2.0.0.exe"],
            staged,
            ConflictPolicy.SkipExisting);

        Assert.True(res.IsSuccess);
        Assert.True(File.Exists(Path.Combine(workspace, "mby-cli-2.0.0.exe")));
        Assert.True(File.Exists(Path.Combine(workspace, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(workspace, "Chapter02", "slides.md")));
        Assert.True(File.Exists(Path.Combine(workspace, "Chapter02", "figs", "a.png")));
        Assert.Equal("original user file", await File.ReadAllTextAsync(preExisting));
        Assert.Equal(Path.Combine(workspace, "mby-cli-2.0.0.exe"), res.Value.WorkspaceMbyPath);
    }

    [Fact]
    public async Task InitializeAsync_RespectsSkipVsOverwriteConflicts_AndSkipsIdentical()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "data2"));
        var srcDir = Path.Combine(_tempRoot, "src2");
        Directory.CreateDirectory(srcDir);
        var fileA = Path.Combine(srcDir, "a.txt");
        var fileB = Path.Combine(srcDir, "b.txt");
        await File.WriteAllTextAsync(fileA, "same-content");
        await File.WriteAllTextAsync(fileB, "new-content");

        var workspace = Path.Combine(_tempRoot, "ws2");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "a.txt"), "same-content");
        await File.WriteAllTextAsync(Path.Combine(workspace, "b.txt"), "old-content");

        var initializer = new WorkspaceInitializer(paths, new WebAppAnalysisStore());
        var staged = new List<StagedCopyItem>
        {
            new(fileA, StagedCopyKind.File, "a.txt", "a.txt"),
            new(fileB, StagedCopyKind.File, "b.txt", "b.txt")
        };

        var plan = await initializer.PlanAsync(workspace, [], staged);
        Assert.True(plan.IsSuccess);
        Assert.Equal(1, plan.Value.SkippedIdenticalCount);
        Assert.Single(plan.Value.Conflicts);

        var skipRes = await initializer.InitializeAsync(workspace, [], staged, ConflictPolicy.SkipExisting);
        Assert.True(skipRes.IsSuccess);
        Assert.Equal("old-content", await File.ReadAllTextAsync(Path.Combine(workspace, "b.txt")));

        var overRes = await initializer.InitializeAsync(workspace, [], staged, ConflictPolicy.OverwriteExisting);
        Assert.True(overRes.IsSuccess);
        Assert.Equal(1, overRes.Value.OverwrittenCount);
        Assert.Equal("new-content", await File.ReadAllTextAsync(Path.Combine(workspace, "b.txt")));
    }

    [Fact]
    public async Task PlanAsync_BlocksCopyingFolderIntoItselfOrDescendant_AndRejectsDriveRoot()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "data3"));
        var parentFolder = Path.Combine(_tempRoot, "Parent");
        var childWorkspace = Path.Combine(parentFolder, "Nested");
        Directory.CreateDirectory(childWorkspace);
        await File.WriteAllTextAsync(Path.Combine(parentFolder, "doc.md"), "hi");

        var initializer = new WorkspaceInitializer(paths, new WebAppAnalysisStore());
        var res = await initializer.PlanAsync(
            childWorkspace,
            [],
            [new StagedCopyItem(parentFolder, StagedCopyKind.Folder, "Parent", "Parent")]);

        Assert.True(res.IsFailure);
        Assert.Equal("Workspace.RecursiveCopyBlocked", res.Error!.Code);

        var driveRoot = Path.GetPathRoot(Environment.CurrentDirectory)!;
        var driveRes = await initializer.PlanAsync(
            childWorkspace,
            [],
            [new StagedCopyItem(driveRoot, StagedCopyKind.Folder, driveRoot, driveRoot)]);

        Assert.True(driveRes.IsFailure);
        Assert.Equal("Security.InvalidPath", driveRes.Error!.Code);
    }

    [Fact]
    public async Task PlanAndInitialize_DetectsIntraBatchCollisions_AndTracksAllEnsuredRelativePaths()
    {
        var paths = new WrapperDataPaths(Path.Combine(_tempRoot, "data_batch"));
        var dirA = Path.Combine(_tempRoot, "A");
        var dirB = Path.Combine(_tempRoot, "B");
        var figs1 = Path.Combine(_tempRoot, "Term1", "Figs");
        var figs2 = Path.Combine(_tempRoot, "Term2", "Figs");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        Directory.CreateDirectory(figs1);
        Directory.CreateDirectory(figs2);

        var fileA = Path.Combine(dirA, "notes.txt");
        var fileB = Path.Combine(dirB, "notes.txt");
        var identicalSrc = Path.Combine(dirA, "same.txt");
        await File.WriteAllTextAsync(fileA, "from-A");
        await File.WriteAllTextAsync(fileB, "from-B");
        await File.WriteAllTextAsync(identicalSrc, "already-there");
        await File.WriteAllTextAsync(Path.Combine(figs1, "fig.png"), "fig-1");
        await File.WriteAllTextAsync(Path.Combine(figs2, "fig.png"), "fig-2");

        var workspace = Path.Combine(_tempRoot, "ws_batch");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "same.txt"), "already-there");

        var initializer = new WorkspaceInitializer(paths, new WebAppAnalysisStore());
        var staged = new List<StagedCopyItem>
        {
            new(identicalSrc, StagedCopyKind.File, "same.txt", "same.txt"),
            new(fileA, StagedCopyKind.File, "notes.txt", "notes.txt"),
            new(fileB, StagedCopyKind.File, "notes.txt", "notes.txt"),
            new(figs1, StagedCopyKind.Folder, "Figs", "Figs"),
            new(figs2, StagedCopyKind.Folder, "Figs", "Figs")
        };

        var plan = await initializer.PlanAsync(workspace, [], staged);
        Assert.True(plan.IsSuccess);
        Assert.Equal(1, plan.Value.SkippedIdenticalCount);
        Assert.Equal(2, plan.Value.Conflicts.Count);
        Assert.Contains(plan.Value.Conflicts, c => c.RelativeTargetPath == "notes.txt");
        Assert.Contains(plan.Value.Conflicts, c => c.RelativeTargetPath == "Figs/fig.png");

        var skipRes = await initializer.InitializeAsync(workspace, [], staged, ConflictPolicy.SkipExisting);
        Assert.True(skipRes.IsSuccess);
        Assert.Equal("from-A", await File.ReadAllTextAsync(Path.Combine(workspace, "notes.txt")));
        Assert.Equal("fig-1", await File.ReadAllTextAsync(Path.Combine(workspace, "Figs", "fig.png")));
        Assert.Contains("same.txt", skipRes.Value.CopiedRelativePaths);
        Assert.Contains("notes.txt", skipRes.Value.CopiedRelativePaths);
        Assert.Contains("Figs/fig.png", skipRes.Value.CopiedRelativePaths);

        var workspaceOver = Path.Combine(_tempRoot, "ws_batch_over");
        Directory.CreateDirectory(workspaceOver);
        var overRes = await initializer.InitializeAsync(workspaceOver, [], staged, ConflictPolicy.OverwriteExisting);
        Assert.True(overRes.IsSuccess);
        Assert.Equal("from-B", await File.ReadAllTextAsync(Path.Combine(workspaceOver, "notes.txt")));
        Assert.Equal("fig-2", await File.ReadAllTextAsync(Path.Combine(workspaceOver, "Figs", "fig.png")));
    }

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

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
