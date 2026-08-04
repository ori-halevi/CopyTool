using CopyTool.Core;
using Xunit;

namespace CopyTool.Tests;

/// <summary>
/// Every conflict policy against every shape of destination.
///
/// The matrix exists because the interesting failures live in the combinations,
/// not in any single policy: identical files are resolved by their own policy and
/// must stay skipped even under Overwrite, and "ask" must park rather than stall.
/// </summary>
public class ConflictPolicyTests
{
    [Fact]
    public async Task No_existing_file_means_no_conflict()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "content");

        CopyReport report = await fx.RunFilesAsync(CopyOperation.Copy, "a.txt");

        Assert.Equal("content", fx.ReadDestination("a.txt"));
        Assert.Empty(report.Skipped);
        Assert.Empty(report.Pending);
    }

    [Fact]
    public async Task Ask_parks_the_item_and_does_not_stall_the_job()
    {
        using var fx = new Fixture();
        fx.WriteSource("conflict.txt", "SOURCE");
        fx.WriteSource("clean.txt", "no conflict here");
        fx.WriteDestination("conflict.txt", "DESTINATION", TimeSpan.FromHours(1));

        CopyReport report = await fx.RunFilesAsync(CopyOperation.Copy, "conflict.txt", "clean.txt");

        // The unaffected file went through; the question waits for later.
        Assert.Equal("no conflict here", fx.ReadDestination("clean.txt"));
        Assert.Equal("DESTINATION", fx.ReadDestination("conflict.txt"));
        Assert.Single(report.Pending);
        Assert.Equal(DecisionKind.NameConflict, report.Pending[0].Kind);
    }

    [Fact]
    public async Task Skip_leaves_the_destination_alone()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "SOURCE");
        fx.WriteDestination("a.txt", "DESTINATION", TimeSpan.FromHours(1));

        CopyReport report = await fx.RunFilesAsync(
            CopyOperation.Copy, new JobPolicies { Conflict = ConflictPolicy.Skip }, "a.txt");

        Assert.Equal("DESTINATION", fx.ReadDestination("a.txt"));
        Assert.Single(report.Skipped);
        Assert.Equal(SkipReason.AlreadyExists, report.Skipped[0].Reason);
    }

    [Fact]
    public async Task Overwrite_replaces_the_destination()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "SOURCE");
        fx.WriteDestination("a.txt", "DESTINATION", TimeSpan.FromHours(1));

        await fx.RunFilesAsync(
            CopyOperation.Copy, new JobPolicies { Conflict = ConflictPolicy.Overwrite }, "a.txt");

        Assert.Equal("SOURCE", fx.ReadDestination("a.txt"));
    }

    [Fact]
    public async Task Keep_both_writes_a_free_name_and_leaves_the_original()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "SOURCE");
        fx.WriteDestination("a.txt", "DESTINATION", TimeSpan.FromHours(1));

        await fx.RunFilesAsync(
            CopyOperation.Copy, new JobPolicies { Conflict = ConflictPolicy.KeepBoth }, "a.txt");

        Assert.Equal("DESTINATION", fx.ReadDestination("a.txt"));
        Assert.Equal("SOURCE", fx.ReadDestination("a (2).txt"));
    }

    [Theory]
    [InlineData(2, "SOURCE")]        // source is newer -> it wins
    [InlineData(-2, "DESTINATION")]  // destination is newer -> it stays
    public async Task Keep_newer_picks_by_timestamp(int sourceAgeHours, string expected)
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "SOURCE", TimeSpan.FromHours(sourceAgeHours));
        fx.WriteDestination("a.txt", "DESTINATION", TimeSpan.Zero);

        await fx.RunFilesAsync(
            CopyOperation.Copy, new JobPolicies { Conflict = ConflictPolicy.KeepNewer }, "a.txt");

        Assert.Equal(expected, fx.ReadDestination("a.txt"));
    }

    [Theory]
    [InlineData(50, 10)]     // source bigger -> source wins
    [InlineData(10, 50)]     // destination bigger -> destination stays
    public async Task Keep_larger_picks_by_size(int sourceLength, int destinationLength)
    {
        // Lengths rather than prose: the first version of this test asserted that
        // "a longer source file" beat "the destination is longer", which is 20
        // characters against 25. The test was wrong and the engine was right.
        string source = new('s', sourceLength);
        string destination = new('d', destinationLength);

        using var fx = new Fixture();
        fx.WriteSource("a.txt", source);
        fx.WriteDestination("a.txt", destination, TimeSpan.FromHours(1));

        await fx.RunFilesAsync(
            CopyOperation.Copy, new JobPolicies { Conflict = ConflictPolicy.KeepLarger }, "a.txt");

        Assert.Equal(sourceLength > destinationLength ? source : destination,
                     fx.ReadDestination("a.txt"));
    }

    [Theory]
    [InlineData(ConflictPolicy.Ask)]
    [InlineData(ConflictPolicy.Skip)]
    [InlineData(ConflictPolicy.Overwrite)]
    [InlineData(ConflictPolicy.KeepBoth)]
    [InlineData(ConflictPolicy.KeepNewer)]
    [InlineData(ConflictPolicy.KeepLarger)]
    public async Task Identical_files_obey_their_own_policy_not_the_conflict_one(ConflictPolicy policy)
    {
        // Sameness is decided by its own policy, before the conflict rules get a
        // say. Told to skip identical files, the job does so whatever the conflict
        // chip says — including "keep both", which would otherwise write a needless
        // second copy of a file that is already there byte for byte.
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "identical");
        fx.WriteDestination("a.txt", "identical");

        CopyReport report = await fx.RunFilesAsync(
            CopyOperation.Copy,
            new JobPolicies { Conflict = policy, Identical = IdenticalPolicy.SkipAndReport },
            "a.txt");

        Assert.Single(report.Skipped);
        Assert.Equal(SkipReason.Identical, report.Skipped[0].Reason);
        Assert.Equal(["a.txt"], fx.DestinationNames());     // no "a (2).txt" either
    }

    [Theory]
    [InlineData(ConflictPolicy.Ask)]
    [InlineData(ConflictPolicy.Skip)]
    [InlineData(ConflictPolicy.Overwrite)]
    [InlineData(ConflictPolicy.KeepBoth)]
    [InlineData(ConflictPolicy.KeepNewer)]
    [InlineData(ConflictPolicy.KeepLarger)]
    public async Task By_default_an_identical_file_is_asked_about_like_any_other(ConflictPolicy policy)
    {
        // The default is to ask. A file being byte-identical is an answer to
        // "something is already there" — not a reason to stop asking the question.
        // It used to skip silently, which is how a job could report "4 of 10 files"
        // with nothing anywhere saying what happened to the other six.
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "identical");
        fx.WriteDestination("a.txt", "identical");

        CopyReport report = await fx.RunFilesAsync(
            CopyOperation.Copy, new JobPolicies { Conflict = policy }, "a.txt");

        PendingDecision parked = Assert.Single(report.Pending);
        Assert.Equal(DecisionKind.Identical, parked.Kind);
        Assert.Empty(report.Skipped);

        // Parked, not copied: the destination is untouched until someone answers.
        Assert.Equal(["a.txt"], fx.DestinationNames());
    }

    [Fact]
    public async Task Identical_can_be_forced_to_copy_anyway()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "identical");
        fx.WriteDestination("a.txt", "identical");

        CopyReport report = await fx.RunFilesAsync(
            CopyOperation.Copy,
            new JobPolicies { Identical = IdenticalPolicy.CopyAnyway, Conflict = ConflictPolicy.Overwrite },
            "a.txt");

        Assert.Empty(report.Skipped);
        Assert.Equal(1, report.FilesCopied);
    }
}
