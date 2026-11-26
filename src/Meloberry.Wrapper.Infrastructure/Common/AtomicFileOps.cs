using Meloberry.Wrapper.Application.Abstractions;

namespace Meloberry.Wrapper.Infrastructure.Common;

/// <summary>Shared atomic temp-file write/copy/move primitives with guaranteed temp cleanup.</summary>
public static class AtomicFileOps
{
    public static async Task WriteAllTextAsync(
        string destinationPath,
        string content,
        CancellationToken ct = default,
        string tempSuffix = ".tmp")
    {
        EnsureParentDirectory(destinationPath);
        var tmp = BuildTempPath(destinationPath, tempSuffix);
        try
        {
            await File.WriteAllTextAsync(tmp, content, ct).ConfigureAwait(false);
            File.Move(tmp, destinationPath, overwrite: true);
        }
        catch
        {
            TryDeleteQuietly(tmp);
            throw;
        }
    }

    public static async Task WriteAllBytesAsync(
        string destinationPath,
        byte[] bytes,
        CancellationToken ct = default,
        string tempSuffix = ".tmp")
    {
        EnsureParentDirectory(destinationPath);
        var tmp = BuildTempPath(destinationPath, tempSuffix);
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, destinationPath, overwrite: true);
        }
        catch
        {
            TryDeleteQuietly(tmp);
            throw;
        }
    }

    public static void CopyFile(string sourcePath, string destinationPath, string tempSuffix = ".tmp")
    {
        EnsureParentDirectory(destinationPath);
        var tmp = BuildTempPath(destinationPath, tempSuffix);
        try
        {
            File.Copy(sourcePath, tmp, overwrite: true);
            File.Move(tmp, destinationPath, overwrite: true);
        }
        catch
        {
            TryDeleteQuietly(tmp);
            throw;
        }
    }

    public static void WriteStream(string destinationPath, Stream contentStream, string tempSuffix = ".tmp")
    {
        ArgumentNullException.ThrowIfNull(contentStream);
        EnsureParentDirectory(destinationPath);
        var tmp = BuildTempPath(destinationPath, tempSuffix);
        try
        {
            using (var fs = File.Create(tmp))
            {
                contentStream.CopyTo(fs);
            }

            File.Move(tmp, destinationPath, overwrite: true);
        }
        catch
        {
            TryDeleteQuietly(tmp);
            throw;
        }
    }

    public static void MoveTempIntoPlace(string tempFilePath, string destinationPath)
    {
        EnsureParentDirectory(destinationPath);
        try
        {
            File.Move(tempFilePath, destinationPath, overwrite: true);
        }
        catch
        {
            TryDeleteQuietly(tempFilePath);
            throw;
        }
    }

    private static void EnsureParentDirectory(string destinationPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static string BuildTempPath(string destinationPath, string tempSuffix)
    {
        var suffix = string.IsNullOrWhiteSpace(tempSuffix) ? ".tmp" : tempSuffix;
        if (!suffix.StartsWith('.'))
        {
            suffix = "." + suffix;
        }

        return $"{destinationPath}.{Guid.NewGuid():N}{suffix}";
    }

    private static void TryDeleteQuietly(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }
    }
}

/// <summary>DI-registered adapter implementing <see cref="IAtomicFileWriter"/> via <see cref="AtomicFileOps"/>.</summary>
public sealed class AtomicFileWriter : IAtomicFileWriter
{
    public Task WriteAllTextAsync(string destinationPath, string content, CancellationToken ct = default) =>
        AtomicFileOps.WriteAllTextAsync(destinationPath, content, ct);

    public Task WriteAllBytesAsync(string destinationPath, byte[] bytes, CancellationToken ct = default) =>
        AtomicFileOps.WriteAllBytesAsync(destinationPath, bytes, ct);

    public void CopyFile(string sourcePath, string destinationPath, string tempSuffix = ".tmp") =>
        AtomicFileOps.CopyFile(sourcePath, destinationPath, tempSuffix);

    public void WriteStream(string destinationPath, Stream contentStream, string tempSuffix = ".tmp") =>
        AtomicFileOps.WriteStream(destinationPath, contentStream, tempSuffix);
}
