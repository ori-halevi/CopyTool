using System.ComponentModel;
using System.Runtime.CompilerServices;
using CopyTool.Core;

namespace CopyTool.Host.Ui;

/// <summary>
/// What the progress window shows for one job.
///
/// The engine reports progress far more often than a human can read, so raw
/// numbers are smoothed and formatted here rather than in the view.
/// </summary>
public sealed class JobViewModel : INotifyPropertyChanged
{
    private readonly JobControl _control;
    private readonly Queue<(DateTime At, long Bytes)> _rateWindow = new();

    public JobViewModel(string operation, string destination, JobControl control, JobPolicies policies)
    {
        Operation = operation;
        Destination = destination;
        _control = control;
        Policies = policies;
        _control.PausedChanged += _ => { Notify(nameof(IsPaused)); Notify(nameof(PauseLabel)); };

        Chips =
        [
            new PolicyChip("התנגשות",
            [
                new PolicyOption("שאל", ConflictPolicy.Ask),
                new PolicyOption("דלג", ConflictPolicy.Skip),
                new PolicyOption("דרוס", ConflictPolicy.Overwrite),
                new PolicyOption("שמור שניהם", ConflictPolicy.KeepBoth),
                new PolicyOption("שמור החדש", ConflictPolicy.KeepNewer),
                new PolicyOption("שמור הגדול", ConflictPolicy.KeepLarger),
            ], () => policies.Conflict, v => policies.Conflict = (ConflictPolicy)v, ConflictPolicy.Ask),

            new PolicyChip("זהים",
            [
                new PolicyOption("דלג ודווח", IdenticalPolicy.SkipAndReport),
                new PolicyOption("שאל", IdenticalPolicy.Ask),
                new PolicyOption("העתק בכל זאת", IdenticalPolicy.CopyAnyway),
            ], () => policies.Identical, v => policies.Identical = (IdenticalPolicy)v, IdenticalPolicy.SkipAndReport),

            new PolicyChip("נעול",
            [
                new PolicyOption("נסה שוב", LockedPolicy.Retry),
                new PolicyOption("שאל", LockedPolicy.Ask),
                new PolicyOption("דלג", LockedPolicy.Skip),
            ], () => policies.Locked, v => policies.Locked = (LockedPolicy)v, LockedPolicy.Retry),

            new PolicyChip("שגיאות",
            [
                new PolicyOption("נסה שוב ×3", IoErrorPolicy.RetryThrice),
                new PolicyOption("שאל", IoErrorPolicy.Ask),
                new PolicyOption("דלג ורשום", IoErrorPolicy.SkipAndLog),
            ], () => policies.IoError, v => policies.IoError = (IoErrorPolicy)v, IoErrorPolicy.RetryThrice),

            new PolicyChip("הרשאות",
            [
                new PolicyOption("שאל", ElevationPolicy.Ask),
                new PolicyOption("הרם מיד", ElevationPolicy.ElevateNow),
                new PolicyOption("דלג על מוגנים", ElevationPolicy.SkipProtected),
            ], () => policies.Elevation, v => policies.Elevation = (ElevationPolicy)v, ElevationPolicy.Ask),

            new PolicyChip("עדיפות",
            [
                new PolicyOption("מלאה", false),
                new PolicyOption("רקע", true),
            ], () => policies.BackgroundIo, v => policies.BackgroundIo = (bool)v, false),
        ];
    }

    public string Operation { get; }
    public string Destination { get; }
    public JobPolicies Policies { get; }
    public IReadOnlyList<PolicyChip> Chips { get; }

    private long _bytesDone, _bytesTotal;
    private int _filesDone, _filesTotal;
    private string? _currentFile;
    private double _bytesPerSecond;
    private bool _finished;
    private string? _summary;

    public double Percent => _bytesTotal > 0 ? 100.0 * _bytesDone / _bytesTotal : 0;

    public string Title => _finished
        ? _summary ?? "הושלם"
        : $"{Operation} {ItemCount} ← {Destination}";

    private string ItemCount => _filesTotal == 1 ? "פריט אחד" : $"{_filesTotal:N0} פריטים";

    /// <summary>
    /// Numbers only, rendered left-to-right. Kept apart from <see cref="Eta"/>
    /// because bidi reordering scrambles the order of Latin runs when they are
    /// mixed into a right-to-left paragraph.
    /// </summary>
    public string Numbers => _finished
        ? ""
        : $"{Format.Bytes(_bytesDone)} / {Format.Bytes(_bytesTotal)}  ·  {Format.Bytes((long)_bytesPerSecond)}/s";

    public string Stats => _finished ? _summary ?? "" : "";

    public string Eta
    {
        get
        {
            if (_finished) return "";
            if (_bytesPerSecond <= 0 || _bytesDone >= _bytesTotal) return "מחשב…";

            var left = TimeSpan.FromSeconds((_bytesTotal - _bytesDone) / _bytesPerSecond);
            if (left.TotalHours >= 1) return $"נותרו {left.Hours} שעות ו-{left.Minutes} דקות";
            if (left.TotalMinutes >= 1) return $"נותרו {left.Minutes} דקות";
            int seconds = Math.Max(1, left.Seconds);
            return seconds == 1 ? "נותרה שנייה" : $"נותרו {seconds} שניות";
        }
    }

    public string CurrentFile => _currentFile ?? "";
    public bool IsPaused => _control.IsPaused;
    public string PauseLabel => _control.IsPaused ? "המשך" : "השהה";
    public bool IsRunning => !_finished;

    private int _pendingCount, _skippedCount;

    private string? _conflictProgress;

