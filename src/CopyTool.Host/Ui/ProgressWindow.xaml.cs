using System.Windows;
using System.Windows.Controls;

namespace CopyTool.Host.Ui;

public partial class ProgressWindow : Window
{
    private readonly JobViewModel _vm;

    public ProgressWindow(JobViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void OnPauseClick(object sender, RoutedEventArgs e) => _vm.TogglePause();

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _vm.Cancel();
        CancelButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnElevateClick(object sender, RoutedEventArgs e) => _vm.RequestElevation();

    /// <summary>
    /// Opens a chip's options as a context menu anchored to the chip. Built on
    /// demand rather than declared in XAML so the checkmark always reflects the
    /// value at the moment of the click, including changes made mid-job.
    /// </summary>
    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PolicyChip chip } button) return;

        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            FlowDirection = FlowDirection.RightToLeft,
        };

        foreach (PolicyOption option in chip.Options)
        {
            var item = new MenuItem
            {
                Header = option.Label,
                IsCheckable = true,
                IsChecked = Equals(option.Value, chip.Current),
            };
            item.Click += (_, _) => chip.Select(option);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// Closing the window must never cancel the copy — it only hides progress.
    /// Cancelling is what the Cancel button is for, and conflating the two is a
    /// good way to lose an hour of work to a stray click.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        DataContext = null;
        base.OnClosed(e);
    }
}
