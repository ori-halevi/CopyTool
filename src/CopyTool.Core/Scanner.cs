namespace CopyTool.Core;

/// <summary>
/// One file to copy. <paramref name="Modified"/> comes free out of the directory
/// entry the walker already read, and carrying it here saves a metadata syscall
/// per file later on when conflicts are resolved.
/// </summary>
public sealed record ScanItem(string SourcePath, string RelativePath, long Size, DateTime Modified = default);

public sealed record ScanResult
{
    public required IReadOnlyList<ScanItem> Files { get; init; }
    /// <summary>Relative directory paths, parents before children, ready to create in order.</summary>
    public required IReadOnlyList<string> Directories { get; init; }
    public required long TotalBytes { get; init; }
    /// <summary>Reparse points that were not followed, reported so the caller can decide.</summary>
    public required IReadOnlyList<string> SkippedReparsePoints { get; init; }
    public required IReadOnlyList<string> Inaccessible { get; init; }

    /// <summary>Files whose data lives in the cloud and would be downloaded on access.</summary>
    public required int PlaceholderCount { get; init; }
    public required long PlaceholderBytes { get; init; }

    public int FileCount => Files.Count;

    /// <summary>
    /// Size distribution — 200k tiny files and one 200 GB file want opposite
    /// settings, and only this tells them apart before the copy starts.
    /// Counted during the scan, not recomputed per read.
    /// </summary>
    public required (long Tiny, long Small, long Medium, long Large) Histogram { get; init; }
}

public static class Scanner
{
    // Not exposed by FileAttributes. Set by OneDrive and similar providers on
    // entries whose contents are not stored locally.
    private const int FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;
    private const int FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;

    internal static bool IsCloudPlaceholder(FileAttributes attrs) =>
        ((int)attrs & (FILE_ATTRIBUTE_RECALL_ON_OPEN | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS)) != 0;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,          // we want hidden and system entries too
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Walks the sources and returns everything that has to be created or copied.
    /// Reparse points are recorded but never followed — that alone makes junction
    /// loops impossible, which is why no cycle bookkeeping is needed.
    /// </summary>
    private sealed class ScanState
    {
        public readonly List<ScanItem> Files = [];
        public readonly List<string> Dirs = [];
        public readonly List<string> Reparse = [];
        public readonly List<string> Inaccessible = [];
        public int PlaceholderCount;
        public long PlaceholderBytes;

        public long Tiny, Small, Medium, Large;

        public void AddFile(string absolute, string relative, long size, DateTime modified, FileAttributes attrs)
        {
            Files.Add(new ScanItem(absolute, relative, size, modified));

            // Bucketed here, during the walk that already touched every entry,
            // rather than by an O(N) property recomputed on each read.
            if (size < 64 * 1024) Tiny++;
            else if (size < 1L << 20) Small++;
            else if (size < 64L << 20) Medium++;
            else Large++;

            if (IsCloudPlaceholder(attrs))
            {
                PlaceholderCount++;
                PlaceholderBytes += size;
            }
        }
    }

    public static ScanResult Scan(IEnumerable<string> sources, CancellationToken ct = default)
    {
        var state = new ScanState();
        List<ScanItem> files = state.Files;
        List<string> dirs = state.Dirs;
        List<string> reparse = state.Reparse;
        List<string> inaccessible = state.Inaccessible;
        long total = 0;

        foreach (string source in sources)
        {
            ct.ThrowIfCancellationRequested();
            string full = Path.GetFullPath(source.TrimEnd(Path.DirectorySeparatorChar));

            FileAttributes attrs;
            try { attrs = File.GetAttributes(full); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                inaccessible.Add(full);
                continue;
            }

            string name = Path.GetFileName(full);
            if (string.IsNullOrEmpty(name)) name = full.TrimEnd('\\', ':');  // drive root

            if ((attrs & FileAttributes.Directory) == 0)
            {
                long size;
                DateTime modified;
                try
                {
                    // One FileInfo, both values: it is a single metadata read.
                    var info = new FileInfo(full);
                    size = info.Length;
                    modified = info.LastWriteTimeUtc;
                }
                catch (IOException) { inaccessible.Add(full); continue; }

                state.AddFile(full, name, size, modified, attrs);
                total += size;
                continue;
            }

            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                reparse.Add(full);
                continue;
            }

            dirs.Add(name);
            total += WalkDirectory(full, name, state, ct);
        }

        return new ScanResult
        {
            Files = files, Directories = dirs, TotalBytes = total,
            SkippedReparsePoints = reparse, Inaccessible = inaccessible,
            PlaceholderCount = state.PlaceholderCount,
            PlaceholderBytes = state.PlaceholderBytes,
            Histogram = (state.Tiny, state.Small, state.Medium, state.Large),
        };
    }

    private static long WalkDirectory(string absolute, string relative, ScanState state, CancellationToken ct)
    {
        List<string> dirs = state.Dirs;
        List<string> reparse = state.Reparse;
        List<string> inaccessible = state.Inaccessible;
        long total = 0;

        // Explicit stack rather than recursion: deep trees are routine and a
        // 10k-level tree would blow the call stack.
        var pending = new Stack<(string Absolute, string Relative)>();
        pending.Push((absolute, relative));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dirAbs, dirRel) = pending.Pop();

            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dirAbs).EnumerateFileSystemInfos("*", Options); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                inaccessible.Add(dirAbs);
                continue;
            }

            using var it = entries.GetEnumerator();
            while (true)
            {
                // MoveNext itself can throw mid-enumeration on a vanishing directory.
                try { if (!it.MoveNext()) break; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    inaccessible.Add(dirAbs);
                    break;
                }

                FileSystemInfo entry = it.Current;
                string childRel = Path.Combine(dirRel, entry.Name);

                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    reparse.Add(entry.FullName);
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    dirs.Add(childRel);
                    pending.Push((entry.FullName, childRel));
                }
                else if (entry is FileInfo fi)
                {
                    // fi is already populated from the directory entry, so Length,
                    // LastWriteTimeUtc and Attributes are all free here.
                    state.AddFile(fi.FullName, childRel, fi.Length, fi.LastWriteTimeUtc, fi.Attributes);
                    total += fi.Length;
                }
            }
        }

        return total;
    }
}
