using System.Collections.Concurrent;
using CopyTool.Core.Native;

namespace CopyTool.Core;

/// <summary>
/// Sector-aligned buffers reused across the large files of one job.
///
/// Every slot of the unbuffered pipeline needs its own aligned block, and with a
/// deep queue that is around 16 MB per file. Large files are copied one at a
/// time, so exactly one set is ever live — allocating and freeing it per file
/// meant paying for the pages, and faulting them all back in, once per file for
/// no benefit.
///
/// The size and alignment come from the job's two volume profiles and do not
/// change while it runs, so a single pool serves the whole job. A request that
/// does not match is served by a fresh block rather than a wrong one.
/// </summary>
public sealed class CopyBufferPool : IDisposable
{
    private readonly ConcurrentBag<AlignedBuffer> _free = [];
    private readonly List<AlignedBuffer> _all = [];
    private readonly object _gate = new();
    private bool _disposed;

    internal CopyBufferPool(int length, int alignment)
    {
        Length = length;
        Alignment = alignment;
    }

    /// <summary>Block size the pool serves. A request of any other size gets a fresh block.</summary>
    public int Length { get; }

    /// <summary>Address alignment of every block, driven by the volume sector size.</summary>
    public int Alignment { get; }

    /// <summary>A pool sized for copies between these two volumes.</summary>
    public static CopyBufferPool For(VolumeProfile source, VolumeProfile destination)
    {
        (int chunk, int sector) = FileCopier.PlanChunk(source, destination);
        return new CopyBufferPool(chunk, sector);
    }

    internal AlignedBuffer Rent(int length, int alignment)
    {
        if (length != Length || alignment != Alignment || _disposed)
            return new AlignedBuffer(length, alignment);      // not ours to serve

        if (_free.TryTake(out AlignedBuffer? buffer)) return buffer;

        buffer = new AlignedBuffer(Length, Alignment);
        lock (_gate) { _all.Add(buffer); }
        return buffer;
    }

    internal void Return(AlignedBuffer buffer)
    {
        // A buffer the pool never issued — because the size did not match — is
        // freed rather than kept, or the pool would grow shapes it cannot reuse.
        if (_disposed || buffer.Length != Length) { buffer.Free(); return; }

        _free.Add(buffer);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            foreach (AlignedBuffer buffer in _all) buffer.Free();
            _all.Clear();
        }
        _free.Clear();
    }
}
