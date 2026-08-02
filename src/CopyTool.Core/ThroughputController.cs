using System.Diagnostics;

namespace CopyTool.Core;

/// <summary>
/// Tunes worker count by measuring, not by guessing.
///
/// Static heuristics cannot know whether source and destination share a spindle,
/// what else is hammering the disk, or how a given USB enclosure behaves. So the
/// controller nudges the worker count and keeps the direction that actually
/// raised aggregate throughput — the same result, arrived at empirically.
/// </summary>
public sealed class ThroughputController
{
    private readonly int _min;
    private readonly int _max;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private long _bytesAtLastSample;
    private TimeSpan _lastSampleAt;
    private double _lastRate;          // bytes/sec
    private int _lastDirection = +1;   // what we tried most recently
    private int _flatSamples;
    private bool _settled;
    private double _settledRate;

    /// <summary>How long a level must be observed before judging it.</summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Below this relative change, treat two samples as equal. Measured copies
    /// swing by roughly 10% between one-second windows even at a fixed worker
    /// count, so a tighter floor just makes the controller chase noise.
    /// </summary>
    private const double NoiseFloor = 0.10;

    /// <summary>Consecutive flat samples after which the controller stops probing.</summary>
    private const int SettleAfter = 3;

    public int Workers { get; private set; }

    /// <summary>Rate of the most recent sample window, bytes/sec.</summary>
    public double CurrentRate => _lastRate;

    public ThroughputController(int initial, int min = 1, int max = 32)
    {
        _min = Math.Max(1, min);
        _max = Math.Max(_min, max);
        Workers = Math.Clamp(initial, _min, _max);
    }

    /// <summary>
    /// Feed the running total of bytes copied. Returns true when the worker
    /// count changed, so the caller can spin workers up or let them retire.
    /// </summary>
    public bool Update(long totalBytesCopied)
    {
        TimeSpan now = _clock.Elapsed;
        TimeSpan window = now - _lastSampleAt;
        if (window < SampleInterval) return false;

        long delta = totalBytesCopied - _bytesAtLastSample;
        double rate = delta / window.TotalSeconds;

        _bytesAtLastSample = totalBytesCopied;
        _lastSampleAt = now;

        // First sample establishes the baseline; there is nothing to compare to.
        if (_lastRate == 0)
        {
            _lastRate = rate;
            return false;
        }

        double change = (rate - _lastRate) / _lastRate;
        _lastRate = rate;

        // Once settled, hold the level and only wake up if throughput genuinely
        // shifts regime — another job starting, a device throttling, media change.
        if (_settled)
        {
            double drift = Math.Abs(rate - _settledRate) / _settledRate;
            if (drift < NoiseFloor * 2) return false;

            _settled = false;
            _flatSamples = 0;
        }

        int previous = Workers;

        if (change > NoiseFloor)
        {
            // The last move helped — keep going that way.
            Workers = Math.Clamp(Workers + _lastDirection, _min, _max);
            _flatSamples = 0;
        }
        else if (change < -NoiseFloor)
        {
            // It hurt. Reverse.
            _lastDirection = -_lastDirection;
            Workers = Math.Clamp(Workers + _lastDirection, _min, _max);
            _flatSamples = 0;
        }
        else if (++_flatSamples >= SettleAfter)
        {
            // Repeatedly flat means worker count is not the bottleneck here.
            // Probing further only adds churn, so stop and stay where we are.
            _settled = true;
            _settledRate = rate;
        }

        return Workers != previous;
    }
}
