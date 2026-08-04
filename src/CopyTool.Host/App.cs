using System.IO;
using System.IO.Pipes;
using System.Threading.Channels;
using System.Windows;
using CopyTool.Core;
using CopyTool.Host.Ui;

namespace CopyTool.Host;

/// <summary>
/// The host application. Owns the job queue, the pipe listener and the windows.
///
/// It is a WPF app with no startup window and <see cref="ShutdownMode.OnExplicitShutdown"/>:
/// between jobs there is nothing on screen at all, which is the point — closing a
/// progress window must not end the host, and an idle host must not look like a
/// running application.
/// </summary>
internal sealed class App : Application
{
    public const string PipeName = "CopyTool.Host";

    private readonly string? _initialJob;
    private readonly TimeSpan _idleTimeout;
    private readonly Channel<string> _jobs =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>One window for everything, created on the first job.</summary>
    private readonly QueueViewModel _queue = new();
    private ProgressWindow? _window;

    /// <summary>
    /// The tail of each destination volume's chain of jobs.
    ///
    /// Two copies onto the same disk do not go twice as fast — they interleave and
    /// both finish later, and on a spinning disk they finish much later. Jobs onto
    /// the same volume therefore run one after another, while jobs onto different
    /// volumes run at the same time, which is exactly where the parallelism pays.
    /// </summary>
    private readonly Dictionary<string, Task> _chains = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Modal dialogs, one at a time — two jobs can finish together.</summary>
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    private int _running;
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    public App(string? initialJob, TimeSpan idleTimeout)
    {
        _initialJob = initialJob;
        _idleTimeout = idleTimeout;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        HostLog.Write($"host started (pid {Environment.ProcessId})");

        // No console, no window: without this a crash is completely silent.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            HostLog.Write($"FATAL unhandled: {args.ExceptionObject}");
        DispatcherUnhandledException += (_, args) =>
        {
            HostLog.Write($"FATAL dispatcher: {args.Exception}");
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            HostLog.Write($"FATAL unobserved task: {args.Exception}");
            args.SetObserved();
        };

        if (_initialJob is not null) _jobs.Writer.TryWrite(_initialJob);

        _ = ListenAsync(_shutdown.Token);
        _ = PumpAsync();
    }

