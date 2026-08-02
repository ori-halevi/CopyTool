using CopyTool.Core;
using Xunit;

namespace CopyTool.Tests;

/// <summary>
/// A move may only delete a source once its contents are at the destination.
///
/// This is the file that matters most. The engine shipped for weeks with a move
/// that deleted the sources of files it had never copied — anything parked for a
/// conflict decision, waiting on elevation, or skipped because the destination
/// was newer. It survived roughly thirty hand-written checks because every one of
/// them tested copy-with-conflict or move-without-conflict, and never the
/// intersection. These are that intersection.
/// </summary>
public class MoveSafetyTests
{
    [Fact]
    public async Task Move_with_no_conflict_deletes_the_source()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "content");

        CopyReport report = await fx.RunFilesAsync(CopyOperation.Move, "a.txt");

        Assert.Equal("content", fx.ReadDestination("a.txt"));
        Assert.False(fx.SourceExists("a.txt"));
        Assert.Equal(1, report.FilesCopied);
    }

    [Theory]
    [InlineData(ConflictPolicy.Ask)]
    [InlineData(ConflictPolicy.Skip)]
    public async Task Move_keeps_the_source_when_the_file_was_never_copied(ConflictPolicy policy)
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "SOURCE");
        fx.WriteDestination("a.txt", "DESTINATION - different", TimeSpan.FromHours(1));

        await fx.RunFilesAsync(CopyOperation.Move, new JobPolicies { Conflict = policy }, "a.txt");

        Assert.True(fx.SourceExists("a.txt"));                       // the only copy of SOURCE
        Assert.Equal("DESTINATION - different", fx.ReadDestination("a.txt"));
    }

    [Fact]
    public async Task Move_keeps_the_source_when_the_destination_is_newer()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "SOURCE", age: TimeSpan.Zero);
        fx.WriteDestination("a.txt", "NEWER AT DESTINATION", age: TimeSpan.FromHours(2));

        await fx.RunFilesAsync(
            CopyOperation.Move, new JobPolicies { Conflict = ConflictPolicy.KeepNewer }, "a.txt");

        Assert.True(fx.SourceExists("a.txt"));
        Assert.Equal("NEWER AT DESTINATION", fx.ReadDestination("a.txt"));
    }

    [Fact]
    public async Task Move_keeps_the_source_when_the_destination_is_larger()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "short");
        fx.WriteDestination("a.txt", "a considerably longer destination file");

        await fx.RunFilesAsync(
            CopyOperation.Move, new JobPolicies { Conflict = ConflictPolicy.KeepLarger }, "a.txt");

        Assert.True(fx.SourceExists("a.txt"));
    }

    [Fact]
    public async Task Move_deletes_the_source_when_the_destination_is_already_identical()
    {
        // The one skip a move may still delete for: the same bytes are provably
        // there already, so removing the source loses nothing.
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "same");
        fx.WriteDestination("a.txt", "same");

        CopyReport report = await fx.RunFilesAsync(CopyOperation.Move, "a.txt");

        Assert.False(fx.SourceExists("a.txt"));
        Assert.Equal("same", fx.ReadDestination("a.txt"));
        Assert.Single(report.Skipped);
        Assert.Equal(SkipReason.Identical, report.Skipped[0].Reason);
    }

    [Fact]
    public async Task Move_of_a_mixed_batch_only_deletes_what_arrived()
    {
        using var fx = new Fixture();
        fx.WriteSource("clean.txt", "moves fine");
        fx.WriteSource("conflict.txt", "SOURCE");
        fx.WriteDestination("conflict.txt", "DESTINATION", TimeSpan.FromHours(1));

        await fx.RunFilesAsync(CopyOperation.Move, "clean.txt", "conflict.txt");

        Assert.False(fx.SourceExists("clean.txt"));                  // arrived, source gone
        Assert.True(fx.SourceExists("conflict.txt"));                // parked, source kept
        Assert.Equal("DESTINATION", fx.ReadDestination("conflict.txt"));
    }

    [Fact]
    public async Task Move_that_overwrites_deletes_the_source()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "NEW");
        fx.WriteDestination("a.txt", "OLD", TimeSpan.FromHours(1));

        await fx.RunFilesAsync(
            CopyOperation.Move, new JobPolicies { Conflict = ConflictPolicy.Overwrite }, "a.txt");

        Assert.Equal("NEW", fx.ReadDestination("a.txt"));
        Assert.False(fx.SourceExists("a.txt"));
    }

    [Fact]
    public async Task Move_with_keep_both_deletes_the_source_and_keeps_the_original()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "NEW");
        fx.WriteDestination("a.txt", "ORIGINAL", TimeSpan.FromHours(1));

        await fx.RunFilesAsync(
            CopyOperation.Move, new JobPolicies { Conflict = ConflictPolicy.KeepBoth }, "a.txt");

        Assert.Equal("ORIGINAL", fx.ReadDestination("a.txt"));
        Assert.Equal("NEW", fx.ReadDestination("a (2).txt"));
        Assert.False(fx.SourceExists("a.txt"));
    }
}
