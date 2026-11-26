using System.Security.Cryptography;

namespace Meloberry.Wrapper.Infrastructure.Workspace;

/// <summary>Byte-level file identity checks using length pre-filter and SHA-256 comparison.</summary>
public static class FileIdentity
{
    public static async Task<bool> AreIdenticalAsync(string fileA, string fileB, CancellationToken ct = default)
    {
        try
        {
            var infoA = new FileInfo(fileA);
            var infoB = new FileInfo(fileB);
            if (!infoA.Exists || !infoB.Exists || infoA.Length != infoB.Length)
            {
                return false;
            }

            await using var streamA = File.OpenRead(fileA);
            await using var streamB = File.OpenRead(fileB);
            var hashA = await SHA256.HashDataAsync(streamA, ct).ConfigureAwait(false);
            var hashB = await SHA256.HashDataAsync(streamB, ct).ConfigureAwait(false);
            return hashA.AsSpan().SequenceEqual(hashB);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
