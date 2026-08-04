using CopyTool.Core;
using CopyTool.Host.Ui;
using Xunit;

namespace CopyTool.Tests;

/// <summary>
/// The three answers the first dialog gives for a whole set of conflicts at once.
///
/// Nearly every conflict set has one answer for all of it, so these are the paths
/// that actually get used. Windows offers only two of them at this stage — getting
/// "keep both" out of its dialog costs four clicks — which is the one addition
/// here worth having.
/// </summary>
public class ConflictBulkTests
{
    private static readonly DateTime Noon = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ConflictViewModel Conflicts(params string[] names) =>
        new([.. names.Select(n => new PendingDecision(
            DecisionKind.NameConflict, $@"C:\src\{n}", $@"C:\dst\{n}",
            100, 50, Noon.AddHours(1), Noon))]);

    [Fact]
    public void Replace_all_copies_every_item_onto_its_own_name()
    {
        ConflictViewModel vm = Conflicts("a.txt", "b.txt");

        vm.ApplyToAll(ConflictChoice.Replace);

        Assert.Equal(2, vm.BuildCopyList().Count);
        Assert.All(vm.BuildCopyList(), i => Assert.Equal(@"C:\dst\", Path.GetDirectoryName(i.Destination) + @"\"));
        Assert.Equal([@"C:\dst\a.txt", @"C:\dst\b.txt"], vm.BuildCopyList().Select(i => i.Destination).Order());
        Assert.Empty(vm.BuildUnansweredList());
    }

    [Fact]
    public void Skip_all_copies_nothing_and_leaves_nothing_unanswered()
    {
        // A bulk skip is a decision, so it must not come back as something to
        // report — that is the difference between choosing and not answering.
        ConflictViewModel vm = Conflicts("a.txt", "b.txt");

        vm.ApplyToAll(ConflictChoice.Skip);

        Assert.Empty(vm.BuildCopyList());
        Assert.Empty(vm.BuildUnansweredList());
        Assert.Equal(0, vm.UndecidedCount);
    }

    [Fact]
    public void Keep_both_gives_every_copy_a_free_name()
    {
        // The answer that loses nothing, and the reason it is on the first dialog.
        using var fx = new Fixture();
        fx.WriteDestination("a.txt", "already here");

        var vm = new ConflictViewModel([
            new PendingDecision(DecisionKind.NameConflict,
                Path.Combine(fx.Source, "a.txt"), Path.Combine(fx.Destination, "a.txt"),
                100, 50, Noon.AddHours(1), Noon)]);

        vm.ApplyToAll(ConflictChoice.KeepBoth);

        var only = Assert.Single(vm.BuildCopyList());
        Assert.Equal(Path.Combine(fx.Destination, "a (2).txt"), only.Destination);
        Assert.True(fx.DestinationExists("a.txt"));      // the original is untouched
    }

    [Fact]
    public void A_bulk_answer_replaces_whatever_was_chosen_before_it()
    {
        // The loop reuses one view model across the first dialog, the detailed list
        // and back — so a bulk answer given afterwards has to win outright.
        ConflictViewModel vm = Conflicts("a.txt", "b.txt");
        vm.Items[0].Choice = ConflictChoice.Skip;

        vm.ApplyToAll(ConflictChoice.Replace);

        Assert.Equal(2, vm.BuildCopyList().Count);
    }
}
