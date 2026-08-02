namespace CopyTool.Core;

public enum ResolutionAction
{
    /// <summary>Copy to <see cref="Resolution.Destination"/> — which may be a free name.</summary>
    Copy,
    /// <summary>Do not copy; record why.</summary>
    Skip,
    /// <summary>Park for the user and keep going.</summary>
    Defer,
}

/// <summary>
/// The decision, plus the destination metadata that reaching it required. Carrying
/// it out avoids re-reading the same three values from disk when the caller parks
/// the item — which, with "ask" as the default policy, is every conflicting file.
/// </summary>
public readonly record struct Resolution(
    ResolutionAction Action,
    string Destination,
    SkipReason Reason,
    long DestinationSize = 0,
    DateTime SourceModified = default,
    DateTime DestinationModified = default);

public static class ConflictResolver
{
    /// <summary>
    /// FAT stores timestamps at 2-second granularity, so identical files copied to
    /// a FAT volume come back differing by up to two seconds. Anything tighter
    /// produces false conflicts on exactly the copies people repeat most.
    ///
    /// Public because the conflict dialog's bulk actions apply the same rules and
    /// must not re-encode the number.
    /// </summary>
    public static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>True when the source is meaningfully newer than the destination.</summary>
    public static bool IsNewer(DateTime source, DateTime destination) =>
        source > destination + TimeTolerance;

    /// <summary>Decides what to do about one file whose destination already exists.</summary>
    public static Resolution Resolve(ScanItem item, string destination, JobPolicies policies)
    {
        FileInfo dst;
        try
        {
            dst = new FileInfo(destination);
            if (!dst.Exists) return new Resolution(ResolutionAction.Copy, destination, SkipReason.AlreadyExists);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Cannot even look at it — let the copy attempt produce the real error.
            return new Resolution(ResolutionAction.Copy, destination, SkipReason.AlreadyExists);
        }

        // The scan already read the source timestamp out of the directory entry;
        // asking the filesystem again would be a syscall per conflicting file.
        DateTime srcTime = item.Modified != default ? item.Modified : SafeWriteTime(item.SourcePath);
        DateTime dstTime = dst.LastWriteTimeUtc;
        long dstSize = dst.Length;

        Resolution Result(ResolutionAction action, string target, SkipReason reason) =>
            new(action, target, reason, dstSize, srcTime, dstTime);

        // Same size and same timestamp: re-copying achieves nothing, and on a large
        // re-run this is the difference between seconds and hours.
        if (dstSize == item.Size && (srcTime - dstTime).Duration() <= TimeTolerance)
        {
            return policies.Identical switch
            {
                IdenticalPolicy.SkipAndReport => Result(ResolutionAction.Skip, destination, SkipReason.Identical),
                IdenticalPolicy.CopyAnyway    => Result(ResolutionAction.Copy, destination, SkipReason.Identical),
                _                             => Result(ResolutionAction.Defer, destination, SkipReason.Identical),
            };
        }

        return policies.Conflict switch
        {
            ConflictPolicy.Skip      => Result(ResolutionAction.Skip, destination, SkipReason.AlreadyExists),
            ConflictPolicy.Overwrite => Result(ResolutionAction.Copy, destination, SkipReason.AlreadyExists),
            ConflictPolicy.KeepBoth  => Result(ResolutionAction.Copy, FreeName(destination), SkipReason.AlreadyExists),

            ConflictPolicy.KeepNewer => IsNewer(srcTime, dstTime)
                ? Result(ResolutionAction.Copy, destination, SkipReason.AlreadyExists)
                : Result(ResolutionAction.Skip, destination, SkipReason.DestinationNewer),

            ConflictPolicy.KeepLarger => item.Size > dstSize
                ? Result(ResolutionAction.Copy, destination, SkipReason.AlreadyExists)
                : Result(ResolutionAction.Skip, destination, SkipReason.DestinationLarger),

            _ => Result(ResolutionAction.Defer, destination, SkipReason.AlreadyExists),
        };
    }

    /// <summary>Builds a "name (2).ext" style path that does not exist yet.</summary>
    public static string FreeName(string path)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);

        for (int n = 2; n < 10000; n++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        // Pathological directory; fall back to something guaranteed unique.
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    public static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    public static long SafeSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }
}
