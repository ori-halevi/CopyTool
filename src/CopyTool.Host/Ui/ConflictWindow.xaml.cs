using System.ComponentModel;
using System.Diagnostics;
// UseWPF swaps the implicit-using set for the desktop one, which drops System.IO.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CopyTool.Core;

namespace CopyTool.Host.Ui;

public partial class ConflictWindow : Window
{
    private readonly ConflictViewModel _vm;

    public ConflictWindow(ConflictViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // Everything starts unanswered, which is also the right answer if the
        // window is dismissed with its close button — neither handler runs then,
        // and a question closed by the X is no more answered than one closed by
        // "cancel".
        Unanswered = vm.AllDecisions();
    }

    /// <summary>Null when the user cancelled; otherwise the list to copy.</summary>
    public List<(string Source, string Destination, long Size)>? Result { get; private set; }

    /// <summary>
    /// Conflicts the user was shown and did not answer. Their files are not
    /// copied, and this is the only place that records which they were.
    /// </summary>
    public List<PendingDecision> Unanswered { get; private set; }

    private void OnAllReplace(object sender, RoutedEventArgs e) => _vm.ApplyToAll(ConflictChoice.Replace);
    private void OnAllSkip(object sender, RoutedEventArgs e) => _vm.ApplyToAll(ConflictChoice.Skip);
    private void OnAllKeepBoth(object sender, RoutedEventArgs e) => _vm.ApplyToAll(ConflictChoice.KeepBoth);
    private void OnAllKeepNewer(object sender, RoutedEventArgs e) => _vm.KeepNewerForAll();

    /// <summary>
    /// Opens the file on the side that was double-clicked.
    ///
    /// Two files with the same name and neither obviously right is exactly when a
    /// size and a date are not enough — you have to look at the thing. Explorer's
    /// own conflict dialog opens a file on double-click for the same reason.
    ///
    /// Deliberately double-click and not single: a stray click while reaching for
    /// the choice button must never launch anything. The shell decides how to open
    /// it, so an executable still meets whatever warning it would normally meet.
    /// </summary>
    private void OnSideDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2 || sender is not FrameworkElement { Tag: string path }) return;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        e.Handled = true;

        try
        {
            using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // No association, cancelled at the security prompt, or the file went
            // away. Looking at a file is a convenience; failing to is not an error
            // worth interrupting the decision for.
        }
    }

    private void OnItemChoiceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ConflictItem item } button) return;

        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
        };

        foreach ((string label, ConflictChoice choice) in new[]
                 {
                     ("החלף", ConflictChoice.Replace),
                     ("דלג", ConflictChoice.Skip),
                     ("שמור שניהם", ConflictChoice.KeepBoth),
                 })
        {
            var entry = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = item.Choice == choice,
            };
            entry.Click += (_, _) => item.Choice = choice;
            menu.Items.Add(entry);
        }

        menu.IsOpen = true;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        // Anything still undecided is treated as "skip", because copying something
        // nobody asked for is the worse surprise — but it is reported by name
        // afterwards, since not answering is not the same as choosing to skip.
        Result = _vm.BuildCopyList();
        Unanswered = _vm.BuildUnansweredList();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Result = null;
        Unanswered = _vm.AllDecisions();
        DialogResult = false;
    }
}
