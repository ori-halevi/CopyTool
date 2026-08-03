using System.Windows;
using System.Windows.Controls;

namespace CopyTool.Host.Ui;

public partial class ProgressWindow : Window
{
    /// <summary>
    /// Every job the host has, not one of them. The window is created once and
    /// outlives any single job; the queue decides which one fills the detail area.
    /// </summary>
    public QueueViewModel Queue { get; }

    public ProgressWindow(QueueViewModel queue)
    {
        InitializeComponent();
        Queue = queue;
        // The window itself, so that `Queue.Primary` in a binding follows the
        // queue's own notifications as the shown job changes.
        DataContext = this;
        Queue.PropertyChanged += OnQueueChanged;
    }

    private bool _grewForList;

    /// <summary>
    /// The list needs room it was not sized for. Growing once when the second job
    /// arrives keeps a single drop looking exactly as it always did, and stops the
    /// list from eating the throughput graph on every later one.
    /// </summary>
    private void OnQueueChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QueueViewModel.ShowList)) return;
        if (_grewForList || !Queue.ShowList) return;

        _grewForList = true;
        Height = Math.Min(Height + 130, SystemParameters.WorkArea.Height - 80);
    }

    /// <summary>The buttons act on whatever is currently shown in full.</summary>
    private JobViewModel? Job => Queue.Primary;

    private void OnPauseClick(object sender, RoutedEventArgs e) => Job?.TogglePause();

    // No manual IsEnabled here: a local value would outlive this job and leave the
    // buttons dead for the next one. IsRunning already drives them.
    private void OnCancelClick(object sender, RoutedEventArgs e) => Job?.Cancel();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnElevateClick(object sender, RoutedEventArgs e) => Job?.RequestElevation();

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
        Queue.PropertyChanged -= OnQueueChanged;
        DataContext = null;
        base.OnClosed(e);
    }
}