    /// <summary>Items parked for a decision. The copy never waited for them.</summary>
    public string PendingText => _conflictProgress
        ?? (_pendingCount == 1 ? "ממתין להחלטה אחת" : $"{_pendingCount:N0} ממתינים להחלטה");

    public bool HasPending => _conflictProgress is not null || _pendingCount > 0;

    public void SetConflictProgress(string? message)
    {
        _conflictProgress = message;
        Notify(nameof(PendingText));
        Notify(nameof(HasPending));
    }

    /// <summary>Skipping is never silent — this is always visible when it happened.</summary>
    public string SkippedText => _skippedCount == 1 ? "פריט אחד דולג" : $"{_skippedCount:N0} דולגו";
    public bool HasSkipped => _skippedCount > 0;

    // --- preflight ----------------------------------------------------------
    private string? _blockedReason;
    private string[] _warnings = [];

    public bool IsBlocked => _blockedReason is not null;
    public string BlockedText => _blockedReason ?? "";

    /// <summary>Non-fatal findings, shown once rather than repeated per file.</summary>
    public bool HasWarnings => _warnings.Length > 0;
    public string WarningsText => string.Join("\n", _warnings);

    /// <summary>The job cannot run as asked; say why and copy nothing.</summary>
    public void Block(string reason)
    {
        _blockedReason = reason;
        _finished = true;
        _summary = reason;
        NotifyAll();
        Notify(nameof(IsBlocked));
        Notify(nameof(BlockedText));
    }

    public void SetWarnings(string[] warnings)
    {
        _warnings = warnings;
        Notify(nameof(HasWarnings));
        Notify(nameof(WarningsText));
    }

    // --- elevation banner ---------------------------------------------------
    private int _needsElevationCount;
    private string? _elevationMessage;
    private bool _elevationResolved;
    private bool _elevationBusy;

    /// <summary>
    /// Shown from the first second when the preflight already knows, rather than
    /// after the copy has stalled. The copy keeps running while it is on screen.
    /// </summary>
    public bool ShowElevationBanner => !_elevationResolved && (_needsElevationCount > 0 || _elevationMessage is not null);

    public string ElevationText => _elevationMessage ?? (_needsElevationCount == 1
        ? "פריט אחד דורש הרשאות מנהל"
        : $"{_needsElevationCount:N0} פריטים דורשים הרשאות מנהל");

    public bool CanRequestElevation => !_elevationBusy && !_elevationResolved && _needsElevationCount > 0;

    public event Action? ElevationRequested;

    public void RequestElevation()
    {
        if (!CanRequestElevation) return;
        _elevationBusy = true;
        Notify(nameof(CanRequestElevation));
        ElevationRequested?.Invoke();
    }

    public void SetElevationNeeded(int count)
    {
        _needsElevationCount = count;
        NotifyElevation();
    }

    public void SetElevationMessage(string? message, bool resolved)
    {
        _elevationMessage = message;
        _elevationResolved = resolved;
        _elevationBusy = false;
        NotifyElevation();
    }

    private void NotifyElevation()
    {
        Notify(nameof(ShowElevationBanner));
        Notify(nameof(ElevationText));
        Notify(nameof(CanRequestElevation));
    }

    public void Update(CopyProgress p)
    {
        _bytesDone = p.BytesDone;
        _bytesTotal = p.BytesTotal;
        _filesDone = p.FilesDone;
        _filesTotal = p.FilesTotal;
        _pendingCount = p.PendingCount;
        _skippedCount = p.SkippedCount;
        if (p.NeedsElevationCount > _needsElevationCount) _needsElevationCount = p.NeedsElevationCount;
        if (p.CurrentFile is not null) _currentFile = p.CurrentFile;

        // The engine's own rate is sampled once a second and jumps around; a short
        // sliding window makes the number readable without hiding real changes.
        var now = DateTime.UtcNow;
        _rateWindow.Enqueue((now, p.BytesDone));
        while (_rateWindow.Count > 1 && (now - _rateWindow.Peek().At).TotalSeconds > 3)
            _rateWindow.Dequeue();

        if (_rateWindow.Count > 1)
        {
            var (firstAt, firstBytes) = _rateWindow.Peek();
            double seconds = (now - firstAt).TotalSeconds;
            if (seconds > 0.2) _bytesPerSecond = (p.BytesDone - firstBytes) / seconds;
        }

        NotifyAll();
    }

    public void Finish(CopyReport report, bool cancelled)
    {
        _finished = true;
        _pendingCount = report.Pending.Count;
        _skippedCount = report.Skipped.Count;

        string seconds = $"{report.Elapsed.TotalSeconds:F1} שניות";
        _summary = cancelled
            ? $"בוטל — {report.FilesCopied:N0} מתוך {_filesTotal:N0} הועתקו"
            : report.Failures.Count > 0
                ? $"הושלם עם {report.Failures.Count} שגיאות — {Format.Bytes(report.BytesCopied)} ב-{seconds}"
                : $"הושלם — {report.FilesCopied:N0} פריטים, {Format.Bytes(report.BytesCopied)} ב-{seconds}";

        NotifyAll();
    }

    public void TogglePause() => _control.Toggle();
    public void Cancel() => _control.Cancel();

    private void NotifyAll()
    {
        Notify(nameof(Percent)); Notify(nameof(Title)); Notify(nameof(Stats));
        Notify(nameof(Numbers)); Notify(nameof(Eta));
        Notify(nameof(CurrentFile)); Notify(nameof(IsRunning));
        Notify(nameof(PendingText)); Notify(nameof(HasPending));
        Notify(nameof(SkippedText)); Notify(nameof(HasSkipped));
        NotifyElevation();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

