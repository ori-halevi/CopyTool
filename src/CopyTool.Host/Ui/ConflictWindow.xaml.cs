using System.Windows;
using System.Windows.Controls;

namespace CopyTool.Host.Ui;

public partial class ConflictWindow : Window
{
    private readonly ConflictViewModel _vm;

    public ConflictWindow(ConflictViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    /// <summary>Null when the user cancelled; otherwise the list to copy.</summary>
    public List<(string Source, string Destination, long Size)>? Result { get; private set; }

    private void OnAllReplace(object sender, RoutedEventArgs e) => _vm.ApplyToAll(ConflictChoice.Replace);
    private void OnAllSkip(object sender, RoutedEventArgs e) => _vm.ApplyToAll(ConflictChoice.Skip);
    private void OnAllKeepBoth(object sender, RoutedEventArgs e) => _vm.ApplyToAll(ConflictChoice.KeepBoth);
    private void OnAllKeepNewer(object sender, RoutedEventArgs e) => _vm.KeepNewerForAll();

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
        // Anything still undecided is treated as "skip". Leaving it unanswered and
        // silently copying would be the worse surprise.
        Result = _vm.BuildCopyList();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }
}
