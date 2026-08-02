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

    public string SourceSummary => Describe(Decision.SourceSize, Decision.SourceModified);
    public string DestinationSummary => Describe(Decision.DestinationSize, Decision.DestinationModified);

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

    private static string Describe(long size, DateTime modified) =>
        modified == DateTime.MinValue
            ? Format.Bytes(size)
            : $"{Format.Bytes(size)}  ·  {modified.ToLocalTime():dd/MM/yyyy HH:mm}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ConflictViewModel : INotifyPropertyChanged
{
    private int _undecided;

    public ConflictViewModel(IEnumerable<PendingDecision> decisions)
    {
        Items = new ObservableCollection<ConflictItem>(decisions.Select(d => new ConflictItem(d)));
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

    public int UndecidedCount => _undecided;

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
