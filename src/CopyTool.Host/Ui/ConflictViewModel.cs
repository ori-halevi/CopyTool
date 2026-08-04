using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using CopyTool.Core;

namespace CopyTool.Host.Ui;

public enum ConflictChoice { Undecided, Replace, Skip, KeepBoth }

/// <summary>
/// One parked decision, with both sides laid out so the difference is obvious
/// without opening anything: size, date, and which of the two is newer or bigger.
/// </summary>
public sealed class ConflictItem : INotifyPropertyChanged
{
    private ConflictChoice _choice = ConflictChoice.Undecided;

    public ConflictItem(PendingDecision decision)
    {
        Decision = decision;
        Name = Path.GetFileName(decision.Source);
    }

    public PendingDecision Decision { get; }
    public string Name { get; }

    public string SourcePath => Decision.Source;
    public string DestinationPath => Decision.Destination;

    // Size and date are separate values rather than one summary line, because the
    // one that differs gets emphasised and you cannot embolden half a string.
    public string SourceSizeText => Format.Bytes(Decision.SourceSize);
    public string DestinationSizeText => Format.Bytes(Decision.DestinationSize);

    public string SourceDateText => DateText(Decision.SourceModified);
    public string DestinationDateText => DateText(Decision.DestinationModified);

    public bool HasSourceDate => Exists(Decision.SourceModified);
    public bool HasDestinationDate => Exists(Decision.DestinationModified);

    /// <summary>
    /// Whether there is really a file on both sides to compare.
    ///
    /// A locked file or an I/O error is parked with nothing at the destination, and
    /// "the source is larger" is a meaningless thing to emphasise when the other
    /// side of the comparison does not exist.
    /// </summary>
    private bool Comparable => HasSourceDate && HasDestinationDate;

    // Which side wins on each axis. The dialog emphasises exactly these, so the
    // value you are deciding on is legible without reading — the same reason the
    // built-in Windows copy dialog puts the newer date in bold. Core owns the
    // two-second tolerance; re-deriving "newer" here would let the emphasis and
    // the verdict disagree about the same file.
    public bool SourceIsNewer => Comparable &&
        ConflictResolver.IsNewer(Decision.SourceModified, Decision.DestinationModified);

    public bool DestinationIsNewer => Comparable &&
        ConflictResolver.IsNewer(Decision.DestinationModified, Decision.SourceModified);

    public bool SourceIsLarger => Comparable && Decision.SourceSize > Decision.DestinationSize;
    public bool DestinationIsLarger => Comparable && Decision.DestinationSize > Decision.SourceSize;

    private static bool Exists(DateTime modified) => modified > DateTime.MinValue;

    /// <summary>The one fact that usually decides it, stated plainly.</summary>
    public string Verdict
    {
        get
        {
            if (Decision.Kind != DecisionKind.NameConflict) return Text.Describe(Decision);

            // Core owns the tolerance and the comparison; re-encoding "2 seconds"
            // here would let the dialog and the chip policy disagree on one file.
            bool newer = ConflictResolver.IsNewer(Decision.SourceModified, Decision.DestinationModified);
            bool older = ConflictResolver.IsNewer(Decision.DestinationModified, Decision.SourceModified);
            string time = newer ? "המקור חדש יותר" : older ? "היעד חדש יותר" : "אותו תאריך";

            long diff = Decision.SourceSize - Decision.DestinationSize;
            string size = diff > 0 ? $"המקור גדול ב-{Format.Bytes(diff)}"
                        : diff < 0 ? $"היעד גדול ב-{Format.Bytes(-diff)}"
                        : "אותו גודל";

            return $"{time} · {size}";
        }
    }

    public ConflictChoice Choice
    {
        get => _choice;
        set
        {
            if (_choice == value) return;
            _choice = value;
            Notify();
            Notify(nameof(ChoiceLabel));
            Notify(nameof(IsDecided));
        }
    }

    public bool IsDecided => _choice != ConflictChoice.Undecided;

    public string ChoiceLabel => _choice switch
    {
        ConflictChoice.Replace => "החלף",
        ConflictChoice.Skip => "דלג",
        ConflictChoice.KeepBoth => "שמור שניהם",
        _ => "בחר…",
    };

