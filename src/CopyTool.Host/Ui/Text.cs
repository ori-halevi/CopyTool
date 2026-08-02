using CopyTool.Core;

namespace CopyTool.Host.Ui;

/// <summary>
/// Turns the engine's codes into Hebrew.
///
/// Every user-facing sentence in the application is here. Core reports what
/// happened as a code plus numbers and never composes a sentence, so the same
/// finding can be logged verbatim, shown to a person, or one day translated —
/// without the engine knowing which of those is happening.
/// </summary>
internal static class Text
{
    public static string Describe(PreflightIssue issue) => issue.Code switch
    {
        PreflightCode.SameSourceAndDestination => "המקור והיעד זהים",
        PreflightCode.DestinationInsideSource  => "היעד נמצא בתוך תיקיית המקור",
        PreflightCode.AlreadyAtDestination     => "הפריט כבר נמצא ביעד",

        PreflightCode.NotEnoughSpace =>
            $"אין מספיק מקום ביעד — דרושים {Format.Bytes(issue.Bytes)} " +
            $"ופנויים {Format.Bytes(issue.OtherBytes)}",

        PreflightCode.LittleSpaceLeft =>
            $"אחרי ההעתקה יישארו ביעד פחות מ-{Format.Bytes(issue.Bytes)}",

        PreflightCode.FileTooLargeForFilesystem =>
            $"{issue.Count:N0} קבצים גדולים מ-4GB, ו-{issue.FileSystem} לא תומכת בזה",

        PreflightCode.UnsupportedCharacters =>
            $"{issue.Count:N0} שמות מכילים תווים ש-{issue.FileSystem} לא מקבלת — יש לשנות שם",

        PreflightCode.ReservedNames =>
            $"{issue.Count:N0} קבצים בשם שמור של Windows (CON, PRN וכדומה)",

        PreflightCode.TrailingSpaceOrDot =>
            $"{issue.Count:N0} שמות מסתיימים ברווח או בנקודה",

        PreflightCode.NameTooLong =>
            $"{issue.Count:N0} שמות ארוכים מ-255 תווים — לא ניתן ליצור אותם",

        PreflightCode.CloudPlaceholders =>
            $"{issue.Count:N0} קבצים שמורים בענן בלבד ({Format.Bytes(issue.Bytes)}) — " +
            "העתקתם תוריד אותם תחילה",

        PreflightCode.ReparsePointsSkipped =>
            $"{issue.Count:N0} קישורים (junction/symlink) לא ייסרקו ולא יועתקו",

        PreflightCode.Inaccessible =>
            $"{issue.Count:N0} פריטים לא נגישים לקריאה",

        _ => issue.Code.ToString(),
    };

    public static string Describe(SkippedItem item) => item.Reason switch
    {
        SkipReason.Identical         => "זהה למקור",
        SkipReason.AlreadyExists     => "כבר קיים ביעד",
        SkipReason.DestinationNewer  => "הקובץ ביעד חדש יותר",
        SkipReason.DestinationLarger => "הקובץ ביעד גדול יותר",
        SkipReason.Locked            => DescribeHolders(item.Holders) ?? "הקובץ נעול או בשימוש",
        SkipReason.IoError           => item.SystemMessage ?? "שגיאת קריאה או כתיבה",
        _ => item.Reason.ToString(),
    };

    /// <summary>The one-line verdict shown for a parked decision.</summary>
    public static string Describe(PendingDecision decision) => decision.Kind switch
    {
        DecisionKind.Identical => "זהה למקור",
        DecisionKind.Locked    => DescribeHolders(decision.Holders) ?? "הקובץ נעול או בשימוש",
        DecisionKind.IoError   => decision.SystemMessage ?? "שגיאת קריאה או כתיבה",
        _ => "כבר קיים ביעד",
    };

    public static string Describe(ElevationError error) => error switch
    {
        ElevationError.AlreadyRequested    => "בקשה כבר ממתינה",
        ElevationError.WorkerDidNotConnect => "העובד המורם לא התחבר",
        ElevationError.WorkerStopped       => "העובד המורם נעצר באמצע",
        _ => "ההרמה נכשלה",
    };

    /// <summary>
    /// "The file is in use" without saying by what is the least useful message
    /// Windows produces; this is the whole point of asking the Restart Manager.
    /// </summary>
    public static string? DescribeHolders(IReadOnlyList<string>? holders) =>
        holders is null || holders.Count == 0
            ? null
            : $"בשימוש ע\"י {string.Join(", ", holders)}";
}
