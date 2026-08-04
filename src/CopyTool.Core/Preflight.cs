namespace CopyTool.Core;

public enum IssueSeverity
{
    /// <summary>The job cannot proceed as asked.</summary>
    Blocking,
    /// <summary>It will proceed, but the user should know first.</summary>
    Warning,
}

public enum PreflightCode
{
    SameSourceAndDestination,
    DestinationInsideSource,
    AlreadyAtDestination,
    NotEnoughSpace,
    LittleSpaceLeft,
    FileTooLargeForFilesystem,
    UnsupportedCharacters,
    ReservedNames,
    TrailingSpaceOrDot,
    NameTooLong,
    CloudPlaceholders,
    ReparsePointsSkipped,
    Inaccessible,
}

/// <summary>
/// One finding. A code plus the numbers behind it — the wording is the UI's job,
/// so the same finding can be logged, translated or shown without Core knowing
/// which is happening.
/// </summary>
public sealed record PreflightIssue(
    IssueSeverity Severity,
    PreflightCode Code,
    string? Path = null,
    long Count = 0,
    long Bytes = 0,
    long OtherBytes = 0,
    string? FileSystem = null);

public sealed record PreflightResult
{
    public required IReadOnlyList<PreflightIssue> Issues { get; init; }

    public bool CanProceed => Issues.All(i => i.Severity != IssueSeverity.Blocking);
    public IEnumerable<PreflightIssue> Blocking => Issues.Where(i => i.Severity == IssueSeverity.Blocking);
    public IEnumerable<PreflightIssue> Warnings => Issues.Where(i => i.Severity == IssueSeverity.Warning);
}

/// <summary>
/// Everything worth knowing before the first byte moves.
///
/// The point of checking up front is that all of these are cheap to detect and
/// expensive to discover late: running out of space at 90%, a name the destination
/// filesystem cannot represent, or a 6 GB file headed for FAT32 all fail *after*
/// the time has been spent. Explorer finds them the hard way.
/// </summary>
public static class Preflight
{
    private const long FatMaxFileSize = 4L * 1024 * 1024 * 1024 - 1;

    /// <summary>Characters no FAT/exFAT/SMB destination will accept.</summary>
    private static readonly char[] HostileChars = [':', '*', '?', '"', '<', '>', '|'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <param name="freeSpace">
    /// How much room the destination has. Injectable only so the space rules can be
    /// tested — the branch that matters is the one that refuses a job, and a test
    /// cannot fill a disk to reach it.
    /// </param>
    public static PreflightResult Run(
        IReadOnlyList<string> sources, string destinationRoot, ScanResult scan, CopyOperation operation,
        Func<string, long>? freeSpace = null)
    {
        var issues = new List<PreflightIssue>();

        CheckPaths(sources, destinationRoot, operation, issues);
        CheckFreeSpace(destinationRoot, scan, operation, freeSpace ?? VolumeProfiler.FreeBytes, issues);
        CheckFilesystemLimits(destinationRoot, scan, issues);
        CheckNames(scan, issues);
        CheckCloudPlaceholders(scan, issues);

        if (scan.SkippedReparsePoints.Count > 0)
            issues.Add(new PreflightIssue(IssueSeverity.Warning, PreflightCode.ReparsePointsSkipped,
                                          Count: scan.SkippedReparsePoints.Count));

        if (scan.Inaccessible.Count > 0)
            issues.Add(new PreflightIssue(IssueSeverity.Warning, PreflightCode.Inaccessible,
                                          Count: scan.Inaccessible.Count));

        return new PreflightResult { Issues = issues };
    }

    private static void CheckPaths(
        IReadOnlyList<string> sources, string destinationRoot, CopyOperation operation, List<PreflightIssue> issues)
    {
        string dest = Normalise(destinationRoot);

        foreach (string source in sources)
        {
            string src = Normalise(source);

            if (string.Equals(src, dest, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new PreflightIssue(IssueSeverity.Blocking,
                    PreflightCode.SameSourceAndDestination, source));
                continue;
            }

            // Copying a folder into itself walks forever, re-copying what it just
            // wrote. Cheap to detect here, impossible to recover from later.
            if (dest.StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new PreflightIssue(IssueSeverity.Blocking,
                    PreflightCode.DestinationInsideSource, source));
                continue;
            }

            if (operation == CopyOperation.Move &&
                string.Equals(Path.GetDirectoryName(src), dest, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new PreflightIssue(IssueSeverity.Blocking,
                    PreflightCode.AlreadyAtDestination, source));
            }
        }
    }

    private static void CheckFreeSpace(
        string destinationRoot, ScanResult scan, CopyOperation operation,
        Func<string, long> freeSpace, List<PreflightIssue> issues)
    {
        long needed = BytesThatMustBeWritten(destinationRoot, scan, operation);
        if (needed <= 0) return;

        // Read now, not from the volume profile: the profile is cached for the life
        // of the process, and free space is exactly the property that does not hold
        // still — least of all between two copies onto the same disk.
        long free = freeSpace(destinationRoot);
        if (free <= 0) return;                          // unknown, e.g. a network share

        if (needed > free)
        {
            // On the point of refusing the job, so it is now worth a metadata read
            // per file to find out whether those bytes would really be written.
            // Re-copying a tree the destination already holds needs no room at all,
            // and blocking that is refusing to do nothing.
            needed -= BytesAlreadyThere(destinationRoot, scan);

            if (needed > free)
            {
                issues.Add(new PreflightIssue(IssueSeverity.Blocking, PreflightCode.NotEnoughSpace,
                                              Bytes: needed, OtherBytes: free));
                return;
            }
        }

        if (free - needed < Math.Min(1L << 30, free / 20))
        {
            issues.Add(new PreflightIssue(IssueSeverity.Warning, PreflightCode.LittleSpaceLeft,
                                          Bytes: free - needed));
        }
    }

