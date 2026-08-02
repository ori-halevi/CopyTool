using System.Runtime.InteropServices;

namespace CopyTool.Host;

/// <summary>
/// Keeps the idle host invisible in every sense that matters: no window, no tray
/// icon, no timers, and a working set small enough that it does not register in
/// Task Manager's memory column.
/// </summary>
internal static partial class Ghost
{
    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(nint hProcess);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    /// <summary>
    /// Pushes the process's pages out to the standby list. They fault back in on
    /// demand, so the only cost is the next job starting a few milliseconds later —
    /// paid once, while idle, in exchange for a near-zero resident footprint.
    /// </summary>
    public static void TrimWorkingSet()
    {
        // Collect first, or the trimmed set still reflects garbage we no longer need.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        EmptyWorkingSet(GetCurrentProcess());
    }

    public static long WorkingSetBytes => Environment.WorkingSet;
}
