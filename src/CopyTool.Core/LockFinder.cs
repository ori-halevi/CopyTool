using System.Runtime.InteropServices;

namespace CopyTool.Core;

/// <summary>
/// Answers "who has this file open?" using the Restart Manager.
///
/// "The file is in use by another program" is one of the least useful messages
/// Windows produces — it names the problem and withholds the one fact needed to
/// fix it. The Restart Manager knows, and asking costs a few milliseconds.
/// </summary>
public static class LockFinder
{
    private const int RmRebootReasonNone = 0;
    private const int CCH_RM_SESSION_KEY = 32;
    private const int ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, nint rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

    /// <summary>
    /// Names the applications holding <paramref name="path"/> open. Returns an
    /// empty list if nothing does, or if the Restart Manager cannot say —
    /// diagnosing a lock must never be able to fail a copy.
    /// </summary>
    public static IReadOnlyList<string> WhoIsUsing(string path)
    {
        uint session;
        string key = Guid.NewGuid().ToString("N")[..CCH_RM_SESSION_KEY];

        if (RmStartSession(out session, 0, key) != 0) return [];

        try
        {
            if (RmRegisterResources(session, 1, [path], 0, 0, 0, null) != 0) return [];

            uint needed = 0, count = 0, reasons = 0;
            int result = RmGetList(session, out needed, ref count, null, ref reasons);

            if (result != ERROR_MORE_DATA || needed == 0) return [];

            var infos = new RM_PROCESS_INFO[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, infos, ref reasons) != 0) return [];

            var names = new List<string>((int)count);
            for (int i = 0; i < count; i++)
            {
                string name = infos[i].strAppName;
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name)) names.Add(name);
            }
            return names;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
        finally
        {
            RmEndSession(session);
        }
    }

    /// <summary>
    /// Applications holding either path open, source first. Names only — the
    /// sentence built around them belongs to whatever is doing the presenting.
    /// </summary>
    public static IReadOnlyList<string> WhoIsUsingAny(params string[] paths)
    {
        foreach (string path in paths)
        {
            IReadOnlyList<string> names = WhoIsUsing(path);
            if (names.Count > 0) return names;
        }
        return [];
    }
}
