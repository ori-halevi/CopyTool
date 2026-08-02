namespace CopyTool.Core;

public enum ConflictPolicy { Ask, Skip, Overwrite, KeepBoth, KeepNewer, KeepLarger }
public enum IdenticalPolicy { Ask, SkipAndReport, CopyAnyway }
public enum LockedPolicy { Ask, Retry, Skip }
public enum IoErrorPolicy { Ask, RetryThrice, SkipAndLog }
public enum ElevationPolicy { Ask, ElevateNow, SkipProtected }

/// <summary>
/// What happens when the job hits something that would otherwise stop it.
///
/// Every policy is decided up front and can be changed while the job runs — the
/// point being that you should be able to walk away from a copy and come back to
/// a finished one, instead of finding it waiting on a question asked ten minutes
/// after you left.
/// </summary>
/// <remarks>
/// Deliberately not observable. The engine reads each value fresh on the next
/// file, and the UI raises its own notifications from the chip that owns the
/// setter — so change tracking here would be plumbing nobody subscribes to.
/// </remarks>
public sealed class JobPolicies
{
    public ConflictPolicy Conflict { get; set; } = ConflictPolicy.Ask;
    public IdenticalPolicy Identical { get; set; } = IdenticalPolicy.SkipAndReport;
    public LockedPolicy Locked { get; set; } = LockedPolicy.Retry;
    public IoErrorPolicy IoError { get; set; } = IoErrorPolicy.RetryThrice;
    public ElevationPolicy Elevation { get; set; } = ElevationPolicy.Ask;

    /// <summary>
    /// Run the transfer at low I/O priority so the rest of the machine stays
    /// responsive. Costs throughput; worth it whenever the copy is not the thing
    /// you are waiting on.
    /// </summary>
    public bool BackgroundIo { get; set; }
}

/// <summary>
/// Why an item is waiting. Elevation is one of these rather than a mechanism of
/// its own: it is the same shape of problem — the job could not decide alone, so
/// the item is parked and the copy carries on — and the only difference is who
/// answers, a dialog or a UAC prompt.
/// </summary>
public enum DecisionKind { NameConflict, Identical, Locked, IoError, NeedsElevation }

/// <summary>Why an item was not copied. A code, so the wording lives in the UI.</summary>
public enum SkipReason
{
    Identical,
    AlreadyExists,
    DestinationNewer,
    DestinationLarger,
    Locked,
    IoError,
}

/// <summary>
/// An item the job could not decide on its own. It is parked here and the copy
/// carries on; nothing blocks waiting for an answer.
/// </summary>
/// <param name="Holders">
/// Applications holding the file open, when that is what stopped us. Names only —
/// the sentence around them is the UI's business.
/// </param>
/// <param name="SystemMessage">Raw OS error text, never translated, for logs and diagnosis.</param>
public sealed record PendingDecision(
    DecisionKind Kind,
    string Source,
    string Destination,
    long SourceSize,
    long DestinationSize,
    DateTime SourceModified,
    DateTime DestinationModified,
    IReadOnlyList<string>? Holders = null,
    string? SystemMessage = null);

/// <summary>Something that was deliberately not copied, recorded so it is never silent.</summary>
public sealed record SkippedItem(
    string Source,
    string Destination,
    SkipReason Reason,
    IReadOnlyList<string>? Holders = null,
    string? SystemMessage = null);
