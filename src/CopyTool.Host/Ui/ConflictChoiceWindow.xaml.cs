using System.Windows;
using System.Windows.Input;

namespace CopyTool.Host.Ui;

/// <summary>What the user answered for the whole set of conflicts at once.</summary>
public enum ConflictAction
{
    /// <summary>
    /// The window was closed. Shutting a question is an answer to it — do not copy
    /// these — so this is acted on exactly like <see cref="SkipAll"/>. Kept as its
    /// own value only so the log can tell the two apart.
    /// </summary>
    Dismissed,
    ReplaceAll,
    SkipAll,
    KeepBothAll,
    /// <summary>Open the side-by-side list and decide item by item.</summary>
    DecidePerFile,
}

/// <summary>
/// The first thing shown when a job parks conflicts: one question, four answers.
///
/// Almost every conflict set has one answer for all of it, and making that answer
/// cost a trip through a list of two hundred files is how a dialog becomes a thing
/// people dread. The detailed list is still one click away, and it is the same
/// list — this window only shortens the common path to it.
/// </summary>
public partial class ConflictChoiceWindow : Window
{
    public ConflictChoiceWindow(ConflictViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => ReplaceButton.Focus();
    }

    /// <summary>Dismissed unless one of the four is picked — including on Escape.</summary>
    public ConflictAction Action { get; private set; } = ConflictAction.Dismissed;

    private void Answer(ConflictAction action)
    {
        Action = action;
        DialogResult = true;
    }

    private void OnReplaceAll(object sender, RoutedEventArgs e) => Answer(ConflictAction.ReplaceAll);
    private void OnSkipAll(object sender, RoutedEventArgs e) => Answer(ConflictAction.SkipAll);
    private void OnKeepBothAll(object sender, RoutedEventArgs e) => Answer(ConflictAction.KeepBothAll);
    private void OnDecidePerFile(object sender, RoutedEventArgs e) => Answer(ConflictAction.DecidePerFile);

    /// <summary>
    /// Escape leaves without answering. No button carries IsCancel, because that
    /// would also make Escape look like a decision in the automation tree.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }

        base.OnPreviewKeyDown(e);
    }
}
