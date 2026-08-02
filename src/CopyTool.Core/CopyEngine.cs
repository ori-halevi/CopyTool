using System.Collections.Concurrent;
using System.Diagnostics;

namespace CopyTool.Core;

public enum CopyOperation { Copy, Move }

public sealed record CopyProgress
{
    public required long BytesDone { get; init; }
    public required long BytesTotal { get; init; }
    public required int FilesDone { get; init; }
    public required int FilesTotal { get; init; }
    public required double BytesPerSecond { get; init; }
    public required int Workers { get; init; }
    public string? CurrentFile { get; init; }
    public int PendingCount { get; init; }
    public int SkippedCount { get; init; }
    public int NeedsElevationCount { get; init; }
}

public sealed record CopyFailure(string Source, string Destination, string Reason);

public sealed record CopyReport
{
    public required long BytesCopied { get; init; }
    public required int FilesCopied { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required string Strategy { get; init; }
    public required IReadOnlyList<CopyFailure> Failures { get; init; }
    public required IReadOnlyList<SkippedItem> Skipped { get; init; }

    /// <summary>
    /// Everything the job parked, of every kind — name conflicts, locked files,
    /// I/O errors and permission problems alike. One list, because they are one
    /// mechanism: filter by <see cref="PendingDecision.Kind"/> to pick a resolver.
    /// </summary>
    public required IReadOnlyList<PendingDecision> Pending { get; init; }

    /// <summary>Items blocked purely by permissions — a question, not a failure.</summary>
    public IEnumerable<PendingDecision> NeedsElevation =>
        Pending.Where(p => p.Kind == DecisionKind.NeedsElevation);

    /// <summary>Items a person has to answer, as opposed to the OS.</summary>
    public IEnumerable<PendingDecision> NeedsAnswer =>
        Pending.Where(p => p.Kind != DecisionKind.NeedsElevation);

    public double BytesPerSecond => Elapsed.TotalSeconds > 0 ? BytesCopied / Elapsed.TotalSeconds : 0;
}

/// <summary>
/// Plans and runs one copy or move.
///
/// Strategy is chosen per job from the size histogram and the two volume
/// profiles, then corrected while running by <see cref="ThroughputController"/>.
/// A failure on one file never aborts the job — failures are collected and
/// reported, which is what makes "come back in an hour and it is 99% done"
/// possible.
/// </summary>
public sealed class CopyEngine
{
    private long _bytesDone;
    private long _bytesNotWritten;
    private int _filesDone;

    /// <summary>
    /// Counts a file as accounted-for without claiming it was written.
    ///
    /// The progress bar has to advance past skipped and deferred items or it would
    /// stall short of 100%, but reporting those bytes as copied produces summaries
    /// like "0 files, 1 GB at 87 GB/s". Progress and throughput are different
    /// questions and need different counters.
    /// </summary>
    private void AccountNotWritten(long size)
    {
        Interlocked.Add(ref _bytesDone, size);
        Interlocked.Add(ref _bytesNotWritten, size);
    }

    private readonly ConcurrentBag<SkippedItem> _skipped = [];

    /// <summary>
    /// Everything the job parked, of every kind. A queue rather than a bag so a
    /// caller holding an elevated worker can drain the permission items while the
    /// copy is still running.
    /// </summary>
    private readonly ConcurrentQueue<PendingDecision> _pending = new();

    // Counted separately from the collections they mirror: Count on a concurrent
    // collection is not free, and progress is reported five times a second. Split
    // by audience — a question for a person, versus consent for the OS.
    private int _skippedCount, _pendingCount, _elevationCount;

    /// <summary>
    /// Sources whose contents are now at the destination — the only files a move
    /// may delete. Recorded as they succeed rather than inferred afterwards.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _arrived = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Marks a source as safely at the destination.</summary>
    private void MarkArrived(string source) => _arrived[source] = 0;

    private ThroughputController? _controller;

