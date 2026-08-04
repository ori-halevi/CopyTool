using CopyTool.Core;
using CopyTool.Host.Ui;
using Xunit;

namespace CopyTool.Tests;

/// <summary>
/// Which side of a conflict gets emphasised.
///
/// The dialog sets the winning value in SemiBold so the thing being chosen between
/// is legible without reading two numbers and subtracting them — the same device
/// the built-in Windows copy dialog uses on the newer date. That only helps if it
/// agrees with the sentence printed next to it, so both come from Core's
/// comparison and its two-second tolerance rather than from a second opinion.
/// </summary>
public class ConflictEmphasisTests
{
    private static readonly DateTime Noon = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ConflictItem Item(
        long sourceSize, DateTime sourceModified,
        long destinationSize, DateTime destinationModified,
        DecisionKind kind = DecisionKind.NameConflict) =>
        new(new PendingDecision(
            kind, @"C:\src\a.txt", @"D:\dst\a.txt",
            sourceSize, destinationSize, sourceModified, destinationModified));

    [Fact]
    public void The_newer_side_is_the_one_marked_newer()
    {
        ConflictItem item = Item(100, Noon.AddHours(1), 100, Noon);

        Assert.True(item.SourceIsNewer);
        Assert.False(item.DestinationIsNewer);
    }

    [Fact]
    public void The_larger_side_is_the_one_marked_larger()
    {
        ConflictItem item = Item(500, Noon, 100, Noon);

        Assert.True(item.SourceIsLarger);
        Assert.False(item.DestinationIsLarger);
    }

    [Fact]
    public void Each_axis_is_judged_on_its_own()
    {
        // The interesting conflict: older but bigger. Emphasising a whole side
        // would have to pick one axis and lie about the other.
        ConflictItem item = Item(500, Noon, 100, Noon.AddHours(3));

        Assert.True(item.SourceIsLarger);
        Assert.True(item.DestinationIsNewer);
        Assert.False(item.SourceIsNewer);
        Assert.False(item.DestinationIsLarger);
    }

    [Fact]
    public void Nothing_is_emphasised_when_the_two_agree()
    {
        ConflictItem item = Item(100, Noon, 100, Noon);

        Assert.False(item.SourceIsNewer);
        Assert.False(item.DestinationIsNewer);
        Assert.False(item.SourceIsLarger);
        Assert.False(item.DestinationIsLarger);
    }

    [Fact]
    public void A_sub_tolerance_difference_is_not_newer()
    {
        // FAT stores timestamps to two seconds, so a one-second gap is noise. The
        // emphasis has to use the same tolerance as the verdict, or one file would
        // be called "same date" while its date was in bold.
        ConflictItem item = Item(100, Noon.AddSeconds(1), 100, Noon);

        Assert.False(item.SourceIsNewer);
        Assert.Contains("אותו תאריך", item.Verdict);
    }

    [Fact]
    public void Nothing_is_emphasised_when_there_is_no_file_at_the_destination()
    {
        // A locked file or an I/O error is parked with nothing on the other side.
        // "The source is larger" is meaningless against a file that is not there,
        // and bolding it would invent a comparison.
        ConflictItem item = Item(500, Noon, 0, DateTime.MinValue, DecisionKind.Locked);

        Assert.False(item.SourceIsLarger);
        Assert.False(item.SourceIsNewer);
        Assert.False(item.HasDestinationDate);
        Assert.Equal("", item.DestinationDateText);
    }

    [Fact]
    public void Size_and_date_are_separate_values()
    {
        // They used to be one preformatted string, which cannot be half bold — and
        // which bidi was free to reorder inside the right-to-left dialog.
        ConflictItem item = Item(2048, Noon, 1024, Noon);

        Assert.Equal("2.0 KB", item.SourceSizeText);
        Assert.Equal("1.0 KB", item.DestinationSizeText);
        Assert.True(item.HasSourceDate);
    }
}
