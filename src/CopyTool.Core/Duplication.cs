namespace CopyTool.Core;

/// <summary>
/// Copying something into the folder it already lives in.
///
/// This is the one name collision that is not a question. Dropping a file onto
/// the folder it came from cannot be two unrelated files that happen to share a
/// name — there is only one file, and the person is asking for a second copy of
/// it. So there is nothing to ask: the copy is made beside the original under a
/// free name, exactly as Explorer does, and the job says nothing about it.
///
/// Everywhere else a collision still parks and still asks, and that difference is
/// the whole point. A name clash in *another* folder is two genuinely different
/// files, and the person may not know the second one is there — which is when a
/// question earns its interruption.
/// </summary>
public static class Duplication
{
    /// <summary>
    /// Redirects every dragged item that already sits in the destination folder to
    /// a free name beside itself, and returns the scan with those paths rewritten.
    ///
    /// Returns the scan untouched when nothing is being duplicated, which is the
    /// overwhelmingly common case — the check is a string comparison per dragged
    /// item, not per file.
    /// </summary>
    public static ScanResult Rename(
        IReadOnlyList<string> sources, ScanResult scan, string destinationRoot)
    {
        Dictionary<string, string>? renames = Plan(sources, destinationRoot);
        if (renames is null) return scan;

        return scan with
        {
            Files = [.. scan.Files.Select(f => f with { RelativePath = Rewrite(f.RelativePath, renames) })],
            Directories = [.. scan.Directories.Select(d => Rewrite(d, renames))],
        };
    }

    /// <summary>
    /// Maps the name of each dragged item that came out of the destination folder
    /// to the free name its copy will take. Null when none did.
    /// </summary>
    private static Dictionary<string, string>? Plan(
        IReadOnlyList<string> sources, string destinationRoot)
    {
        string destination;
        try
        {
            destination = Path.TrimEndingDirectorySeparator(Files.FullPath(destinationRoot));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        Dictionary<string, string>? renames = null;

        foreach (string source in sources)
        {
            string full;
            try { full = Files.FullPath(source); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            // A drive root has no parent, so it can never be duplicated into itself
            // — and the preflight blocks that case as source-equals-destination.
            string? parent = Path.GetDirectoryName(full);
            if (parent is null) continue;

            if (!string.Equals(Path.TrimEndingDirectorySeparator(parent), destination,
                               StringComparison.OrdinalIgnoreCase))
                continue;

            string name = Path.GetFileName(full);
            if (name.Length == 0) continue;

            // Asked of the real path, so an existing "x - Copy" is stepped over
            // rather than overwritten — including one that is itself part of this
            // same drop.
            string free = ConflictResolver.CopyName(full, Directory.Exists(full));

            renames ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            renames[name] = Path.GetFileName(free);
        }

        return renames;
    }

    /// <summary>
    /// Swaps the first segment of a destination-relative path.
    ///
    /// That segment is always the dragged item's own name — the scanner builds
    /// every relative path underneath it — so replacing it redirects an entire
    /// tree in one step, and the folder's contents keep their structure inside the
    /// renamed copy.
    /// </summary>
    private static string Rewrite(string relativePath, Dictionary<string, string> renames)
    {
        int separator = relativePath.IndexOf(Path.DirectorySeparatorChar);
        string head = separator < 0 ? relativePath : relativePath[..separator];

        if (!renames.TryGetValue(head, out string? replacement)) return relativePath;

        return separator < 0 ? replacement : replacement + relativePath[separator..];
    }
}