    /// <summary>
    /// Destination directories that permissions prevented us from creating. The
    /// whole tree is created up front, so an exact set is enough — no prefix
    /// matching and no per-file syscall.
    /// </summary>
    private readonly HashSet<string> _blockedDirectories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many parked items of a given kind are still outstanding.</summary>
    public int CountPending(DecisionKind kind) =>
        kind == DecisionKind.NeedsElevation
            ? Volatile.Read(ref _elevationCount)
            : _pending.Count(p => p.Kind == kind);

    /// <summary>
    /// Takes one parked item of the given kind, if there is one.
    ///
    /// Lets a caller that already holds an elevated worker drain permission items
    /// while the copy is still running, so a consent given up front covers the
    /// whole job instead of only what was known when it was granted.
    ///
    /// Items of other kinds are rotated to the back rather than dropped. Parked
    /// items are exceptional, so the queue is short and the rotation is cheap.
    /// </summary>
    public bool TryTakePending(DecisionKind kind, out PendingDecision item)
    {
        for (int budget = _pending.Count; budget > 0; budget--)
        {
            if (!_pending.TryDequeue(out PendingDecision? candidate)) break;

            if (candidate.Kind == kind)
            {
                if (kind == DecisionKind.NeedsElevation) Interlocked.Decrement(ref _elevationCount);
                else Interlocked.Decrement(ref _pendingCount);

                item = candidate;
                return true;
            }

            _pending.Enqueue(candidate);
        }

        item = null!;
        return false;
    }

    public IProgress<CopyProgress>? Progress { get; init; }

    /// <summary>Pause / cancel handle. When null the job simply runs to completion.</summary>
    public JobControl? Control { get; init; }

    /// <summary>Live policy set. Changes take effect from the next file onwards.</summary>
    public JobPolicies Policies { get; init; } = new();

    public async Task<CopyReport> RunAsync(
        ScanResult scan, string destinationRoot, CopyOperation operation, CancellationToken ct = default)
    {
        if (Control is not null)
            ct = CancellationTokenSource.CreateLinkedTokenSource(ct, Control.Token).Token;

        var clock = Stopwatch.StartNew();
        var failures = new ConcurrentBag<CopyFailure>();

        // Creating the tree must not be able to abort the job. A protected
        // destination fails here first, and letting that throw would end the whole
        // operation before a single file was attempted — no banner, no elevation,
        // nothing copied. Files under a directory we could not create fail with
        // access-denied of their own accord and are routed to elevation, where the
        // worker creates the directory itself.
        CreateDirectorySafely(destinationRoot, failures);
        foreach (string rel in scan.Directories)
            CreateDirectorySafely(Path.Combine(destinationRoot, rel), failures);

        VolumeProfile dstProfile = VolumeProfiler.ForPath(destinationRoot);
        VolumeProfile srcProfile = scan.Files.Count > 0
            ? VolumeProfiler.ForPath(scan.Files[0].SourcePath)
            : dstProfile;

        // Fast path: a move within one volume is a directory-entry rename. Size is
        // irrelevant — this is the difference between instant and an hour.
        //
        // Checked per file, not from the first one: a multi-selection can span
        // volumes, and assuming otherwise renamed what it could and then re-copied
        // the same files from sources that no longer existed.
        IReadOnlyList<ScanItem> toCopy = scan.Files;

        if (operation == CopyOperation.Move && scan.Files.Count > 0)
        {
            var remaining = new List<ScanItem>(scan.Files.Count);
            int renamed = RenameSameVolume(scan.Files, destinationRoot, remaining, failures, ct);

            if (renamed > 0 && remaining.Count == 0)
            {
                return new CopyReport
                {
                    BytesCopied = scan.TotalBytes, FilesCopied = renamed, Elapsed = clock.Elapsed,
                    Strategy = "same-volume rename", Failures = failures.ToArray(),
                    Skipped = _skipped.ToArray(), Pending = _pending.ToArray(),
                };
            }

            // Only what was not renamed goes on to be copied.
            toCopy = remaining;
        }

        long threshold = Math.Max(srcProfile.LargeFileThreshold, dstProfile.LargeFileThreshold);
        var large = toCopy.Where(f => f.Size >= threshold).ToArray();
        var small = toCopy.Where(f => f.Size < threshold).ToArray();

        var controller = new ThroughputController(
            Math.Min(srcProfile.InitialWorkers, dstProfile.InitialWorkers),
            min: 1,
            max: Math.Max(4, Environment.ProcessorCount * 2));
        _controller = controller;

        // Same physical volume on both ends: reads and writes contend, so cap
        // concurrency hard instead of letting the controller discover it slowly.
        bool sameVolume = scan.Files.Count > 0 && VolumeProfiler.SameVolume(scan.Files[0].SourcePath, destinationRoot);
        int workerCeiling = sameVolume && srcProfile.Media == MediaKind.Hdd ? 1 : int.MaxValue;

        await CopySmallFilesAsync(small, destinationRoot, controller, workerCeiling, scan, failures, ct)
            .ConfigureAwait(false);

        await CopyLargeFilesAsync(large, destinationRoot, srcProfile, dstProfile, scan, failures, ct)
            .ConfigureAwait(false);

        if (operation == CopyOperation.Move)
            DeleteMovedSources(failures);

        string strategy = $"{small.Length} small via CopyFileEx, {large.Length} large via unbuffered pipeline";

        return new CopyReport
        {
            BytesCopied = Interlocked.Read(ref _bytesDone) - Interlocked.Read(ref _bytesNotWritten),
            FilesCopied = Volatile.Read(ref _filesDone),
            Elapsed = clock.Elapsed,
            Strategy = strategy,
            Failures = failures.ToArray(),
            Skipped = _skipped.ToArray(),
            Pending = _pending.ToArray(),
        };
    }

