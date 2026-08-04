using System.Runtime.InteropServices;

namespace CopyTool.Core;

/// <summary>Small filesystem helpers shared across the projects.</summary>
public static class Files
{
    /// <summary>
    /// Deletes a file if it is there, ignoring the ways that can fail.
    ///
    /// Every caller wants the same thing — get rid of a partial or stale file, and
    /// never let the attempt itself break anything — so the swallow lives here
    /// rather than in four hand-maintained copies.
    /// </summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Clears the read-only attribute if it is set. Returns true when it actually
    /// changed something.
    ///
    /// A read-only destination fails to open for writing with access-denied, which
    /// looks exactly like a permissions problem and is not one: elevating changes
    /// nothing, and prompting for admin rights over a flag the user can clear
    /// themselves is the wrong answer. Explorer clears it too.
    /// </summary>
    public static bool TryClearReadOnly(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;

            FileAttributes attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) == 0) return false;

            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The Win32 error behind an exception, or 0 if it does not carry one.
    /// One decoder so that adding an exception shape fixes every predicate at
    /// once — a miss here makes access-denied look like a hard failure.
    /// </summary>
    public static int Win32ErrorOf(Exception e) => e switch
    {
        System.ComponentModel.Win32Exception w32 => w32.NativeErrorCode,
        IOException io => io.HResult & 0xFFFF,
        _ => 0,
    };

    /// <summary>
    /// Full path of <paramref name="path"/>, without letting a drive root collapse
    /// into something else.
    ///
    /// <c>Path.GetFullPath("C:")</c> does not mean <c>C:\</c> — it means the
    /// process's current directory *on* drive C. Anything that trims a trailing
    /// separator before resolving has to put it back, or a dragged drive root
    /// silently resolves to wherever the program happens to be running from.
    /// </summary>
    public static string FullPath(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar);
        if (trimmed.EndsWith(':')) trimmed += Path.DirectorySeparatorChar;
        return Path.GetFullPath(trimmed);
    }

    /// <summary>Reads a value from an argument list of the form <c>--flag value</c>.</summary>
    public static string? Arg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }
}
