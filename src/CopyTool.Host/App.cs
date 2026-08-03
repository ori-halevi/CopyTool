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
        JobSpec? job = JobSpec.TryLoad(jobFilePath, out string error);
        if (job is null)
        {
            HostLog.Write($"rejected {jobFilePath}: {error}");
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

            report = await engine.RunAsync(scan, job.Destination, operation, control.Token);

            vm.Finish(report, cancelled: control.IsCancelled);

            CopyReport? resolved = await ResolveConflictsAsync(report, engine, vm, control);
            bool elevationResolved = await ResolveElevationAsync(elevation, vm, policies, control);

            if (elevation.Copied > 0 || elevation.Failed > 0)
                HostLog.Write($"  elevated: {elevation.Copied:N0} copied, {elevation.Failed:N0} failed");

            if (resolved is not null)
                HostLog.Write($"  conflicts resolved: {resolved.FilesCopied:N0} copied, " +
                              $"{resolved.Failures.Count} failed");
            HostLog.Write($"  done: {report.FilesCopied:N0} files, {report.BytesCopied / 1024.0 / 1024:N1} MB " +
                          $"in {report.Elapsed.TotalSeconds:F2}s ({report.BytesPerSecond / 1024 / 1024:F0} MB/s) " +
                          $"[{report.Strategy}]");

            foreach (CopyFailure f in report.Failures.Take(20))
                HostLog.Write($"  FAILED {f.Source}: {f.Reason}");

            // Keep the job on disk unless it truly finished, so a crash or a
            // cancellation leaves something to retry.
            if (report.Failures.Count == 0 && !control.IsCancelled && elevationResolved)
            {
                TryDelete(jobFilePath);
                Retire(vm);
            }
        }
        catch (OperationCanceledException)
        {
            HostLog.Write($"  job {job.Id} cancelled");
            Retire(vm);
        }
        catch (Exception e)
        {
            HostLog.Write($"  job {job.Id} threw: {e}");
        }
        finally
        {
            vm.ElevationRequested -= OnElevationRequested;
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
    /// Deals with whatever the copy could not write for permission reasons.
    ///
    /// By the time this runs the rest of the job is already done — which is the
    /// whole point. Walking away from a copy and coming back to a finished one
    /// should not depend on having answered a permission prompt in between.
    /// </summary>
    /// <summary>
    /// Shows the parked conflicts and copies whatever the user decided to keep.
    ///
    /// This runs after the copy, never during it — the whole reason those items
    /// were parked is that a question must not hold up the other 99%.
    /// </summary>
    private async Task<CopyReport?> ResolveConflictsAsync(
        CopyReport report, CopyEngine engine, JobViewModel vm, JobControl control)
    {
        // Permission items are answered by a UAC prompt, not by this dialog.
        var answerable = report.NeedsAnswer.ToArray();
        if (answerable.Length == 0 || control.IsCancelled) return null;

        // Two jobs on different disks can finish within the same second; stacked
        // modal dialogs are how a user ends up answering the wrong one's questions.
        await _dialogGate.WaitAsync(control.Token);
        bool? answer;
        List<(string Source, string Destination, long Size)>? list;
        try
        {
            var vmConflicts = new ConflictViewModel(answerable);
            var dialog = new ConflictWindow(vmConflicts)
            {
                Owner = _window is { IsLoaded: true } w ? w : null,
            };
            answer = dialog.ShowDialog();
            list = dialog.Result;
        }
        finally
        {
            _dialogGate.Release();
        }

        if (answer != true || list is not { Count: > 0 })
        {
            HostLog.Write($"  conflicts: {answerable.Length} left unresolved");
            return null;
        }

        vm.SetConflictProgress($"מעתיק {list.Count:N0} פריטים שנבחרו…");
        try
        {
            return await engine.CopyExplicitAsync(list, ct: control.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            vm.SetConflictProgress(null);
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
            return await decided.Task;
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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                HostLog.Write($"pipe error: {e.Message}");
                await Task.Delay(200, CancellationToken.None);
            }
        }
    }

    private static void TryDelete(string path) => Files.TryDelete(path);
}
