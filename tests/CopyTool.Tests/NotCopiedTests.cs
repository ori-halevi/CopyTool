using CopyTool.Core;
using CopyTool.Host.Ui;
using Xunit;

namespace CopyTool.Tests;

/// <summary>
/// Which skips are still worth a word once the job is over.
///
/// Almost none of them. Every skip is a rule playing out as written, and telling
/// someone what their own rule did is paperwork. The exception is a skip where a
/// file is simply not there afterwards: "skip locked files" is a rule its owner
/// knows, but *which* file was locked, and what held it, is the part no rule could
/// have said — and it leaves no trace at all once the window closes.
/// </summary>
public class NotCopiedTests
{
    private static CopyReport Report(params SkippedItem[] skipped) => new()
    {
        BytesCopied = 0, FilesCopied = 0, Elapsed = TimeSpan.Zero, Strategy = "test",
        Failures = [], Skipped = skipped, Pending = [],
    };

    private static SkippedItem Skip(string name, SkipReason reason,
                                    IReadOnlyList<string>? holders = null) =>
        new($@"C:\src\{name}", $@"D:\dst\{name}", reason, holders);

    [Theory]
    [InlineData(SkipReason.Identical)]
    [InlineData(SkipReason.AlreadyExists)]
    [InlineData(SkipReason.DestinationNewer)]
    [InlineData(SkipReason.DestinationLarger)]
    public void A_skip_the_rule_already_explains_is_not_reported(SkipReason reason)
    {
        // Nothing is missing that the user did not choose to be missing.
        CopyReport report = Report(Skip("a.txt", reason));

        Assert.Empty(report.SkippedAfterError);
        Assert.Empty(Text.DescribeNotCopied([.. report.SkippedAfterError]));
    }

    [Theory]
    [InlineData(SkipReason.Locked)]
    [InlineData(SkipReason.IoError)]
    public void A_skip_that_left_a_file_behind_is_reported(SkipReason reason)
    {
        CopyReport report = Report(Skip("a.txt", reason));

        Assert.Single(report.SkippedAfterError);
        Assert.NotEmpty(Text.DescribeNotCopied([.. report.SkippedAfterError]));
    }

    [Fact]
    public void The_report_names_the_file_and_what_was_holding_it()
    {
        // The whole reason this exists: "the file is in use" without saying by what
        // is the least useful message Windows produces.
        CopyReport report = Report(Skip("Project.docx", SkipReason.Locked, ["Microsoft Word"]));

        string[] lines = Text.DescribeNotCopied([.. report.SkippedAfterError]);

        Assert.Equal("פריט אחד לא הועתק:", lines[0]);
        Assert.Contains("Project.docx", lines[1]);
        Assert.Contains("Microsoft Word", lines[1]);
    }

    [Fact]
    public void A_mixed_job_reports_only_what_went_wrong()
    {
        CopyReport report = Report(
            Skip("same.txt", SkipReason.Identical),
            Skip("newer.txt", SkipReason.DestinationNewer),
            Skip("locked.docx", SkipReason.Locked, ["Word"]));

        string[] lines = Text.DescribeNotCopied([.. report.SkippedAfterError]);

        Assert.Equal(3, report.Skipped.Count);
        Assert.Equal(2, lines.Length);                  // one heading, one file
        Assert.Contains("locked.docx", lines[1]);
        Assert.DoesNotContain(lines, l => l.Contains("same.txt"));
    }

    [Fact]
    public void A_conflict_that_was_answered_skip_is_not_repeated_back()
    {
        // The user was asked and chose. Telling them what they just chose is noise,
        // and it is the same reason an identical file is never mentioned.
        var vm = new ConflictViewModel([Conflict("chosen.txt")]);
        vm.ApplyToAll(ConflictChoice.Skip);

        Assert.Empty(vm.BuildUnansweredList());
    }