    /// <summary>
    /// Copies an explicit list of source→destination pairs, overwriting whatever
    /// is there.
    ///
    /// Used once the user has answered the questions the job parked earlier, and
    /// by the elevated worker. Policies do not apply here: the decision has
    /// already been made, and re-asking would be absurd.
    /// </summary>
    public async Task<CopyReport> CopyExplicitAsync(
        IReadOnlyList<(string Source, string Destination, long Size)> items,
        Action<string, long>? onFileDone = null,
        CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var failures = new ConcurrentBag<CopyFailure>();
        long total = items.Sum(i => i.Size);
        long bytes = 0;
        int copied = 0;

        foreach (var (source, destination, size) in items)
        {
            ct.ThrowIfCancellationRequested();
            Control?.WaitWhilePaused(ct);

            try
            {
                string? dir = Path.GetDirectoryName(destination);
                if (dir is not null) Directory.CreateDirectory(dir);

                await FileCopier.CopyOneAsync(
                    source, destination, size,
                    b =>
                    {
                        Interlocked.Add(ref bytes, b);
                        // Reported as bytes land, so a long elevated transfer shows
                        // movement instead of a window that looks frozen.
                        Progress?.Report(new CopyProgress
                        {
                            BytesDone = Interlocked.Read(ref bytes), BytesTotal = total,
                            FilesDone = copied, FilesTotal = items.Count,
                            BytesPerSecond = 0, Workers = 1, CurrentFile = source,
                        });
                    },
                    Control, Policies.BackgroundIo, ct).ConfigureAwait(false);

                copied++;
                onFileDone?.Invoke(destination, size);
            }
            catch (OperationCanceledException)
            {
                Files.TryDelete(destination);
                throw;
            }
            catch (Exception e)
            {
                Files.TryDelete(destination);
                failures.Add(new CopyFailure(source, destination, e.Message));
            }
        }

        return new CopyReport
        {
            BytesCopied = bytes,
            FilesCopied = copied,
            Elapsed = clock.Elapsed,
            Strategy = $"{items.Count} resolved item(s)",
            Failures = failures.ToArray(),
            Skipped = [],
            Pending = [],
        };
    }

