using System.Security.Cryptography;

namespace CopyTool.Core;

/// <summary>
/// Confirms that what landed is what left.
///
/// A copy can be reported as complete and still be wrong — a cable, a failing
/// sector, a device lying about a flush. Nothing in the copy path can detect
/// that, because every layer has already agreed the write succeeded. The only
/// way to know is to read the bytes back.
///
/// SHA-256 rather than a faster non-cryptographic hash on purpose: verification
/// re-reads both files, so it moves twice the file's size through the disk, and
/// hardware-accelerated SHA-256 runs comfortably ahead of any drive this tool
/// will meet. The bottleneck is the platter or the flash, not the arithmetic —
/// and this way Core keeps its zero dependencies.
/// </summary>
public static class Verifier
{
    /// <summary>Read size for verification. Large enough to keep the queue busy.</summary>
    private const int BufferSize = 1 << 20;

    public static async Task<byte[]> HashAsync(string path, CancellationToken ct = default)
    {
        using var sha = SHA256.Create();
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] buffer = new byte[BufferSize];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            sha.TransformBlock(buffer, 0, read, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return sha.Hash!;
    }

    /// <summary>
    /// True when both files hold the same bytes.
    ///
    /// Sizes are compared first: a length mismatch is the common failure and
    /// costs one metadata read to rule out, against reading both files in full.
    /// The two hashes are then computed concurrently, which is free when the
    /// files are on different drives and harmless when they are not.
    /// </summary>
    public static async Task<bool> AreIdenticalAsync(string first, string second, CancellationToken ct = default)
    {
        try
        {
            var a = new FileInfo(first);
            var b = new FileInfo(second);
            if (!a.Exists || !b.Exists || a.Length != b.Length) return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        Task<byte[]> left = HashAsync(first, ct);
        Task<byte[]> right = HashAsync(second, ct);
        await Task.WhenAll(left, right).ConfigureAwait(false);

        return left.Result.AsSpan().SequenceEqual(right.Result);
    }
}
