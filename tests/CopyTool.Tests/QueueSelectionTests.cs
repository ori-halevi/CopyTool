using CopyTool.Core;
using CopyTool.Host.Ui;
using Xunit;

namespace CopyTool.Tests;

/// <summary>
/// Which job the window shows in full, and who decides.
///
/// With no way to select a row there was exactly one thing you could not do:
/// cancel a job waiting its turn. The buttons acted on whatever the queue had
/// picked, so reaching a queued job meant cancelling the running one first.
/// </summary>
public class QueueSelectionTests
{
    private static JobViewModel Job(string destination) =>
        new("מעתיק", destination, new JobControl(), new JobPolicies());

    [Fact]
    public void The_first_job_owns_the_detail_area()
    {
        var queue = new QueueViewModel();
        JobViewModel first = Job(@"D:\one");
        JobViewModel second = Job(@"E:\two");

        queue.Add(first);
        queue.Add(second);

        Assert.Same(first, queue.Primary);
        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
    }

    [Fact]
    public void Selecting_a_row_brings_that_job_up()
    {
        var queue = new QueueViewModel();
        JobViewModel running = Job(@"D:\one");
        JobViewModel queued = Job(@"E:\two");
        queue.Add(running);
        queue.Add(queued);
        running.Status = JobStatus.Running;

        queue.Primary = queued;

        Assert.Same(queued, queue.Primary);
        Assert.True(queued.IsPrimary);
        Assert.False(running.IsPrimary);
    }

    [Fact]
    public void A_deliberate_choice_is_not_taken_away_by_another_job_starting()
    {
        // The hand-over only fires when the shown job has finished. Otherwise
        // bringing up a queued job to cancel it would last until the next
        // status change somewhere else in the list.
        var queue = new QueueViewModel();
        JobViewModel running = Job(@"D:\one");
        JobViewModel queued = Job(@"E:\two");
        queue.Add(running);
        queue.Add(queued);

        queue.Primary = queued;
        running.Status = JobStatus.Running;

        Assert.Same(queued, queue.Primary);
    }

    [Fact]
    public void The_detail_area_moves_on_once_the_shown_job_finishes()
    {
        var queue = new QueueViewModel();
        JobViewModel first = Job(@"D:\one");
        JobViewModel second = Job(@"E:\two");
        queue.Add(first);
        queue.Add(second);
        second.Status = JobStatus.Running;

        first.Status = JobStatus.Finished;

        Assert.Same(second, queue.Primary);
    }

    [Fact]
    public void Clearing_the_selection_falls_back_rather_than_blanking()
    {
        // A list drops its selection when the selected row is removed. That is a
        // job finishing, not a request for an empty detail area.
        var queue = new QueueViewModel();
        JobViewModel first = Job(@"D:\one");
        JobViewModel second = Job(@"E:\two");
        queue.Add(first);
        queue.Add(second);

        queue.Primary = null;

        Assert.NotNull(queue.Primary);
        Assert.True(queue.HasPrimary);
    }

    [Fact]
    public void Removing_the_shown_job_hands_over_to_another()
    {
        var queue = new QueueViewModel();
        JobViewModel first = Job(@"D:\one");
        JobViewModel second = Job(@"E:\two");
        queue.Add(first);
        queue.Add(second);

        queue.Remove(first);

        Assert.Same(second, queue.Primary);
        Assert.True(second.IsPrimary);
    }

    [Fact]
    public void An_empty_queue_has_nothing_to_show()
    {
        var queue = new QueueViewModel();
        JobViewModel only = Job(@"D:\one");
        queue.Add(only);

        queue.Remove(only);

        Assert.Null(queue.Primary);
        Assert.False(queue.HasPrimary);
        Assert.False(queue.ShowList);
    }

    [Fact]
    public void The_list_only_earns_its_space_with_more_than_one_job()
    {
        var queue = new QueueViewModel();
        queue.Add(Job(@"D:\one"));
        Assert.False(queue.ShowList);

        queue.Add(Job(@"E:\two"));
        Assert.True(queue.ShowList);
    }
}