    [Fact]
    public void A_conflict_that_was_never_answered_is_reported()
    {
        // Asked, and not answered. The file is not copied either way, but "I chose
        // to skip it" and "I closed the window" are different facts — and only one
        // of them is already known to the person who did it.
        var vm = new ConflictViewModel([Conflict("ignored.txt"), Conflict("chosen.txt")]);

        // By name, not by index: the dialog sorts its list, so a positional
        // assumption here is really an assumption about the sort order.
        vm.Items.Single(i => i.Name == "chosen.txt").Choice = ConflictChoice.Replace;

        List<PendingDecision> unanswered = vm.BuildUnansweredList();

        Assert.Single(unanswered);
        Assert.EndsWith("ignored.txt", unanswered[0].Source);

        string[] lines = Text.DescribeNotCopied([], unanswered);
        Assert.Contains("ignored.txt", lines[1]);
        Assert.Contains("ללא הכרעה", lines[1]);
    }

    [Fact]
    public void Errors_and_unanswered_questions_are_counted_together()
    {
        SkippedItem[] locked = [Skip("held.docx", SkipReason.Locked, ["Word"])];
        var vm = new ConflictViewModel([Conflict("ignored.txt")]);

        string[] lines = Text.DescribeNotCopied(locked, vm.BuildUnansweredList());

        Assert.Equal("שני פריטים לא הועתקו:", lines[0]);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void One_tick_clears_the_files_that_are_already_there()
    {
        // The escape hatch for the case the new default creates: re-copying a folder
        // that is already there produces a list of nothing but identical files, and
        // one tick has to be enough to get past them.
        var vm = new ConflictViewModel([Identical("same1.txt"), Identical("same2.txt"), Conflict("real.txt")]);

        Assert.Equal(2, vm.IdenticalCount);
        Assert.False(vm.SkipIdentical);              // never pre-ticked
        Assert.Equal(3, vm.UndecidedCount);

        vm.SkipIdentical = true;

        Assert.Equal(1, vm.UndecidedCount);          // only the real conflict is left
        Assert.Empty(vm.BuildCopyList());            // and nothing is queued to copy
    }

    [Fact]
    public void Unticking_takes_back_only_what_ticking_did()
    {
        // A file the user went and chose "replace" for is their decision. Unticking
        // the box must not quietly undo it.
        var vm = new ConflictViewModel([Identical("chosen.txt"), Identical("other.txt")]);
        vm.SkipIdentical = true;
        vm.Items.Single(i => i.Name == "chosen.txt").Choice = ConflictChoice.Replace;

        vm.SkipIdentical = false;

        Assert.Equal(ConflictChoice.Replace, vm.Items.Single(i => i.Name == "chosen.txt").Choice);
        Assert.Equal(ConflictChoice.Undecided, vm.Items.Single(i => i.Name == "other.txt").Choice);
    }

    [Fact]
    public void A_list_with_no_identical_files_does_not_offer_the_tick()
    {
        var vm = new ConflictViewModel([Conflict("real.txt")]);

        Assert.False(vm.HasIdentical);
        Assert.Equal(0, vm.IdenticalCount);
    }

    private static PendingDecision Identical(string name)
    {
        DateTime when = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        return new PendingDecision(DecisionKind.Identical, $@"C:\src\{name}", $@"D:\dst\{name}",
                                   99, 99, when, when);
    }

    private static PendingDecision Conflict(string name) =>
        new(DecisionKind.NameConflict, $@"C:\src\{name}", $@"D:\dst\{name}",
            10, 20, DateTime.UtcNow, DateTime.UtcNow.AddHours(-1));

    [Fact]
    public void A_long_list_is_capped_and_says_so()
    {
        SkippedItem[] many = [.. Enumerable.Range(0, 20).Select(i => Skip($"f{i}.txt", SkipReason.IoError))];

        string[] lines = Text.DescribeNotCopied(many, limit: 3);

        Assert.Equal("20 פריטים לא הועתקו:", lines[0]);
        Assert.Equal(5, lines.Length);                  // heading + 3 + "and N more"
        Assert.Contains("ועוד 17", lines[^1]);
    }
}
