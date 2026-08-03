using CopyTool.Core;
using CopyTool.Host.Ui;

namespace CopyTool.Host;

/// <summary>
/// Decides when to ask for elevation, and drains protected files through the
/// worker for as long as the job runs.
///
/// The point of holding the session open is that consent is given once and covers
/// the rest of the copy — including files nothing knew about when the prompt was
/// answered. "Elevate now" therefore means exactly that: the prompt appears before
/// the first byte moves, not after the job has finished discovering problems.
/// </summary>
internal sealed class ElevationCoordinator : IAsyncDisposable
{
    private readonly CopyEngine _engine;
    private readonly JobViewModel _vm;
    private readonly JobPolicies _policies;
    private readonly ElevationSession _session = new();

    private Task? _pump;
    private CancellationTokenSource? _pumpStop;
    private int _opening;

    public ElevationCoordinator(CopyEngine engine, JobViewModel vm, JobPolicies policies)
    {
        _engine = engine;
        _vm = vm;
        _policies = policies;
    }

    public bool IsOpen => _session.IsOpen;
    public int Copied => _session.Copied;
    public int Failed => _session.Failed;

    /// <summary>Raised whenever an attempt to open the session settles, either way.</summary>
    public event Action<bool>? Opened;

    /// <summary>
    /// Called once the preflight knows whether anything is out of reach. With the
    /// policy set to "elevate now" this is where the UAC prompt happens — before
    /// the copy, not after it.
    ///
    /// The prompt is not conditional on the preflight having found something. A job
    /// can be refused a file it has not reached yet, and "elevate now" is the user
    /// saying they would rather answer once, up front, than be asked later.
    /// </summary>
    public async Task PrepareAsync(bool destinationNeedsElevation, CancellationToken ct)
    {
        if (destinationNeedsElevation) _vm.SetElevationNeeded(1);

        if (_policies.Elevation == ElevationPolicy.ElevateNow)
            await OpenAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Raises the prompt and starts draining. Safe to call more than once.</summary>
    public async Task<bool> OpenAsync(CancellationToken ct)
    {
        if (_session.IsOpen)
        {
            Opened?.Invoke(true);
            return true;
        }

        // A second click while the prompt is up would mean two dialogs and two
        // workers.
        if (Interlocked.Exchange(ref _opening, 1) == 1) return false;

        try
        {
            _vm.SetElevationMessage("ממתין לאישור הרשאות מנהל…", resolved: false);

            (ElevationOutcome outcome, ElevationError error) =
                await _session.OpenAsync(_policies.BackgroundIo, ct).ConfigureAwait(false);

            HostLog.Write($"  elevation session: {outcome}" +
                          (error == ElevationError.None ? "" : $" ({error})"));

            if (outcome != ElevationOutcome.Completed)
            {
                _vm.SetElevationMessage(outcome switch
                {
                    ElevationOutcome.Declined     => "ההרשאה נדחתה — פריטים מוגנים לא יועתקו. אפשר לנסות שוב",
                    ElevationOutcome.NotPermitted => "נדרש חשבון מנהל — פריטים מוגנים לא יועתקו",
                    _ => Text.Describe(error),
                }, resolved: false);
                Opened?.Invoke(false);
                return false;
            }

            // Nothing is necessarily waiting: consent may have been given up front,
            // before anything was refused. The pump then sits ready for whatever the
            // copy runs into later, which is exactly what was asked for.
            _vm.SetElevationMessage("הרשאות מנהל מוכנות — פריטים מוגנים יועתקו", resolved: false);
            StartPump();
            Opened?.Invoke(true);
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _opening, 0);
        }
    }

    private void StartPump()
    {
        if (_pump is not null) return;

        _pumpStop = new CancellationTokenSource();
        CancellationToken ct = _pumpStop.Token;

        _pump = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && _session.IsOpen)
            {
                if (!await DrainOnceAsync(ct).ConfigureAwait(false))
                {
                    try { await Task.Delay(150, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, CancellationToken.None);
    }

    /// <summary>Copies everything currently parked. Returns true if it did any work.</summary>
    private async Task<bool> DrainOnceAsync(CancellationToken ct)
    {
        bool didWork = false;

        while (!ct.IsCancellationRequested && _session.IsOpen &&
               _engine.TryTakePending(DecisionKind.NeedsElevation, out PendingDecision item))
        {
            didWork = true;
            bool ok = await _session.CopyAsync(item.Source, item.Destination, item.SourceSize, ct)
                                    .ConfigureAwait(false);
            if (!ok && !_session.IsOpen)
            {
                _vm.SetElevationMessage(Text.Describe(ElevationError.WorkerStopped), resolved: false);
                break;
            }
        }

        return didWork;
    }

    /// <summary>
    /// Final pass once the copy has stopped producing work. Returns true when
    /// nothing is left unresolved.
    /// </summary>
    public async Task<bool> FinishAsync(CancellationToken ct)
    {
        if (_pumpStop is not null)
        {
            await _pumpStop.CancelAsync().ConfigureAwait(false);
            if (_pump is not null)
            {
                try { await _pump.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }

        if (_session.IsOpen) await DrainOnceAsync(ct).ConfigureAwait(false);

        int remaining = _engine.CountPending(DecisionKind.NeedsElevation);
        if (remaining == 0)
        {
            if (_session.Copied > 0)
                HostLog.Write($"  elevated: {_session.Copied} copied, {_session.Failed} failed");
            _vm.SetElevationMessage(null, resolved: true);
            return true;
        }

        _vm.SetElevationNeeded(remaining);
        if (_policies.Elevation == ElevationPolicy.SkipProtected)
        {
            _vm.SetElevationMessage($"{remaining:N0} פריטים מוגנים דולגו לפי ההגדרה", resolved: false);
        }
        else if (!Elevation.CanElevate)
        {
            _vm.SetElevationMessage($"{remaining:N0} פריטים דורשים חשבון מנהל", resolved: false);
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _pumpStop?.Cancel();
        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _pumpStop?.Dispose();
        await _session.DisposeAsync().ConfigureAwait(false);
    }
}
