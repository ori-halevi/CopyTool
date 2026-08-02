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

    /// <summary>Runs queued jobs, then exits once the idle window elapses.</summary>
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
                    string window = _idleTimeout.TotalMinutes >= 1
                        ? $"{_idleTimeout.TotalMinutes:F0} min"
                        : $"{_idleTimeout.TotalSeconds:F0} s";
                    HostLog.Write($"idle for {window} - exiting");
                    break;
                }

                await RunJobAsync(path);

                if (!_jobs.Reader.TryPeek(out _))
                {
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

    private async Task RunJobAsync(string jobFilePath)
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

        HostLog.Write($"job {job.Id}: {operation} {job.Sources.Length} source(s) -> {job.Destination}");

        using var control = new JobControl();
        var policies = new JobPolicies();
        var vm = new JobViewModel(
            operation == CopyOperation.Move ? "מעביר" : "מעתיק",
            job.Destination, control, policies);

        var window = new ProgressWindow(vm);
        window.Show();

        try
        {
            // Scanning a large tree blocks; keep it off the UI thread.
            CopyReport report;
            ScanResult scan = await Task.Run(() => Scanner.Scan(job.Sources, control.Token));
            var (tiny, small, medium, large) = scan.Histogram;
            HostLog.Write($"  scan: {scan.FileCount:N0} files, {scan.TotalBytes / 1024.0 / 1024:N1} MB " +
                          $"(tiny={tiny} small={small} medium={medium} large={large})");

            var engine = new CopyEngine
            {
                Control = control,
                Policies = policies,
                // Constructed on the UI thread, so reports arrive there too.
                Progress = new Progress<CopyProgress>(vm.Update),
            };

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

            // Permission preflight, so the banner is up immediately rather than
            // after the job has already hit the wall.
            ElevationCheck check = await Task.Run(
                () => ElevationCheck.Run(job.Sources, job.Destination), control.Token);
            if (check.AnythingNeedsElevation)
                HostLog.Write($"  preflight: destination blocked={check.DestinationNeedsElevation}, " +
                              $"unreadable sources={check.UnreadableSources.Count}");

            await using var elevation = new ElevationCoordinator(engine, vm, policies);

            // The banner button now works during the copy, not only after it: with
            // the session held open, one consent covers whatever the job runs into
            // later on.
            void OnElevationRequested() => _ = elevation.OpenAsync(control.Token);
            vm.ElevationRequested += OnElevationRequested;

            try
            {
                // "Elevate now" means now — the prompt goes up before the first
                // byte moves, so the permission is already in hand when the copy
                // reaches a protected file.
                await elevation.PrepareAsync(check.AnythingNeedsElevation, control.Token);

                CopyReport report0 = await engine.RunAsync(scan, job.Destination, operation, control.Token);
                report = report0;
            }
            finally
            {
                vm.ElevationRequested -= OnElevationRequested;
            }

            vm.Finish(report, cancelled: control.IsCancelled);

            CopyReport? resolved = await ResolveConflictsAsync(report, engine, vm, control, window);
            bool elevationResolved = await ResolveElevationAsync(elevation, vm, policies, control, window);

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
                window.Close();
            }
        }
        catch (OperationCanceledException)
        {
            HostLog.Write($"  job {job.Id} cancelled");
            window.Close();
        }
        catch (Exception e)
        {
            HostLog.Write($"  job {job.Id} threw: {e}");
        }
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
    private static async Task<CopyReport?> ResolveConflictsAsync(
        CopyReport report, CopyEngine engine, JobViewModel vm, JobControl control, ProgressWindow window)
    {
        if (report.Pending.Count == 0 || control.IsCancelled) return null;

        var vmConflicts = new ConflictViewModel(report.Pending);
        var dialog = new ConflictWindow(vmConflicts) { Owner = window.IsLoaded ? window : null };

        if (dialog.ShowDialog() != true || dialog.Result is not { Count: > 0 } list)
        {
            HostLog.Write($"  conflicts: {report.Pending.Count} left unresolved");
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
    private static async Task<bool> ResolveElevationAsync(
        ElevationCoordinator elevation, JobViewModel vm, JobPolicies policies,
        JobControl control, ProgressWindow window)
    {
        if (await elevation.FinishAsync(control.Token).ConfigureAwait(false)) return true;
        if (control.IsCancelled) return false;

        // Nothing more to ask for: the answer is already settled.
        if (policies.Elevation == ElevationPolicy.SkipProtected || !Elevation.CanElevate) return false;

        var decided = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnClosed(object? s, EventArgs e) => decided.TrySetResult(false);
        async void OnRequested()
        {
            if (await elevation.OpenAsync(control.Token).ConfigureAwait(false))
                decided.TrySetResult(await elevation.FinishAsync(control.Token).ConfigureAwait(false));
            else
                decided.TrySetResult(false);
        }

        window.Closed += OnClosed;
        vm.ElevationRequested += OnRequested;
        try
        {
            return await decided.Task;
        }
        finally
        {
            window.Closed -= OnClosed;
            vm.ElevationRequested -= OnRequested;
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
