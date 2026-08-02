using System.Security.Principal;
using CopyTool.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace CopyTool.Core;

/// <summary>What the current process can do, and what a given path demands.</summary>
public static class Elevation
{
    private static readonly SecurityIdentifier AdminSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static bool? _isElevated;
    private static bool? _isAdmin;

    /// <summary>True when this process already runs with an elevated token.</summary>
    public static bool IsElevated
    {
        get
        {
            if (_isElevated is null)
            {
                using var identity = WindowsIdentity.GetCurrent();
                _isElevated = new WindowsPrincipal(identity)
                    .IsInRole(WindowsBuiltInRole.Administrator);
            }
            return _isElevated.Value;
        }
    }

    /// <summary>
    /// True when the user could elevate at all — i.e. the Administrators SID is in
    /// the token even if only as deny-only, which is how a filtered admin token
    /// looks before elevation. A standard user gets a credential prompt instead of
    /// a consent prompt, and usually has no admin password to give, so it is worth
    /// wording the request differently for them.
    /// </summary>
    public static bool CanElevate
    {
        get
        {
            if (_isAdmin is null)
            {
                using var identity = WindowsIdentity.GetCurrent();
                _isAdmin = identity.Groups?.Any(g => g == AdminSid) == true
                           || IsElevated
                           || HasDenyOnlyAdmins(identity);
            }
            return _isAdmin.Value;
        }
    }

    private static bool HasDenyOnlyAdmins(WindowsIdentity identity)
    {
        // A filtered admin token keeps the Administrators SID but marks it
        // deny-only, so it does not show up as a normal group membership.
        try
        {
            return identity.Claims.Any(c => c.Value == AdminSid.Value);
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// UAC turned off entirely (EnableLUA=0). Everything an admin runs is already
    /// elevated, so asking would be meaningless.
    /// </summary>
    public static bool UacDisabled
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
                return key?.GetValue("EnableLUA") is int v && v == 0;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Can this process create files in <paramref name="directory"/>?
    ///
    /// Asking the ACL is unreliable — inherited denies, ownership and privileges
    /// all interfere — so the check is simply to try it. The probe file is opened
    /// delete-on-close, so nothing is left behind even on a hard kill.
    /// </summary>
    public static bool CanWriteTo(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }

        string probe = Path.Combine(directory, $".copytool-probe-{Guid.NewGuid():N}");

        using SafeFileHandle h = Win32.CreateFile(
            probe, Win32.GENERIC_WRITE, 0, 0, Win32.CREATE_ALWAYS,
            Win32.FILE_ATTRIBUTE_NORMAL | Win32.FILE_FLAG_DELETE_ON_CLOSE, 0);

        return !h.IsInvalid;
    }

    /// <summary>Can this process read <paramref name="path"/>?</summary>
    public static bool CanRead(string path)
    {
        using SafeFileHandle h = Win32.CreateFile(
            path, Win32.GENERIC_READ, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, 0,
            Win32.OPEN_EXISTING, Win32.FILE_FLAG_BACKUP_SEMANTICS, 0);

        return !h.IsInvalid;
    }

    /// <summary>ERROR_ACCESS_DENIED — the file exists, we are simply not allowed.</summary>
    public static bool IsAccessDenied(Exception e) =>
        e is UnauthorizedAccessException || Files.Win32ErrorOf(e) == 5;
}

/// <summary>What the preflight concluded about a job's permission needs.</summary>
public sealed record ElevationCheck
{
    public required bool DestinationNeedsElevation { get; init; }
    public required IReadOnlyList<string> UnreadableSources { get; init; }

    public bool AnythingNeedsElevation =>
        DestinationNeedsElevation || UnreadableSources.Count > 0;

    /// <summary>
    /// Runs before the copy starts so the banner can appear immediately, instead
    /// of the job stopping halfway through the way Explorer's does.
    /// </summary>
    public static ElevationCheck Run(IEnumerable<string> sources, string destinationRoot)
    {
        bool destBlocked = !Elevation.CanWriteTo(destinationRoot);

        var unreadable = new List<string>();
        foreach (string s in sources)
        {
            if (!Elevation.CanRead(s)) unreadable.Add(s);
        }

        return new ElevationCheck
        {
            DestinationNeedsElevation = destBlocked,
            UnreadableSources = unreadable,
        };
    }
}