    private static string DateText(DateTime modified) =>
        Exists(modified) ? $"{modified.ToLocalTime():dd/MM/yyyy HH:mm}" : "";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ConflictViewModel : INotifyPropertyChanged
{
    private int _undecided;

    public ConflictViewModel(IEnumerable<PendingDecision> decisions)
    {
        // Sorted by path. Parked items come off a concurrent queue in whatever
        // order the workers happened to reach them, which for a folder of related
        // files reads as shuffled — and a list you are about to make decisions in
        // should be in the order you would find it in Explorer.
        Items = new ObservableCollection<ConflictItem>(
            decisions.OrderBy(d => d.Source, StringComparer.OrdinalIgnoreCase)
                     .Select(d => new ConflictItem(d)));
        _undecided = Items.Count;

        // Tracked incrementally. Recounting the list on every change made the bulk
        // actions quadratic — each of N items raised events that re-read an O(N)
        // property, which shows on a few thousand parked conflicts.
        foreach (ConflictItem item in Items)
        {
            item.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != nameof(ConflictItem.IsDecided)) return;
                _undecided += ((ConflictItem)s!).IsDecided ? -1 : +1;
                Notify(nameof(UndecidedCount));
            };
        }
    }

    public ObservableCollection<ConflictItem> Items { get; }

    public string Header => Items.Count == 1
        ? "פריט אחד כבר קיים ביעד"
        : $"{Items.Count:N0} פריטים כבר קיימים ביעד";

    /// <summary>
    /// Which folder this is about. The first dialog can be the only thing on
    /// screen, and "something is already there" is not much use without "where".
    /// </summary>
    public string Into
    {
        get
        {
            if (Items.Count == 0) return "";
            string? folder = Path.GetDirectoryName(Items[0].Decision.Destination);
            return string.IsNullOrEmpty(folder) ? "" : $"אל {folder}";
        }
    }

    public int UndecidedCount => _undecided;

    // --- the identical ones, in one stroke -----------------------------------

    /// <summary>
    /// How many of these are the same file on both sides — same size, same
    /// timestamp within the tolerance. Core decided that when it parked them.
    /// </summary>
    public int IdenticalCount => Items.Count(i => i.Decision.Kind == DecisionKind.Identical);

    public bool HasIdentical => IdenticalCount > 0;

    public string SkipIdenticalLabel => $"דלג על {Text.Items(IdenticalCount)} בעלי אותו תאריך וגודל";

    private bool _skipIdentical;

    /// <summary>
    /// Clears the files that are already there byte for byte, leaving only the
    /// conflicts that are actually a question.
    ///
    /// Unticked to start with, deliberately: the whole reason these are on screen
    /// is that skipping them silently is what the tool used to do wrong. But
    /// re-running a copy over a folder that already has it produces a list of
    /// nothing but these, and one tick has to be enough to get past them.
    /// </summary>
    public bool SkipIdentical
    {
        get => _skipIdentical;
        set
        {
            if (_skipIdentical == value) return;
            _skipIdentical = value;

            foreach (ConflictItem item in Items.Where(i => i.Decision.Kind == DecisionKind.Identical))
            {
                // Unticking only takes back what ticking did. A file the user went
                // and chose "replace" for is their decision, not ours to undo.
                if (value) item.Choice = ConflictChoice.Skip;
                else if (item.Choice == ConflictChoice.Skip) item.Choice = ConflictChoice.Undecided;
            }

            Notify();
        }
    }

    /// <summary>
    /// Bulk actions exist because the common case is one answer for everything —
    /// deciding fifty files one at a time is how Explorer makes people give up.
    /// </summary>
    public void ApplyToAll(ConflictChoice choice)
    {
        foreach (ConflictItem item in Items) item.Choice = choice;
    }

    public void KeepNewerForAll()
    {
        foreach (ConflictItem item in Items)
        {
            item.Choice = ConflictResolver.IsNewer(item.Decision.SourceModified, item.Decision.DestinationModified)
                ? ConflictChoice.Replace
                : ConflictChoice.Skip;
        }
    }

    /// <summary>
    /// Items the dialog is about to close on without an answer.
    ///
    /// Deliberately not the ones answered "skip". That is a decision, and handing
    /// it back as something to report would be telling someone what they just
    /// chose. An unanswered question is the opposite: the file is not copied, and
    /// nothing anywhere says which one or why.
    /// </summary>
    public List<PendingDecision> BuildUnansweredList() =>
        [.. Items.Where(i => i.Choice == ConflictChoice.Undecided).Select(i => i.Decision)];

    /// <summary>Every parked item, for a dialog dismissed without applying anything.</summary>
    public List<PendingDecision> AllDecisions() => [.. Items.Select(i => i.Decision)];

    /// <summary>Resolved pairs ready for <c>CopyEngine.CopyExplicitAsync</c>.</summary>
    public List<(string Source, string Destination, long Size)> BuildCopyList()
    {
        var result = new List<(string, string, long)>();
        foreach (ConflictItem item in Items)
        {
            switch (item.Choice)
            {
                case ConflictChoice.Replace:
                    result.Add((item.Decision.Source, item.Decision.Destination, item.Decision.SourceSize));
                    break;
                case ConflictChoice.KeepBoth:
                    result.Add((item.Decision.Source,
                                ConflictResolver.FreeName(item.Decision.Destination),
                                item.Decision.SourceSize));
                    break;
            }
        }
        return result;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