    /// <summary>
    /// Renames every file that already sits on the destination volume. Anything
    /// else — a different volume, an occupied name, a rename that simply failed —
    /// is added to <paramref name="remaining"/> for the normal copy path, never
    /// silently dropped and never renamed twice.
    /// </summary>
    private int RenameSameVolume(
        IReadOnlyList<ScanItem> files, string destinationRoot,
        List<ScanItem> remaining, ConcurrentBag<CopyFailure> failures, CancellationToken ct)
    {
        int renamed = 0;

        foreach (ScanItem f in files)
        {
            ct.ThrowIfCancellationRequested();
            Control?.WaitWhilePaused(ct);

            if (!VolumeProfiler.SameVolume(f.SourcePath, destinationRoot))
            {
                remaining.Add(f);
                continue;
            }

            string dst = Path.Combine(destinationRoot, f.RelativePath);

            // overwrite:false, so an occupied destination falls through to the
            // copy path where the conflict policy gets its say.
            if (FileCopier.TryRename(f.SourcePath, dst, overwrite: false))
            {
                renamed++;
                Interlocked.Add(ref _bytesDone, f.Size);
                Interlocked.Increment(ref _filesDone);
                // Deliberately not MarkArrived: a rename already removed the source.
            }
            else
            {
                remaining.Add(f);
            }
        }

        return renamed;
    }

