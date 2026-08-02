using System.ComponentModel;
using System.Runtime.InteropServices;
using CopyTool.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace CopyTool.Core;

/// <summary>Single-file copy primitives. Strategy selection lives in <see cref="CopyEngine"/>.</summary>
public static class FileCopier
{
    private const FileOptions NoBuffering = (FileOptions)Win32.FILE_FLAG_NO_BUFFERING;

    /// <summary>
    /// Unbuffered, overlapped copy for large files.
    ///
    /// The file is cut into chunks and <paramref name="queueDepth"/> slots each run
    /// their own read/write pair at explicit offsets, so that many I/Os are in
    /// flight on both devices at once. That depth is the whole point: an NVMe
    /// drive only reaches full bandwidth with a deep queue, and a read-then-write
    /// loop — however well pipelined — never presents more than one request per
    /// direction.
    ///
    /// FILE_FLAG_NO_BUFFERING keeps the transfer out of the system cache, which
    /// matters because a multi-GB copy would otherwise evict everything else.
    /// </summary>
    public static async Task CopyLargeAsync(
        string source, string destination,
        VolumeProfile srcProfile, VolumeProfile dstProfile,
        Action<long>? onBytes = null,
        int? queueDepth = null,
        JobControl? control = null,
        bool backgroundPriority = false,
        long knownSize = 0,
        CopyBufferPool? pool = null,
        CancellationToken ct = default)
    {
        (int chunk, int sector) = PlanChunk(srcProfile, dstProfile);

        // The slower side sets the pace: queuing 8 deep against a USB disk because
        // the other end is NVMe only produces thrashing.
        int depth = Math.Clamp(
            queueDepth ?? Math.Min(srcProfile.QueueDepth, dstProfile.QueueDepth), 1, 32);

        long totalSize = knownSize > 0 ? knownSize : new FileInfo(source).Length;

        using SafeFileHandle src = File.OpenHandle(
            source, FileMode.Open, FileAccess.Read, FileShare.Read,
            FileOptions.Asynchronous | FileOptions.SequentialScan | NoBuffering);

        // preallocationSize reserves the extents up front, so the file lands in as
        // few fragments as possible instead of growing a chunk at a time.
        using SafeFileHandle dst = File.OpenHandle(
            destination, FileMode.Create, FileAccess.Write, FileShare.None,
            FileOptions.Asynchronous | NoBuffering, preallocationSize: totalSize);

        // Background mode tells the storage stack to schedule this transfer behind
        // everything else. It costs throughput, and it is the difference between a
        // copy the machine can work through and one that freezes video playback.
        if (backgroundPriority)
        {
            SetLowIoPriority(src);
            SetLowIoPriority(dst);
        }

        long nextOffset = 0;

        async Task RunSlot()
        {
            AlignedBuffer buffer = pool?.Rent(chunk, sector) ?? new AlignedBuffer(chunk, sector);
            try
            {
                await RunSlotCore(buffer).ConfigureAwait(false);
            }
            finally
            {
                if (pool is not null) pool.Return(buffer); else buffer.Free();
            }
        }

        async Task RunSlotCore(AlignedBuffer buffer)
        {
            Memory<byte> memory = buffer.Memory;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Between chunks, not mid-chunk: pausing must never leave a
                // partially written block behind.
                if (control is not null)
                    await control.WaitWhilePausedAsync(ct).ConfigureAwait(false);

                long offset = Interlocked.Add(ref nextOffset, chunk) - chunk;
                if (offset >= totalSize) return;

                int want = (int)Math.Min(chunk, RoundUp(totalSize - offset, sector));

                // Read until the chunk is full or the file ends. A short read is
                // legal — network redirectors return them routinely — and taking
                // one at face value would leave an uncopied gap in the middle of
                // the file, which the final SetEndOfFile then makes look complete.
                int read = 0;
                while (read < want)
                {
                    int got = await RandomAccess
                        .ReadAsync(src, memory[read..want], offset + read, ct)
                        .ConfigureAwait(false);
                    if (got == 0) break;              // genuine end of file
                    read += got;
                }

                if (read == 0) return;

                // NO_BUFFERING only accepts sector-multiple lengths, so the tail
                // chunk is padded here and the file truncated once at the end.
                int padded = RoundUp(read, sector);
                await RandomAccess.WriteAsync(dst, memory[..padded], offset, ct).ConfigureAwait(false);

                long real = Math.Min(read, totalSize - offset);
                onBytes?.Invoke(real);
            }
        }

        var slots = new Task[Math.Max(1, (int)Math.Min(depth, Math.Max(1, totalSize / chunk + 1)))];
        for (int i = 0; i < slots.Length; i++) slots[i] = RunSlot();
        await Task.WhenAll(slots).ConfigureAwait(false);

