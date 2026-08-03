using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using CopyTool.Core;

namespace CopyTool.Host.Ui;

public enum JobStatus { Queued, Running, Finished }

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
            ], () => policies.Elevation, v => SetElevationPolicy((ElevationPolicy)v), ElevationPolicy.Ask),

            new PolicyChip("אימות",
            [
                new PolicyOption("כבוי", VerifyPolicy.Off),
                new PolicyOption("קרא ובדוק", VerifyPolicy.EveryFile),
            ], () => policies.Verify, v => policies.Verify = (VerifyPolicy)v, VerifyPolicy.Off),

            new PolicyChip("עדיפות",
            [
                new PolicyOption("מלאה", false),
                new PolicyOption("רקע", true),
            ], () => policies.BackgroundIo, v => policies.BackgroundIo = (bool)v, false),
        ];
    }

    public string Operation { get; }
    public string Destination { get; }

    // --- place in the queue -------------------------------------------------
    private JobStatus _status = JobStatus.Queued;
    private bool _isPrimary;

    public JobStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Notify();
            Notify(nameof(StatusText));
            Notify(nameof(IsQueued));
        }
    }

    /// <summary>True for the one job shown in full; the rest are single rows.</summary>
    public bool IsPrimary
    {
        get => _isPrimary;
        set { if (_isPrimary != value) { _isPrimary = value; Notify(); } }
    }

    public bool IsQueued => _status == JobStatus.Queued;

    public string StatusText => _status switch
    {
        JobStatus.Queued => "ממתין בתור",
        JobStatus.Running => Numbers,
        _ => _summary ?? "הושלם",
    };

    /// <summary>One line for the queue list: what, and where to.</summary>
    public string ShortTitle => $"{Operation} ← {System.IO.Path.GetFileName(Destination.TrimEnd('\\'))}";
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

    // --- speed graph --------------------------------------------------------
    private const int HistoryLength = 90;             // ~18 seconds at 5 samples/sec
    private readonly Queue<double> _rateHistory = new(HistoryLength);
    private int _sinceLastSample;

    /// <summary>
    /// The recent throughput, as a polyline in a 100x100 box that the view
    /// stretches to fit.
    ///
    /// Scaled to the highest rate seen so far rather than to an absolute ceiling:
    /// the useful information is the shape — a copy holding steady, tailing off
    /// on a fragmented region, or stalling — and an absolute scale would flatten
    /// every USB transfer into a line along the bottom.
    /// </summary>
    public PointCollection SpeedGraph { get; private set; } = [];

    /// <summary>
    /// Shown only once there is throughput to draw.
    ///
    /// With every sample at zero the polyline is degenerate — every point on one
    /// line — and a stretched-to-fill degenerate shape renders as a bar across
    /// the middle of the box, which reads as a healthy steady copy and is the
    /// opposite of the truth.
    /// </summary>
    public bool HasSpeedGraph => _rateHistory.Count >= 2 && _rateHistory.Max() > 0;

    private void SampleRate()
    {
        // The engine reports far more often than the graph needs; one point per
        // ~200 ms keeps the window meaningful rather than a jittering blur.
        if (++_sinceLastSample < 2) return;
        _sinceLastSample = 0;

        _rateHistory.Enqueue(_bytesPerSecond);
        while (_rateHistory.Count > HistoryLength) _rateHistory.Dequeue();

        if (_rateHistory.Count < 2) return;

        double[] rates = [.. _rateHistory];
        double peak = rates.Max();
        if (peak <= 0) peak = 1;

        var points = new PointCollection(rates.Length);
        for (int i = 0; i < rates.Length; i++)
        {
            double x = 100.0 * i / (rates.Length - 1);
            double y = 100.0 - 100.0 * rates[i] / peak;      // y grows downwards
            points.Add(new Point(x, y));
        }

        points.Freeze();
        SpeedGraph = points;

        Notify(nameof(SpeedGraph));
        Notify(nameof(HasSpeedGraph));
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
        Status = JobStatus.Finished;
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

    /// <summary>
    /// "Elevate now" means now. Picking it is the answer to a question the job has
    /// not asked yet, so the prompt goes up on the click rather than waiting for
    /// something to be refused first — that is the whole point of choosing it: to
    /// walk away and come back to a finished copy.
    ///
    /// If the job has not started (it is still waiting its turn in the queue) there
    /// is nothing to elevate into yet. The policy is set, and the coordinator
    /// raises the prompt when the job starts.
    /// </summary>
    private void SetElevationPolicy(ElevationPolicy value)
    {
        Policies.Elevation = value;
        if (value == ElevationPolicy.ElevateNow) RequestElevation(preAuthorise: true);
    }

    /// <param name="preAuthorise">
    /// Ask even when nothing is known to need admin rights yet. The banner button
    /// never does this — it exists precisely because something already was refused.
    /// </param>
    public void RequestElevation(bool preAuthorise = false)
    {
        if (!preAuthorise && !CanRequestElevation) return;
        if (preAuthorise && (_elevationBusy || _elevationResolved)) return;

        // Nobody is listening yet: the job is still queued. Leaving the button
        // disabled after a click that did nothing would be the worse lie.
        if (ElevationRequested is null) return;

        _elevationBusy = true;
        Notify(nameof(CanRequestElevation));
        ElevationRequested.Invoke();
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

        SampleRate();
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
                : $"הושלם — {report.FilesCopied:N0} פריטים, {Format.Bytes(report.BytesCopied)} ב-{seconds}"
                  + (report.Verified > 0 ? $", {report.Verified:N0} אומתו" : "");

        Status = JobStatus.Finished;
        NotifyAll();
    }

    public void TogglePause() => _control.Toggle();
    public void Cancel() => _control.Cancel();

    private void NotifyAll()
    {
        Notify(nameof(Percent)); Notify(nameof(Title));
        Notify(nameof(Numbers)); Notify(nameof(Eta));
        Notify(nameof(CurrentFile)); Notify(nameof(IsRunning));
        Notify(nameof(PendingText)); Notify(nameof(HasPending));
        Notify(nameof(SkippedText)); Notify(nameof(HasSkipped));
        Notify(nameof(StatusText));
        NotifyElevation();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