    private async Task CopySmallFilesAsync(
        ScanItem[] items, string destinationRoot, ThroughputController controller, int workerCeiling,
        ScanResult scan, ConcurrentBag<CopyFailure> failures, CancellationToken ct)
    {
        if (items.Length == 0) return;

        var queue = new ConcurrentQueue<ScanItem>(items);
        var workers = new List<Task>();
        int active = 0;

        // A worker retires on its own once the controller lowers the target, so
        // the level can fall without cancelling in-flight file copies.
        void Spawn()
        {
            Interlocked.Increment(ref active);
            workers.Add(Task.Run(() =>
            {
                try
                {
                    while (queue.TryDequeue(out ScanItem? item))
                    {
                        ct.ThrowIfCancellationRequested();
                        Control?.WaitWhilePaused(ct);

                        string planned = Path.Combine(destinationRoot, item.RelativePath);
                        if (!TryResolve(item, planned, out string dst))
                        {
                            // Skipped or parked for a decision — either way the job
                            // keeps moving. Nothing here ever waits for an answer.
                            AccountNotWritten(item.Size);
                            continue;
                        }

                        CopySmallWithPolicy(item, dst, failures, ct);

                        if (Volatile.Read(ref active) > Math.Min(controller.Workers, workerCeiling)) break;
                    }
                }
                finally { Interlocked.Decrement(ref active); }
            }, ct));
        }

        int target = Math.Min(controller.Workers, workerCeiling);
        for (int i = 0; i < target; i++) Spawn();

        while (!queue.IsEmpty && !ct.IsCancellationRequested)
        {
            await Task.Delay(200, ct).ConfigureAwait(false);

            controller.Update(Interlocked.Read(ref _bytesDone));
            ReportProgress(scan);

            int want = Math.Min(controller.Workers, workerCeiling);
            while (Volatile.Read(ref active) < want && !queue.IsEmpty) Spawn();
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task CopyLargeFilesAsync(
        ScanItem[] items, string destinationRoot, VolumeProfile srcProfile, VolumeProfile dstProfile,
        ScanResult scan, ConcurrentBag<CopyFailure> failures, CancellationToken ct)
    {
        // Large files already saturate the device from a single stream via the
        // internal read/write pipeline; running several in parallel only adds seeks.
        foreach (ScanItem item in items)
        {
            ct.ThrowIfCancellationRequested();
            string planned = Path.Combine(destinationRoot, item.RelativePath);
            if (!TryResolve(item, planned, out string dst))
            {
                AccountNotWritten(item.Size);
                ReportProgress(scan, item.RelativePath);
                continue;
            }

            await CopyLargeWithPolicyAsync(item, dst, srcProfile, dstProfile, scan, failures, ct)
                .ConfigureAwait(false);

            ReportProgress(scan, item.RelativePath);
        }
    }

    /// <summary>
    /// Copies one large file through the unbuffered pipeline, honouring exactly the
    /// same retry and failure policies as the small-file path.
    /// </summary>
    private async Task CopyLargeWithPolicyAsync(
        ScanItem item, string dst, VolumeProfile srcProfile, VolumeProfile dstProfile,
        ScanResult scan, ConcurrentBag<CopyFailure> failures, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Progress is reported as bytes land, so a retry has to give back what
            // the failed attempt already counted or the bar would run past 100%.
            long attemptBytes = 0;

            try
            {
                Task copy = FileCopier.CopyLargeAsync(
                    item.SourcePath, dst, srcProfile, dstProfile,
                    b =>
                    {
                        Interlocked.Add(ref _bytesDone, b);
                        Interlocked.Add(ref attemptBytes, b);
                    },
                    queueDepth: null,               // taken from the volume profiles
                    control: Control,
                    backgroundPriority: Policies.BackgroundIo,
                    ct: ct);

                // A single large file can take minutes. Without ticking here the
                // UI would sit frozen and only update once the file completed.
                while (!copy.IsCompleted)
                {
                    Task tick = await Task.WhenAny(copy, Task.Delay(200, ct)).ConfigureAwait(false);
                    ReportProgress(scan, item.RelativePath);
                    if (tick == copy) break;
                }
                await copy.ConfigureAwait(false);

                Interlocked.Increment(ref _filesDone);
                MarkArrived(item.SourcePath);
                return;
            }
            catch (OperationCanceledException)
            {
                // The destination was preallocated at its final length, so an
                // interrupted large copy leaves a file of exactly the right size
                // full of garbage — which looks valid to every tool that checks
                // size. Deleting it is the only safe outcome.
                TryDeletePartial(dst);
                throw;
            }
            catch (Exception e) when (Elevation.IsAccessDenied(e))
            {
                Interlocked.Add(ref _bytesDone, -Interlocked.Read(ref attemptBytes));

                // Try the mundane explanation before the alarming one: a read-only
                // destination is not a permissions problem, and UAC cannot fix it.
                if (attempt == 1 && Files.TryClearReadOnly(dst)) continue;

                TryDeletePartial(dst);
                RecordNeedsElevation(item, dst);
                return;
            }
            catch (Exception e)
            {
                Interlocked.Add(ref _bytesDone, -Interlocked.Read(ref attemptBytes));
                TryDeletePartial(dst);

                if (ShouldRetry(e, attempt, out bool locked))
                {
                    await Task.Delay(200 * attempt, ct).ConfigureAwait(false);
                    continue;
                }
                RecordFailure(item, dst, e, locked, failures);
                return;
            }
        }
    }

    /// <summary>
    /// Applies the conflict / identical policies to one file.
    /// Returns false when the file must not be copied — either it was skipped or
    /// it was parked for the user. Neither outcome stops the job.
    /// </summary>
    private bool TryResolve(ScanItem item, string planned, out string destination)
    {
        destination = planned;

        // Its directory could not be created for permission reasons, so attempting
        // the copy would only produce a misleading "path not found". The Count
        // guard keeps the GetDirectoryName allocation out of the common path,
        // where the set is empty for every file in the job.
        if (_blockedDirectories.Count > 0)
        {
            string? dir = Path.GetDirectoryName(planned);
            if (dir is not null && _blockedDirectories.Contains(dir))
            {
                RecordNeedsElevation(item, planned, account: false);
                return false;
            }
        }

        Resolution r = ConflictResolver.Resolve(item, planned, Policies);
        destination = r.Destination;

        switch (r.Action)
        {
            case ResolutionAction.Skip:
                _skipped.Add(new SkippedItem(item.SourcePath, planned, r.Reason));
                Interlocked.Increment(ref _skippedCount);

                // Identical is the one skip a move may still delete for: the same
                // bytes are provably already at the destination. Every other skip
                // means the destination holds something *different*, and removing
                // the source would destroy the only copy of it.
                if (r.Reason == SkipReason.Identical) MarkArrived(item.SourcePath);
                return false;

            case ResolutionAction.Defer:
                // Reuse the metadata Resolve already read; re-deriving it here cost
                // three extra syscalls per deferred file, and "Ask" is the default.
                _pending.Enqueue(new PendingDecision(
                    r.Reason == SkipReason.Identical ? DecisionKind.Identical : DecisionKind.NameConflict,
                    item.SourcePath, planned,
                    item.Size, r.DestinationSize, r.SourceModified, r.DestinationModified));
                Interlocked.Increment(ref _pendingCount);
                return false;

            default:
                return true;
        }
    }

    private const int MaxAttempts = 3;

    /// <summary>
    /// Whether a failed copy is worth another go.
    ///
    /// Shared by both copy paths on purpose: the "locked" and "I/O error" policies
    /// have to mean the same thing for a 2 KB file and a 20 GB one. Keeping this
    /// decision inside one path — as an earlier version did — silently made both
    /// chips inert for exactly the large files where waiting matters most.
    /// </summary>
    private bool ShouldRetry(Exception e, int attempt, out bool locked)
    {
        locked = IsSharingViolation(e);

        return locked
            ? Policies.Locked == LockedPolicy.Retry && attempt < MaxAttempts
            : Policies.IoError == IoErrorPolicy.RetryThrice && attempt < MaxAttempts;
    }

    /// <summary>
    /// A permission problem is a question for the user, not a failure.
    /// </summary>
    /// <param name="account">
    /// False when the caller accounts for the bytes itself. <c>TryResolve</c> does,
    /// via its callers — counting here as well double-counted the file and pushed
    /// the progress bar past 100%.
    /// </param>
    private void RecordNeedsElevation(ScanItem item, string dst, bool account = true)
    {
        _pending.Enqueue(new PendingDecision(
            DecisionKind.NeedsElevation, item.SourcePath, dst,
            item.Size, ConflictResolver.SafeSize(dst),
            item.Modified, ConflictResolver.SafeWriteTime(dst)));
        Interlocked.Increment(ref _elevationCount);
        if (account) AccountNotWritten(item.Size);
    }

    /// <summary>Files a give-up under the configured policy and accounts for the bytes.</summary>
    private void RecordFailure(
        ScanItem item, string dst, Exception e, bool locked, ConcurrentBag<CopyFailure> failures)
    {
        bool defer = locked
            ? Policies.Locked == LockedPolicy.Ask
            : Policies.IoError == IoErrorPolicy.Ask;

        // Name the culprit. "The file is in use" without saying by what is the
        // single most useless message Windows produces, and the answer costs a few
        // milliseconds to look up — only once we have given up.
        IReadOnlyList<string>? holders = locked
            ? LockFinder.WhoIsUsingAny(item.SourcePath, dst)
            : null;

        if (defer)
        {
            _pending.Enqueue(new PendingDecision(
                locked ? DecisionKind.Locked : DecisionKind.IoError,
                item.SourcePath, dst, item.Size, ConflictResolver.SafeSize(dst),
                ConflictResolver.SafeWriteTime(item.SourcePath),
                ConflictResolver.SafeWriteTime(dst), holders, e.Message));
            Interlocked.Increment(ref _pendingCount);
        }
        else if ((locked && Policies.Locked == LockedPolicy.Skip) ||
                 (!locked && Policies.IoError == IoErrorPolicy.SkipAndLog))
        {
            _skipped.Add(new SkippedItem(
                item.SourcePath, dst,
                locked ? SkipReason.Locked : SkipReason.IoError, holders, e.Message));
            Interlocked.Increment(ref _skippedCount);
        }
        else
        {
            // Raw OS text, never translated: this is the field that gets logged.
            failures.Add(new CopyFailure(item.SourcePath, dst, e.Message));
        }

        AccountNotWritten(item.Size);
    }

    /// <summary>Copies one small file, honouring the retry and failure policies.</summary>
    private void CopySmallWithPolicy(
        ScanItem item, string dst, ConcurrentBag<CopyFailure> failures, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // No per-byte callback: for a sub-threshold file the progress
                // granularity buys nothing and costs a delegate, a display class
                // and a marshalling stub on every single file.
                FileCopier.CopySmall(item.SourcePath, dst);
                Interlocked.Add(ref _bytesDone, item.Size);
                Interlocked.Increment(ref _filesDone);
                MarkArrived(item.SourcePath);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (Elevation.IsAccessDenied(e))
            {
                // Try the mundane explanation before the alarming one.
                if (attempt == 1 && Files.TryClearReadOnly(dst)) continue;

                RecordNeedsElevation(item, dst);
                return;
            }
            catch (Exception e)
            {
                if (ShouldRetry(e, attempt, out bool locked))
                {
                    // Back off a little; hammering a locked file changes nothing.
                    Thread.Sleep(200 * attempt);
                    continue;
                }
                RecordFailure(item, dst, e, locked, failures);
                return;
            }
        }
    }