        // Undo the sector padding of the final chunk.
        if (!Win32.SetFileInformationByHandle(dst, Win32.FileEndOfFileInfo, totalSize, sizeof(long)))
            throw new Win32Exception($"set end-of-file failed: {destination}");

        dst.Dispose();
        src.Dispose();
        CopyMetadata(source, destination);
    }

    /// <summary>
    /// Marks a handle's I/O as low priority. Best-effort: some drivers and remote
    /// filesystems ignore the hint, and a copy is not worth failing over it.
    /// </summary>
    private static void SetLowIoPriority(SafeFileHandle handle)
    {
        int hint = (int)Win32.IoPriorityHint.Low;
        Win32.SetFileIoPriorityHint(handle, Win32.FileIoPriorityHintInfo, in hint, sizeof(int));
    }

    /// <summary>
    /// Transfer size and alignment for a copy between two volumes.
    ///
    /// Shared with <see cref="CopyBufferPool"/> so the pool hands out blocks of
    /// exactly the shape the pipeline will ask for — otherwise every rent would
    /// miss and the pool would be an elaborate way to allocate.
    /// </summary>
    public static (int Chunk, int Sector) PlanChunk(VolumeProfile source, VolumeProfile destination)
    {
        int sector = Math.Max(source.BytesPerSector, destination.BytesPerSector);
        int chunk = Math.Max(sector, (Math.Min(source.ChunkSize, destination.ChunkSize) / sector) * sector);
        return (chunk, sector);
    }

    private static int RoundUp(long value, int multiple) =>
        (int)Math.Min(int.MaxValue, ((value + multiple - 1) / multiple) * multiple);

    /// <summary>
    /// Small and mid-size files go through CopyFileEx: it is well tuned for this
    /// case, preserves attributes and alternate data streams, and avoids the
    /// per-file setup cost the pipeline would pay for little gain.
    /// </summary>
    public static void CopySmall(string source, string destination, Action<long>? onBytes = null)
    {
        // The progress callback is a legacy DllImport delegate: every instance
        // forces a fresh marshalling stub. Below the large-file threshold the
        // byte-granular reporting is not worth that, so callers pass null.
        long lastReported = 0;
        Win32.CopyProgressRoutine? cb = onBytes is null ? null : (_, transferred, _, _, _, _, _, _, _) =>
        {
            long delta = transferred - lastReported;
            if (delta > 0) { lastReported = transferred; onBytes(delta); }
            return 0; // PROGRESS_CONTINUE
        };

        int cancel = 0;
        if (!Win32.CopyFileEx(source, destination, cb, 0, in cancel, 0))
        {
            // Capture the code before anything else runs. Win32Exception(string)
            // reads GetLastError itself, by which point string interpolation and
            // its allocations may already have overwritten it — and then every
            // failure looks like the wrong kind, including access-denied.
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"copy failed ({error}): {source}");
        }
    }

    /// <summary>
    /// Copies one file, choosing the primitive from the two volume profiles.
    ///
    /// The size-versus-threshold rule lives here alone. It used to be spelled out
    /// at three call sites, which is how the elevated worker ended up on a subtly
    /// different code path from the engine.
    /// </summary>
    public static async Task CopyOneAsync(
        string source, string destination, long size,
        Action<long>? onBytes = null,
        JobControl? control = null,
        bool backgroundPriority = false,
        CancellationToken ct = default)
    {
        VolumeProfile src = VolumeProfiler.ForPath(source);
        VolumeProfile dst = VolumeProfiler.ForPath(destination);

        if (size >= Math.Max(src.LargeFileThreshold, dst.LargeFileThreshold))
        {
            await CopyLargeAsync(source, destination, src, dst, onBytes,
                                 control: control, backgroundPriority: backgroundPriority,
                                 knownSize: size, ct: ct).ConfigureAwait(false);
        }
        else
        {
            CopySmall(source, destination);
            onBytes?.Invoke(size);
        }
    }

    /// <summary>
    /// Rename on the same volume. Independent of file size — this is why a same-volume
    /// move of 100 GB finishes instantly instead of copying and deleting.
    /// </summary>
    public static bool TryRename(string source, string destination, bool overwrite)
    {
        uint flags = Win32.MOVEFILE_WRITE_THROUGH | (overwrite ? Win32.MOVEFILE_REPLACE_EXISTING : 0);
        return Win32.MoveFileEx(source, destination, flags);
    }

    private static void CopyMetadata(string source, string destination)
    {
        try
        {
            var si = new FileInfo(source);
            var di = new FileInfo(destination)
            {
                CreationTimeUtc  = si.CreationTimeUtc,
                LastWriteTimeUtc = si.LastWriteTimeUtc,
                LastAccessTimeUtc = si.LastAccessTimeUtc,
            };
            di.Attributes = si.Attributes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Timestamps are not worth failing a completed copy over.
        }
    }
}