    /// <summary>
    /// Accepts jobs and hands them to the scheduler, then exits once nothing has
    /// arrived and nothing is running for the whole idle window.
    ///
    /// The pump no longer runs the jobs itself: it would serialise every drop
    /// behind every other one, including drops onto a completely different disk.
    /// </summary>
    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                string path;
                try
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    idle.CancelAfter(_idleTimeout);
                    path = await _jobs.Reader.ReadAsync(idle.Token);
                }
                catch (OperationCanceledException)
                {
                    if (_shutdown.IsCancellationRequested) break;

                    // A long copy is not an idle host, and neither is the minute
                    // after one finished while its window is still on screen.
                    if (Volatile.Read(ref _running) > 0) continue;
                    if (DateTime.UtcNow - _lastActivityUtc < _idleTimeout) continue;

                    string window = _idleTimeout.TotalMinutes >= 1
                        ? $"{_idleTimeout.TotalMinutes:F0} min"
                        : $"{_idleTimeout.TotalSeconds:F0} s";
                    HostLog.Write($"idle for {window} - exiting");
                    break;
                }

                _lastActivityUtc = DateTime.UtcNow;
                Schedule(path);
            }
        }
        catch (Exception e)
        {
            HostLog.Write($"FATAL in pump: {e}");
        }
        finally
        {
            _shutdown.Cancel();
            HostLog.Write("host stopped");
            Shutdown();
        }
    }

    /// <summary>
    /// Puts a job on screen straight away and queues the work behind whatever else
    /// is already going to the same volume.
    ///
    /// Everything here runs on the UI thread — the pump's continuations come back
    /// to the dispatcher — so the window, the queue and <see cref="_chains"/> need
    /// no locking.
    /// </summary>
    private void Schedule(string jobFilePath)
    {
        if (!IsOurJobFile(jobFilePath))
        {
            Reject(jobFilePath, "outside the jobs directory", Text.JobNotRecognised);
            return;
        }

        JobSpec? job = JobSpec.TryLoad(jobFilePath, out string error);
        if (job is null)
        {
            Reject(jobFilePath, error, Text.JobNotRecognised);
            return;
        }

        var operation = job.Operation.Equals("move", StringComparison.OrdinalIgnoreCase)
            ? CopyOperation.Move
            : CopyOperation.Copy;

        var control = new JobControl();
        var policies = new JobPolicies();
        var vm = new JobViewModel(
            operation == CopyOperation.Move ? "מעביר" : "מעתיק",
            job.Destination, control, policies);

        ShowQueue();
        _queue.Add(vm);

        string volume = VolumeKey(job.Destination);
        Task previous = _chains.TryGetValue(volume, out Task? tail) ? tail : Task.CompletedTask;

        Interlocked.Increment(ref _running);
        _chains[volume] = RunChainedAsync(previous, jobFilePath, job, operation, control, policies, vm);
    }

    /// <summary>
    /// Whether a job file is one the shell extension wrote.
    ///
    /// The pipe carries a path, and anything running as this user can connect to
    /// it. Without this check that path could be any file anywhere, which turns
    /// the host into a deputy: the caller chooses the destination, and a
    /// destination under <c>C:\Windows</c> makes CopyTool raise its own "approve
    /// permissions" banner over a job the user never asked for. One trusted click
    /// away from an elevated write. Confining jobs to the directory the extension
    /// writes to costs nothing and closes it.
    /// </summary>
    private static bool IsOurJobFile(string path)
    {
        try
        {
            string root = Path.TrimEndingDirectorySeparator(
                              Path.GetFullPath(Path.Combine(HostLog.Directory, "jobs")))
                          + Path.DirectorySeparatorChar;

            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Says no, visibly.
    ///
    /// A drop that produces nothing at all is the worst answer this program can
    /// give: the user picked a menu item and the machine did nothing, with the
    /// explanation in a log file they do not know exists. The blocked-job row is
    /// already the shape of "we did not do this, and here is why".
    /// </summary>
    private void Reject(string jobFilePath, string reason, string shown)
    {
        HostLog.Write($"rejected {jobFilePath}: {reason}");

        // Not disposed: the row stays on screen, and its bindings keep reading this
        // control. A rejection is rare enough that one live handle costs nothing.
        var vm = new JobViewModel("מעתיק", "", new JobControl(), new JobPolicies());

        ShowQueue();
        _queue.Add(vm);
        vm.Block(shown);
        vm.Settle();
    }

    /// <summary>The volume a path lands on: two jobs sharing one must not overlap.</summary>
    private static string VolumeKey(string destination)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(destination));
            if (!string.IsNullOrEmpty(root)) return root;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unparseable destination gets its own chain; preflight will reject
            // it in a moment anyway.
        }
        return destination;
    }

    private async Task RunChainedAsync(
        Task previous, string jobFilePath, JobSpec job, CopyOperation operation,
        JobControl control, JobPolicies policies, JobViewModel vm)
    {
        try
        {
            await previous;                       // never faults: RunJobAsync swallows
            vm.Status = JobStatus.Running;
            await RunJobAsync(jobFilePath, job, operation, control, policies, vm);
        }
        catch (Exception e)
        {
            HostLog.Write($"  job {job.Id} threw in chain: {e}");
        }
        finally
        {
            control.Dispose();

            if (Interlocked.Decrement(ref _running) == 0)
            {
                _lastActivityUtc = DateTime.UtcNow;
                _chains.Clear();                  // nothing running: no tail worth keeping

                try
                {
                    Ghost.TrimWorkingSet();
                    HostLog.Write($"idle; working set {Ghost.WorkingSetBytes / 1024.0 / 1024:F1} MB");
                }
                catch (Exception e)
                {
                    HostLog.Write($"working-set trim failed (ignored): {e.Message}");
                }
            }
        }
    }

    /// <summary>Creates the one window on first use, or brings it back into view.</summary>
    private void ShowQueue()
    {
        if (_window is null)
        {
            _window = new ProgressWindow(_queue);

            // Closing the window is how a finished job is dismissed — including a
            // failed one, whose row was the only record of it. Anything still
            // running stays: closing has never meant cancelling.
            _window.Closed += (_, _) =>
            {
                _window = null;
                foreach (JobViewModel done in _queue.Jobs.Where(j => j.Status == JobStatus.Finished).ToArray())
                    _queue.Remove(done);
            };
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
    }

    private async Task RunJobAsync(
        string jobFilePath, JobSpec job, CopyOperation operation,
        JobControl control, JobPolicies policies, JobViewModel vm)
    {
        HostLog.Write($"job {job.Id}: {operation} {job.Sources.Length} source(s) -> {job.Destination}");

        var engine = new CopyEngine
        {
            Control = control,
            Policies = policies,
            // Constructed on the UI thread, so reports arrive there too.
            Progress = new Progress<CopyProgress>(vm.Update),
        };

        // Built before the scan, not after it, because the window is already up and
        // its chips are already clickable. Picking "elevate now" while a large tree
        // is still being walked has to raise the prompt then, not once the walk
        // happens to finish.
        await using var elevation = new ElevationCoordinator(engine, vm, policies);

        void OnElevationRequested() => _ = elevation.OpenAsync(control.Token);
        vm.ElevationRequested += OnElevationRequested;

        try
        {
            // Scanning a large tree blocks; keep it off the UI thread.
            CopyReport report;

            // The permission probe takes no dependency on the scan — it opens the
            // destination and each source root — so it runs alongside the tree
            // walk instead of behind it. On a network destination that probe is
            // hundreds of milliseconds of pure latency on the critical path.
            Task<ElevationCheck> elevationCheck = Task.Run(
                () => ElevationCheck.Run(job.Sources, job.Destination), control.Token);

            ScanResult scan = await Task.Run(() => Scanner.Scan(job.Sources, control.Token));

            // Dropping something onto the folder it came from is a duplication, not
            // a collision: there is only one file, and the person is asking for a
            // second copy of it. Renaming here rather than letting the conflict
            // machinery see it is what keeps the dialog for the case that actually
            // warrants one — two different files, in another folder, sharing a name.
            //
            // Before the preflight, so its name checks see the names that will
            // really be written. Copy only: a move onto its own folder moves
            // nothing, and the preflight blocks it by saying so.
            if (operation == CopyOperation.Copy)
                scan = Duplication.Rename(job.Sources, scan, job.Destination);

            var (tiny, small, medium, large) = scan.Histogram;
            HostLog.Write($"  scan: {scan.FileCount:N0} files, {scan.TotalBytes / 1024.0 / 1024:N1} MB " +
                          $"(tiny={tiny} small={small} medium={medium} large={large})");

            // Everything cheap to check and expensive to discover late: no space,
            // a name the destination cannot represent, a folder copied into itself.
            PreflightResult preflight = await Task.Run(
                () => Preflight.Run(job.Sources, job.Destination, scan, operation), control.Token);

            foreach (PreflightIssue issue in preflight.Issues)
                HostLog.Write($"  preflight [{issue.Severity}] {issue.Code}" +
                              (issue.Count > 0 ? $" x{issue.Count}" : "") +
                              (issue.Path is null ? "" : $" ({issue.Path})"));

            if (!preflight.CanProceed)
            {
                string reason = string.Join(" · ", preflight.Blocking.Select(Text.Describe));
                vm.Block(reason);
                HostLog.Write($"  job {job.Id} blocked by preflight");
                return;   // nothing copied; the window stays up with the reason
            }

            if (preflight.Warnings.Any())
                vm.SetWarnings(preflight.Warnings.Select(Text.Describe).ToArray());

            // Started before the scan; by now it has almost certainly finished.
            ElevationCheck check = await elevationCheck;
            if (check.AnythingNeedsElevation)
                HostLog.Write($"  preflight: destination blocked={check.DestinationNeedsElevation}, " +
                              $"unreadable sources={check.UnreadableSources.Count}");

            // "Elevate now" means now — the prompt goes up before the first byte
            // moves, so the permission is already in hand when the copy reaches a
            // protected file. Held open for the rest of the job, one consent covers
            // whatever it runs into later on.
            await elevation.PrepareAsync(check.AnythingNeedsElevation, control.Token);

            // Task.Run, not a bare await: RunAsync does real work before its first
            // await — it creates the whole destination tree, profiles both volumes
            // and, for a same-volume move, performs every single rename. All of
            // that ran on the dispatcher, so the window that exists to show the job
            // progressing was frozen solid for the duration of the biggest jobs.
            report = await Task.Run(() => engine.RunAsync(scan, job.Destination, operation, control.Token));

            vm.Finish(report, cancelled: control.IsCancelled);

            // Every other skip is a rule the user set playing out as written. These
            // are the ones where a file is simply not there and only its name says
            // which — so they are also the only ones the job stays on screen for.
            // Shown now, before the dialog, so a locked file is on screen while the
            // conflicts are being decided.
            vm.SetNotCopied(Text.DescribeNotCopied([.. report.SkippedAfterError]));

            ConflictOutcome conflicts = await ResolveConflictsAsync(report, engine, vm, control, operation);
            CopyReport? resolved = conflicts.Copied;

            // A question that was asked and not answered belongs here too. The file
            // is not copied either way, but "I chose to skip it" and "I closed the
            // window" are not the same fact, and only one of them is already known
            // to the person who did it.
            string[] notCopied = Text.DescribeNotCopied(
                [.. report.SkippedAfterError], conflicts.Unanswered);
            vm.SetNotCopied(notCopied);

            // The report's count is what the copy parked; by now the questions have
            // been put, so only what nobody answered is still waiting.
            vm.SetPending(conflicts.Unanswered.Count);
            bool elevationResolved = await ResolveElevationAsync(elevation, vm, policies, control);

            // Copied under elevation after the engine had already removed the
            // sources it knew about, so a move has to finish the job here.
            if (operation == CopyOperation.Move)
                DeleteLateMovedSources(elevation.ArrivedSources);

            if (elevation.Copied > 0 || elevation.Failed > 0)
                HostLog.Write($"  elevated: {elevation.Copied:N0} copied, {elevation.Failed:N0} failed");

            foreach (CopyFailure f in elevation.Failures.Take(20))
                HostLog.Write($"  ELEVATED FAILED {f.Source}: {f.Reason}");

            if (resolved is not null)
                HostLog.Write($"  conflicts resolved: {resolved.FilesCopied:N0} copied, " +
                              $"{resolved.Failures.Count} failed");
            // Skipped and parked counts belong on this line. Without them "scan: 5
            // files → done: 1 file" with no failures reads as four files silently
            // lost, and the only way to tell that apart from four files correctly
            // skipped as identical was to go and look at the destination.
            string accounted =
                (report.Skipped.Count > 0 ? $", {report.Skipped.Count:N0} skipped" : "") +
                (report.Pending.Count > 0 ? $", {report.Pending.Count:N0} pending" : "") +
                (report.Failures.Count > 0 ? $", {report.Failures.Count:N0} failed" : "") +
                (report.Verified > 0 ? $", {report.Verified:N0} verified" : "");

            HostLog.Write($"  done: {report.FilesCopied:N0} of {report.FilesCopied + report.Skipped.Count + report.Pending.Count + report.Failures.Count:N0} files" +
                          $"{accounted} — {report.BytesCopied / 1024.0 / 1024:N1} MB " +
                          $"in {report.Elapsed.TotalSeconds:F2}s ({report.BytesPerSecond / 1024 / 1024:F0} MB/s) " +
                          $"[{report.Strategy}]");

            foreach (IGrouping<SkipReason, SkippedItem> group in report.Skipped.GroupBy(s => s.Reason))
                HostLog.Write($"  skipped {group.Count():N0}: {group.Key}");

            foreach (CopyFailure f in report.Failures.Take(20))
                HostLog.Write($"  FAILED {f.Source}: {f.Reason}");

            // Two separate questions, and they used to share one answer.
            //
            // The job file exists so that a crash or a cancellation leaves
            // something to retry. A job that ran to the end has nothing to retry —
            // including one that skipped what it was told to skip. "Skip protected
            // items" is an instruction being followed, not an unfinished job, and
            // treating it as one left a file on disk for ever that nothing ever
            // reads back and that the uninstaller reports as recoverable work.
            bool ranToTheEnd = report.Failures.Count == 0 && !control.IsCancelled;
            bool nothingLeftToAsk = elevationResolved
                                    || policies.Elevation == ElevationPolicy.SkipProtected;

            if (ranToTheEnd && nothingLeftToAsk) Files.TryDelete(jobFilePath);

            // The row, on the other hand, is the only record of anything that did
            // not arrive — so it stays whenever there is such a thing to record.
            if (ranToTheEnd && elevationResolved && notCopied.Length == 0) Retire(vm);
        }
        catch (OperationCanceledException)
        {
            HostLog.Write($"  job {job.Id} cancelled");
            Retire(vm);
        }
        catch (Exception e)
        {
            HostLog.Write($"  job {job.Id} threw: {e}");

            // Without this the row stays "running" for ever, with Pause and Cancel
            // still enabled over a JobControl that is about to be disposed — so the
            // one thing the user can do to a wedged job throws.
            vm.Block(Text.UnexpectedFailure);
        }
        finally
        {
            vm.Settle();
            vm.ElevationRequested -= OnElevationRequested;
        }
    }

    /// <summary>
    /// Removes the sources of a move that only arrived after the engine finished —
    /// an answered conflict, or a file the elevated worker wrote.
    ///
    /// The engine deletes sources at the end of its own pass, so anything copied
    /// after that is invisible to it and the move quietly left both copies in
    /// place: the user asked to move and got a duplicate, with nothing saying so.
    /// The rule itself is unchanged, and it is the rule that matters — delete only
    /// what demonstrably landed, never what merely failed to be a failure.
    /// </summary>
    private static void DeleteLateMovedSources(IEnumerable<string> sources)
    {
        foreach (string source in sources)
        {
            try
            {
                File.Delete(source);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                HostLog.Write($"  move: source kept, could not remove {source}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Takes a finished job off the list, and closes the window once the last one
    /// goes — which is what a single successful drop looked like before there was
    /// a queue at all. A job that failed, was blocked or has something left to
    /// answer is never retired: its row is the only record the user gets.
    /// </summary>
    private void Retire(JobViewModel vm)
    {
        _queue.Remove(vm);
        if (_queue.Jobs.Count == 0) _window?.Close();
    }

    /// <summary>
    /// Shows the parked conflicts and copies whatever the user decided to keep.
    ///
    /// This runs after the copy, never during it — the whole reason those items
    /// were parked is that a question must not hold up the other 99%.
    /// </summary>
    /// <summary>
    /// What came of the parked questions: what the user chose to copy, and what
    /// they closed the window on without answering.
    /// </summary>
    private sealed record ConflictOutcome(
        CopyReport? Copied, IReadOnlyList<PendingDecision> Unanswered)
    {
        public static readonly ConflictOutcome Nothing = new(null, []);
    }

    /// <summary>
    /// Puts the question and returns what to copy, plus whatever went unanswered.
    ///
    /// Two windows, because the answer is nearly always the same for all of it:
    /// four choices first, and the side-by-side list only for someone who says
    /// they want to decide per item. Coming back out of that list returns here
    /// rather than abandoning the question — leaving for good is what closing this
    /// window is for.
    ///
    /// One view model across the whole loop, so a trip into the list and back does
    /// not throw away the choices made in it.
    /// </summary>
    private (List<(string Source, string Destination, long Size)>? List, List<PendingDecision> Unanswered)
        Ask(IReadOnlyList<PendingDecision> answerable)
    {
        var vm = new ConflictViewModel(answerable);
        Window? owner = _window is { IsLoaded: true } w ? w : null;

        while (true)
        {
            var choice = new ConflictChoiceWindow(vm) { Owner = owner };
            choice.ShowDialog();
            HostLog.Write($"  conflicts: {answerable.Count:N0} parked, answered {choice.Action}");

            switch (choice.Action)
            {
                case ConflictAction.DecidePerFile:
                    var details = new ConflictWindow(vm) { Owner = owner };
                    if (details.ShowDialog() == true) return (details.Result, details.Unanswered);
                    continue;                       // "back" — ask the four again

                default:
                    vm.ApplyToAll(choice.Action switch
                    {
                        ConflictAction.ReplaceAll => ConflictChoice.Replace,
                        ConflictAction.KeepBothAll => ConflictChoice.KeepBoth,

                        // Closing the window lands here on purpose. Shutting a
                        // question is an answer to it — do not copy these — and it
                        // is the same answer as picking "skip", so it carries the
                        // same weight. Treating it as *unanswered* instead would
                        // have reported back a decision the user had just made,
                        // which is the thing this deliberately never does.
                        _ => ConflictChoice.Skip,
                    });

                    return (vm.BuildCopyList(), []);
            }
        }
    }

    private async Task<ConflictOutcome> ResolveConflictsAsync(
        CopyReport report, CopyEngine engine, JobViewModel vm, JobControl control, CopyOperation operation)
    {
        // Permission items are answered by a UAC prompt, not by this dialog.
        var answerable = report.NeedsAnswer.ToArray();
        if (answerable.Length == 0 || control.IsCancelled) return ConflictOutcome.Nothing;

        // Two jobs on different disks can finish within the same second; stacked
        // modal dialogs are how a user ends up answering the wrong one's questions.
        await _dialogGate.WaitAsync(control.Token);
        List<(string Source, string Destination, long Size)>? list;
        List<PendingDecision> unanswered;
        try
        {
            (list, unanswered) = Ask(answerable);
        }
        finally
        {
            _dialogGate.Release();
        }

        if (unanswered.Count > 0)
            HostLog.Write($"  conflicts: {unanswered.Count:N0} of {answerable.Length:N0} left unanswered");

        if (list is not { Count: > 0 })
            return new ConflictOutcome(null, unanswered);

        vm.SetConflictProgress($"מעתיק {list.Count:N0} פריטים שנבחרו…");

        // Collected as they land rather than derived afterwards, for the same
        // reason the engine's own move does it that way: "not a failure" is not
        // the same as "arrived", and only one of the two may delete a source.
        var moved = new List<string>();

        try
        {
            CopyReport copied = await engine.CopyExplicitAsync(
                list,
                onFileDone: (source, _, _) =>
                {
                    if (operation == CopyOperation.Move) moved.Add(source);
                },
                ct: control.Token);

            return new ConflictOutcome(copied, unanswered);
        }
        catch (OperationCanceledException)
        {
            return new ConflictOutcome(null, unanswered);
        }
        finally
        {
            vm.SetConflictProgress(null);
            DeleteLateMovedSources(moved);
        }
    }

    /// <summary>
    /// Settles whatever the copy could not write for permission reasons.
    ///
    /// Most of it is normally already done: with a session open, protected files
    /// were copied as they were discovered. What is left is the case where nobody
    /// has consented yet — and then the window simply waits for the button, or for
    /// the user to close it, which is the other valid answer.
    /// </summary>
    private async Task<bool> ResolveElevationAsync(
        ElevationCoordinator elevation, JobViewModel vm, JobPolicies policies,
        JobControl control)
    {
        if (await elevation.FinishAsync(control.Token).ConfigureAwait(false)) return true;
        if (control.IsCancelled) return false;

        // Consent was given and the worker still could not write these. Waiting for
        // the user to press "approve" again would be waiting for something that has
        // already happened and did not help.
        if (elevation.HasFailures) return false;

        // Nothing more to ask for: the answer is already settled.
        if (policies.Elevation == ElevationPolicy.SkipProtected || !Elevation.CanElevate) return false;

        var decided = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnClosed(object? s, EventArgs e) => decided.TrySetResult(false);

        // Watches the coordinator rather than the request, because the request is
        // already wired for the whole job. Subscribing to it twice here would mean
        // two calls to OpenAsync, and the second one — refused by the single-flight
        // guard — would read as a refusal by the user.
        async void OnOpened(bool opened)
        {
            if (!opened) { decided.TrySetResult(false); return; }
            decided.TrySetResult(await elevation.FinishAsync(control.Token).ConfigureAwait(false));
        }

        // Closing the window is the other valid answer to "may I elevate?" — but
        // only the window this job is actually shown in, so a job scheduled after
        // it reopens one does not inherit a stale subscription.
        ProgressWindow? window = _window;
        if (window is null) return false;

        window.Closed += OnClosed;
        elevation.Opened += OnOpened;
        try
        {
            // Bounded by the job's own cancellation. Unbounded, a single protected
            // file that nobody answered for held this job open for ever: the next
            // job queued behind it on the same disk never started, and the host —
            // which counts this one as running — never reached its idle timeout and
            // never exited. Closing the window was the only way out, and closing is
            // documented as *not* cancelling.
            return await decided.Task.WaitAsync(control.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            window.Closed -= OnClosed;
            elevation.Opened -= OnOpened;
        }
    }

    /// <summary>
    /// Accepts job hand-offs. The shell extension writes here directly from inside
    /// explorer.exe, so the protocol is deliberately trivial: one job file path per
    /// line, then the client disconnects.
    /// </summary>
    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server);
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    HostLog.Write($"received job: {line}");
                    _jobs.Writer.TryWrite(line);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                // Deliberately every exception. Anything that escaped this loop
                // killed the listener while the process stayed alive holding the
                // single-instance mutex — so every later drop was delivered to a
                // host that was no longer listening, and vanished without a trace.
                // A broken connection is never worth giving up the endpoint for.
                HostLog.Write($"pipe error: {e.Message}");
                await Task.Delay(200, CancellationToken.None);
            }
        }
    }
}
