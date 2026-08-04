using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CopyTool.Host.Ui;

/// <summary>
/// Every job the host is working on, in one window.
///
/// A window per drop stacks up: three right-drags in a row used to mean three
/// overlapping windows, each with its own buttons and its own idea of what was
/// happening. One window with a list is what the built-in copy dialog does, and
/// it is also what makes sensible scheduling visible — jobs waiting on a busy
/// disk look queued rather than frozen.
/// </summary>
public sealed class QueueViewModel : INotifyPropertyChanged
{
    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    private JobViewModel? _primary;

    /// <summary>
    /// The job shown in full. Everything else is a one-line row.
    ///
    /// Settable, and bound to the list's selection: clicking a row brings that job
    /// into the detail area, and the pause and cancel buttons follow it there. That
    /// is what makes it possible to cancel a job waiting its turn without
    /// cancelling the one that is running — which, with no way to select, could
    /// only be done by cancelling the wrong job first.
    ///
    /// The automatic hand-over still applies, but only when the shown job has
    /// finished, so it never takes the detail area away from something the user
    /// deliberately brought up.
    /// </summary>
    public JobViewModel? Primary
    {
        get => _primary;
        set
        {
            // A list clears its selection when the selected row is removed. That is
            // a job finishing, not a request to empty the detail area, so fall back
            // to whatever should be shown in its place.
            value ??= PickPrimary();

            if (ReferenceEquals(_primary, value)) return;

            if (_primary is not null) _primary.IsPrimary = false;
            _primary = value;
            if (_primary is not null) _primary.IsPrimary = true;

            Notify();
            Notify(nameof(HasPrimary));
        }
    }

    public bool HasPrimary => _primary is not null;

    /// <summary>The list only earns its space once there is more than one job.</summary>
    public bool ShowList => Jobs.Count > 1;

    public string Header => Jobs.Count switch
    {
        0 or 1 => "",
        _ => $"{Jobs.Count(j => j.Status != JobStatus.Finished):N0} פעולות פעילות מתוך {Jobs.Count:N0}",
    };

    public void Add(JobViewModel job)
    {
        Jobs.Add(job);
        job.PropertyChanged += OnJobChanged;

        // The first job to arrive owns the detail area; later ones take it over
        // only once it frees up.
        Primary ??= job;
        NotifyList();
    }

    public void Remove(JobViewModel job)
    {
        job.PropertyChanged -= OnJobChanged;
        Jobs.Remove(job);

        if (ReferenceEquals(Primary, job)) Primary = PickPrimary();
        NotifyList();
    }

    private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(JobViewModel.Status) or null)) return;

        // Hand the detail area to something that is actually running.
        if (Primary is null || Primary.Status == JobStatus.Finished)
        {
            JobViewModel? next = PickPrimary();
            if (next is not null) Primary = next;
        }

        NotifyList();
    }

    private JobViewModel? PickPrimary() =>
        Jobs.FirstOrDefault(j => j.Status == JobStatus.Running)
        ?? Jobs.FirstOrDefault(j => j.Status != JobStatus.Finished)
        ?? Jobs.FirstOrDefault();

    private void NotifyList()
    {
        Notify(nameof(ShowList));
        Notify(nameof(Header));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
