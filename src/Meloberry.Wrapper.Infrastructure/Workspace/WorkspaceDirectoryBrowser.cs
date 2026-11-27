using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Errors;
using Meloberry.Wrapper.Domain.Models;

namespace Meloberry.Wrapper.Infrastructure.Workspace;

/// <summary>Enumerates non-hidden subfolders and read-only file names for the Active Workspace tree popup.</summary>
public sealed class WorkspaceDirectoryBrowser : IWorkspaceDirectoryBrowser
{
    public Task<Result<WorkspaceDirectoryListing>> ListDirectoryAsync(string directoryPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!PathNormalization.TryNormalizeDirectoryPath(directoryPath, out var fullPath) || !Directory.Exists(fullPath))
        {
            return Task.FromResult(Result<WorkspaceDirectoryListing>.Failure(
                WrapperErrors.InvalidWorkspaceDir(directoryPath)));
        }

        try
        {
            var dirInfo = new DirectoryInfo(fullPath);
            var parentPath = dirInfo.Parent?.FullName;

            var folders = new List<WorkspaceTreeFolder>();
            foreach (var sub in dirInfo.EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                         .Where(IsVisibleEntry)
                         .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                folders.Add(new WorkspaceTreeFolder(sub.Name, sub.FullName, SafeHasSubfolders(sub)));
            }

            var files = dirInfo.EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(IsVisibleEntry)
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new WorkspaceTreeFile(f.Name, f.Extension))
                .ToList();

            return Task.FromResult(Result<WorkspaceDirectoryListing>.Success(
                new WorkspaceDirectoryListing(fullPath, parentPath, folders, files)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<WorkspaceDirectoryListing>.Failure(
                WrapperErrors.InvalidWorkspaceDir($"{fullPath}: {ex.Message}")));
        }
    }

    private static bool IsVisibleEntry(FileSystemInfo entry) =>
        (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0
        && !entry.Name.StartsWith(".", StringComparison.Ordinal);

    private static bool SafeHasSubfolders(DirectoryInfo dir)
    {
        try
        {
            return dir.EnumerateDirectories("*", SearchOption.TopDirectoryOnly).Any(IsVisibleEntry);
        }
        catch
        {
            return false;
        }
    }
}
