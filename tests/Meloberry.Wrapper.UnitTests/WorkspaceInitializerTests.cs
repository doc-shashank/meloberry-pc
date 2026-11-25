using Meloberry.Wrapper.Domain.Models;
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

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
