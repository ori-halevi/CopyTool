namespace CopyTool.Core;

/// <summary>
/// Shared display formatting. One implementation, because three had already
/// drifted: the same 512-byte file printed "512.0 B" in a preflight warning and
/// "512 B" in the progress line.
/// </summary>
public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Human-readable size. Whole bytes below 1 KB, one decimal above.</summary>
    public static string Bytes(long n)
    {
        double v = n;
        int unit = 0;
        while (v >= 1024 && unit < Units.Length - 1) { v /= 1024; unit++; }

        return unit == 0 ? $"{v:F0} {Units[unit]}" : $"{v:F1} {Units[unit]}";
    }
}
