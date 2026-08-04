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
public static class Text
{
    /// <summary>
    /// How long is left, in Hebrew, with the agreement the language actually needs.
    ///
    /// Two separate problems. Hebrew has a dual form — <em>שעתיים</em>, <em>יומיים</em> —
    /// and the verb agrees with the noun, so "נותרו 1 שעות" is not merely clumsy,
    /// it reads as broken software. And the unit has to be the largest one that
    /// fits: <see cref="TimeSpan.Hours"/> is 0-23, so a copy with thirty hours to
    /// go used to report six.
    /// </summary>
    public static string Remaining(TimeSpan left)
    {
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;

        Quantity big;
        Quantity? small = null;

        if (left.TotalDays >= 1)
        {
            big = Days((int)left.TotalDays);
            if (left.Hours > 0) small = Hours(left.Hours);
        }
        else if (left.TotalHours >= 1)
        {
            big = Hours(left.Hours);
            if (left.Minutes > 0) small = Minutes(left.Minutes);
        }
        else if (left.TotalMinutes >= 1)
        {
            big = Minutes(left.Minutes);
        }
        else
        {
            // Never "0 שניות": the copy is still running, so the honest floor is one.
            big = Seconds(Math.Max(1, left.Seconds));
        }

        if (small is null) return $"{big.Verb} {big.Text}";

        // ו attaches straight to a word and takes a hyphen before a numeral:
        // "3 ימים ושעתיים", but "3 ימים ו-4 שעות".
        string and = char.IsDigit(small.Value.Text[0]) ? "ו-" : "ו";
        return $"{big.Verb} {big.Text} {and}{small.Value.Text}";
    }

    /// <summary>A quantity and the verb form that agrees with it.</summary>
    private readonly record struct Quantity(string Text, string Verb);

    private static Quantity Days(int n) => n switch
    {
        1 => new("יום", "נותר"),
        2 => new("יומיים", "נותרו"),
        _ => new($"{n} ימים", "נותרו"),
    };

    private static Quantity Hours(int n) => n switch
    {
        1 => new("שעה", "נותרה"),
        2 => new("שעתיים", "נותרו"),
        _ => new($"{n} שעות", "נותרו"),
    };

    private static Quantity Minutes(int n) => n switch
    {
        1 => new("דקה", "נותרה"),
        2 => new("שתי דקות", "נותרו"),
        _ => new($"{n} דקות", "נותרו"),
    };

    private static Quantity Seconds(int n) => n switch
    {
        1 => new("שנייה", "נותרה"),
        2 => new("שתי שניות", "נותרו"),
        _ => new($"{n} שניות", "נותרו"),
    };

    /// <summary>
    /// Shown when a hand-off cannot be acted on at all. Silence is the one answer
    /// a drag-and-drop tool must never give.
    /// </summary>
    public const string JobNotRecognised = "לא ניתן לקרוא את הבקשה — הפעולה לא בוצעה";

    /// <summary>Something went wrong that no other message covers.</summary>
    public const string UnexpectedFailure = "הפעולה נעצרה עקב שגיאה בלתי צפויה — הפרטים ביומן";

    /// <summary>
    /// A count of items, with the agreement Hebrew requires. "1 פריטים" is the
    /// same kind of broken as "1 שעות", and it appears in the one line every job
    /// ends on.
    /// </summary>
    public static string Items(long n) => n switch
    {
        1 => "פריט אחד",
        2 => "שני פריטים",
        _ => $"{n:N0} פריטים",
    };

    /// <summary>
    /// The files a job did not copy because something went wrong with them, named.
    ///
    /// The only skips worth a word at the end. "Skip locked files" is a rule its
    /// owner set and already knows the meaning of — but *which* file was locked,
    /// and what was holding it, is the part no rule could have told them. Without
    /// this the file leaves no trace at all once the window closes.
    /// </summary>
    /// <param name="unanswered">
    /// Conflicts the user was shown and closed without answering. Not the same as
    /// answering "skip" — that is a decision, and repeating it back is noise. This
    /// is a question that went unanswered, so its file is not there and nothing
    /// else records which one.
    /// </param>
    public static string[] DescribeNotCopied(
        IReadOnlyList<SkippedItem> items,
        IReadOnlyList<PendingDecision>? unanswered = null,
        int limit = 8)
    {
        unanswered ??= [];
        int total = items.Count + unanswered.Count;
        if (total == 0) return [];

        var lines = new List<string>
        {
            total == 1 ? $"{Items(total)} לא הועתק:" : $"{Items(total)} לא הועתקו:",
        };

        foreach (SkippedItem item in items.Take(limit))
            lines.Add(NotCopiedLine(item.Source, Describe(item)));

        foreach (PendingDecision item in unanswered.Take(Math.Max(0, limit - items.Count)))
            lines.Add(NotCopiedLine(item.Source, "ההתנגשות נותרה ללא הכרעה"));

        if (total > limit) lines.Add($"    ועוד {total - limit:N0}");

        return [.. lines];
    }

    private static string NotCopiedLine(string source, string reason) =>
        $"    {System.IO.Path.GetFileName(source)} — {reason}";

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