    /// <summary>
    /// Bytes the destination already holds in identical form.
    ///
    /// Same size and same timestamp within the tolerance — the test the engine
    /// itself uses to decide a file is already there. For such a file the space
    /// required is zero under every policy: skipping writes nothing, parking writes
    /// nothing, and copying anyway replaces a file of exactly that size. "Keep
    /// both" cannot apply, because sameness is settled before the conflict rules
    /// get a say.
    /// </summary>
    private static long BytesAlreadyThere(string destinationRoot, ScanResult scan)
    {
        long already = 0;

        foreach (ScanItem file in scan.Files)
        {
            try
            {
                var destination = new FileInfo(Path.Combine(destinationRoot, file.RelativePath));
                if (!destination.Exists || destination.Length != file.Size) continue;

                DateTime source = file.Modified != default
                    ? file.Modified
                    : ConflictResolver.SafeWriteTime(file.SourcePath);

                if ((source - destination.LastWriteTimeUtc).Duration() > ConflictResolver.TimeTolerance)
                    continue;

                already += file.Size;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Cannot tell, so assume we will have to write it. Erring towards
                // refusing beats promising room that is not there.
            }
        }

        return already;
    }

    /// <summary>
    /// How much has to be written at the destination.
    ///
    /// A move within one volume is a rename and needs no space at all — but that
    /// is decided per file, not from the first one. A multi-selection can span
    /// volumes, which is the same assumption the engine's rename path had to stop
    /// making: judging the whole job by <c>Files[0]</c> skipped the space check
    /// entirely whenever the first file happened to be on the destination volume.
    /// </summary>
    private static long BytesThatMustBeWritten(string destinationRoot, ScanResult scan, CopyOperation operation)
    {
        if (operation != CopyOperation.Move) return scan.TotalBytes;

        long needed = 0;
        foreach (ScanItem file in scan.Files)
        {
            if (!VolumeProfiler.SameVolume(file.SourcePath, destinationRoot)) needed += file.Size;
        }
        return needed;
    }

    private static void CheckFilesystemLimits(string destinationRoot, ScanResult scan, List<PreflightIssue> issues)
    {
        VolumeProfile dst = VolumeProfiler.ForPath(destinationRoot);
        bool isFat = dst.FileSystem.StartsWith("FAT", StringComparison.OrdinalIgnoreCase);

        // NTFS accepts these; FAT, exFAT and many SMB servers do not.
        bool restrictedNames = isFat
            || dst.FileSystem.Equals("exFAT", StringComparison.OrdinalIgnoreCase)
            || dst.Media == MediaKind.Network;

        if (!restrictedNames) return;

        // One pass for both questions rather than a Count() each.
        int tooBig = 0, hostile = 0;
        foreach (ScanItem file in scan.Files)
        {
            if (isFat && file.Size > FatMaxFileSize) tooBig++;
            if (restrictedNames && file.RelativePath.IndexOfAny(HostileChars) >= 0) hostile++;
        }

        if (tooBig > 0)
            issues.Add(new PreflightIssue(IssueSeverity.Blocking, PreflightCode.FileTooLargeForFilesystem,
                                          Count: tooBig, FileSystem: dst.FileSystem));

        if (hostile > 0)
            issues.Add(new PreflightIssue(IssueSeverity.Warning, PreflightCode.UnsupportedCharacters,
                                          Count: hostile, FileSystem: dst.FileSystem));
    }

    private static void CheckNames(ScanResult scan, List<PreflightIssue> issues)
    {
        int reserved = 0, trailing = 0, tooLong = 0;

        foreach (ScanItem file in scan.Files)
        {
            string name = Path.GetFileName(file.RelativePath);
            string stem = Path.GetFileNameWithoutExtension(name);

            if (ReservedNames.Contains(stem)) reserved++;

            // A name ending in a space or dot cannot be created through the normal
            // Win32 path rules, even though NTFS itself allows it.
            if (name.Length > 0 && (name[^1] == ' ' || name[^1] == '.')) trailing++;

            // Individual path components are capped at 255 regardless of \\?\.
            if (name.Length > 255) tooLong++;
        }

        if (reserved > 0)
            issues.Add(new PreflightIssue(IssueSeverity.Warning, PreflightCode.ReservedNames, Count: reserved));

        if (trailing > 0)
            issues.Add(new PreflightIssue(IssueSeverity.Warning, PreflightCode.TrailingSpaceOrDot, Count: trailing));

        if (tooLong > 0)
            issues.Add(new PreflightIssue(IssueSeverity.Blocking, PreflightCode.NameTooLong, Count: tooLong));
    }

    /// <summary>
    /// OneDrive "files on demand" entries look like ordinary files but hold no
    /// data locally. Copying one silently pulls it down from the cloud, which on a
    /// large folder means a very long wait and a lot of metered traffic.
    /// </summary>
    private static void CheckCloudPlaceholders(ScanResult scan, List<PreflightIssue> issues)
    {
        if (scan.PlaceholderCount == 0) return;

        issues.Add(new PreflightIssue(IssueSeverity.Warning, PreflightCode.CloudPlaceholders,
                                      Count: scan.PlaceholderCount, Bytes: scan.PlaceholderBytes));
    }

    private static string Normalise(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