    /// <summary>
    /// ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION — the file exists and is
    /// readable in principle, another process just has it open right now. Worth
    /// distinguishing, because the right answer is to wait rather than to give up.
    /// </summary>
    private static bool IsSharingViolation(Exception e) => Files.Win32ErrorOf(e) is 32 or 33;

    private void CreateDirectorySafely(string path, ConcurrentBag<CopyFailure> failures)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception e) when (Elevation.IsAccessDenied(e))
        {
            // Remember it. Files under a directory that could not be created do
            // not report access-denied themselves — they report "path not found",
            // because the directory simply is not there. Without this the real
            // cause is lost and a permission question looks like a broken path.
            _blockedDirectories.Add(path);
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException)
        {
            failures.Add(new CopyFailure("", path, e.Message));
        }
    }

    /// <summary>A half-written destination is worse than none — it looks valid.</summary>
    private static void TryDeletePartial(string path) => Files.TryDelete(path);

    /// <summary>
    /// Removes the sources of a move — and only those that demonstrably arrived.
    ///
    /// An allow-list, never a deny-list. Deriving "safe to delete" by subtracting
    /// failures is wrong in three separate ways: a file parked for a name-conflict
    /// decision has not been copied, nor has one waiting on elevation, nor one
    /// skipped because the destination was newer. None of them are failures, and
    /// deleting any of them destroys the only copy of the user's data.
    /// </summary>
    private void DeleteMovedSources(ConcurrentBag<CopyFailure> failures)
    {
        foreach (string source in _arrived.Keys)
        {
            try { File.Delete(source); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failures.Add(new CopyFailure(source, "", e.Message));
            }
        }
    }

    private void ReportProgress(ScanResult scan, string? current = null)
    {
        Progress?.Report(new CopyProgress
        {
            BytesDone = Interlocked.Read(ref _bytesDone),
            BytesTotal = scan.TotalBytes,
            FilesDone = Volatile.Read(ref _filesDone),
            FilesTotal = scan.FileCount,
            BytesPerSecond = _controller?.CurrentRate ?? 0,
            Workers = _controller?.Workers ?? 1,
            CurrentFile = current,
            // Interlocked counters rather than ConcurrentBag.Count: Count freezes
            // the bag by taking every per-thread lock, and this runs five times a
            // second while workers are pushing into those same bags.
            PendingCount = Volatile.Read(ref _pendingCount),
            SkippedCount = Volatile.Read(ref _skippedCount),
            NeedsElevationCount = Volatile.Read(ref _elevationCount),
        });
    }
}
