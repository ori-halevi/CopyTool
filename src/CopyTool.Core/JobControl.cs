namespace CopyTool.Core;

/// <summary>
/// Pause / resume / cancel for a running job.
///
/// Pausing has to be honoured at points where stopping is safe and cheap: between
/// files, and between chunks inside a large file. Checking mid-chunk would leave a
/// half-written block; checking only between files would make pausing a 40 GB copy
/// take minutes to take effect.
///
/// Both a blocking and an awaiting wait are offered because the engine uses both —
/// worker threads for small files, async slots for the large-file pipeline.
/// </summary>
public sealed class JobControl : IDisposable
{
    private readonly ManualResetEventSlim _gate = new(initialState: true);
    private readonly CancellationTokenSource _cts = new();
    private volatile TaskCompletionSource _resumeSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public JobControl()
    {
        _resumeSignal.SetResult();   // starts unpaused
    }

    public CancellationToken Token => _cts.Token;
    public bool IsPaused => !_gate.IsSet;
    public bool IsCancelled => _cts.IsCancellationRequested;

    /// <summary>Raised whenever the paused state flips, so the UI can follow.</summary>
    public event Action<bool>? PausedChanged;

    public void Pause()
    {
        if (IsPaused) return;
        _resumeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.Reset();
        PausedChanged?.Invoke(true);
    }

    public void Resume()
    {
        if (!IsPaused) return;
        _gate.Set();
        _resumeSignal.TrySetResult();
        PausedChanged?.Invoke(false);
    }

    public void Toggle()
    {
        if (IsPaused) Resume(); else Pause();
    }

    public void Cancel()
    {
        // Release anyone parked at the gate, or a paused job would never observe
        // the cancellation and would hang instead of stopping.
        _gate.Set();
        _resumeSignal.TrySetResult();
        _cts.Cancel();
    }

    /// <summary>Blocking wait, for the small-file worker threads.</summary>
    public void WaitWhilePaused(CancellationToken ct = default)
    {
        if (_gate.IsSet) return;   // fast path: no waiting in the common case
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        _gate.Wait(linked.Token);
    }

    /// <summary>Awaiting wait, for the large-file pipeline slots.</summary>
    public ValueTask WaitWhilePausedAsync(CancellationToken ct = default)
    {
        if (_gate.IsSet) return ValueTask.CompletedTask;
        return new ValueTask(_resumeSignal.Task.WaitAsync(ct));
    }

    public void Dispose()
    {
        _gate.Dispose();
        _cts.Dispose();
    }
}
