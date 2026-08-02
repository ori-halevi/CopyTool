using CopyTool.Core;
using Xunit;

namespace CopyTool.Tests;

/// <summary>
/// One parking mechanism, not several.
///
/// Anything the job cannot decide alone lands in the same queue with a
/// <see cref="DecisionKind"/> saying who should answer: a person, through the
/// conflict dialog, or the OS, through a UAC prompt. Permission problems used to
/// have their own bag, counter, progress field and report field, which forced the
/// host to reach past the report it had just been handed.
/// </summary>
public class PendingTests
{
    [Fact]
    public async Task A_name_conflict_is_parked_with_its_kind()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "SOURCE");
        fx.WriteDestination("a.txt", "DESTINATION", TimeSpan.FromHours(1));

        CopyReport report = await fx.RunFilesAsync(CopyOperation.Copy, "a.txt");

        PendingDecision item = Assert.Single(report.Pending);
        Assert.Equal(DecisionKind.NameConflict, item.Kind);
        Assert.Equal(Path.Combine(fx.Source, "a.txt"), item.Source);
        Assert.Equal(Path.Combine(fx.Destination, "a.txt"), item.Destination);
    }

    [Fact]
    public async Task An_identical_file_under_ask_is_parked_as_identical()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "same");
        fx.WriteDestination("a.txt", "same");

        CopyReport report = await fx.RunFilesAsync(
            CopyOperation.Copy, new JobPolicies { Identical = IdenticalPolicy.Ask }, "a.txt");

        PendingDecision item = Assert.Single(report.Pending);
        Assert.Equal(DecisionKind.Identical, item.Kind);
    }

    [Fact]
    public async Task Parked_items_carry_both_sides_so_the_dialog_need_not_re_read_them()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "12345");                                   // 5 bytes
        fx.WriteDestination("a.txt", "0123456789", TimeSpan.FromHours(3));  // 10 bytes, newer

        CopyReport report = await fx.RunFilesAsync(CopyOperation.Copy, "a.txt");

        PendingDecision item = Assert.Single(report.Pending);
        Assert.Equal(5, item.SourceSize);
        Assert.Equal(10, item.DestinationSize);
        Assert.True(ConflictResolver.IsNewer(item.DestinationModified, item.SourceModified));
    }

    [Fact]
    public async Task Answerable_and_elevation_items_are_split_by_audience()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "SOURCE");
        fx.WriteDestination("a.txt", "DESTINATION", TimeSpan.FromHours(1));

        CopyReport report = await fx.RunFilesAsync(CopyOperation.Copy, "a.txt");

        // No permission problem here, so everything parked is for a person.
        Assert.Single(report.NeedsAnswer);
        Assert.Empty(report.NeedsElevation);
        Assert.Equal(report.Pending.Count, report.NeedsAnswer.Count() + report.NeedsElevation.Count());
    }

    [Fact]
    public async Task Taking_a_pending_item_leaves_the_other_kinds_alone()
    {
        // The drain rotates past kinds it does not want rather than dropping them.
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "SOURCE");
        fx.WriteDestination("a.txt", "DESTINATION", TimeSpan.FromHours(1));

        ScanResult scan = Scanner.Scan([Path.Combine(fx.Source, "a.txt")]);
        var engine = new CopyEngine();
        await engine.RunAsync(scan, fx.Destination, CopyOperation.Copy);

        Assert.Equal(1, engine.CountPending(DecisionKind.NameConflict));

        // Nothing of this kind is parked, and the attempt must not consume the
        // conflict that is.
        Assert.False(engine.TryTakePending(DecisionKind.NeedsElevation, out _));
        Assert.Equal(1, engine.CountPending(DecisionKind.NameConflict));

        Assert.True(engine.TryTakePending(DecisionKind.NameConflict, out PendingDecision taken));
        Assert.Equal(DecisionKind.NameConflict, taken.Kind);
        Assert.Equal(0, engine.CountPending(DecisionKind.NameConflict));
    }
}
