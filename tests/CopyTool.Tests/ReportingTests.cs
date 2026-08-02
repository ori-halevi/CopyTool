using CopyTool.Core;
using Xunit;

namespace CopyTool.Tests;

/// <summary>
/// What the report claims must match what is on disk.
///
/// An earlier version counted skipped and deferred files as copied bytes, which
/// produced summaries like "0 files, 1 GB at 87 GB/s". A copy tool that
/// misreports is worse than one that is merely slow: the numbers are how anyone
/// decides whether it worked.
/// </summary>
public class ReportingTests
{
    [Fact]
    public async Task Bytes_copied_counts_only_what_was_written()
    {
        using var fx = new Fixture();
        fx.WriteSource("copied.txt", "12345");                      // 5 bytes, will be written
        fx.WriteSource("parked.txt", "SOURCE");
        fx.WriteDestination("parked.txt", "DESTINATION", TimeSpan.FromHours(1));

        CopyReport report = await fx.RunFilesAsync(CopyOperation.Copy, "copied.txt", "parked.txt");

        Assert.Equal(1, report.FilesCopied);
        Assert.Equal(5, report.BytesCopied);
        Assert.Single(report.Pending);
    }

    [Fact]
    public async Task Nothing_copied_reports_zero_bytes()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "SOURCE");
        fx.WriteDestination("a.txt", "DESTINATION", TimeSpan.FromHours(1));

        CopyReport report = await fx.RunFilesAsync(
            CopyOperation.Copy, new JobPolicies { Conflict = ConflictPolicy.Skip }, "a.txt");

        Assert.Equal(0, report.FilesCopied);
        Assert.Equal(0, report.BytesCopied);
    }

    [Fact]
    public async Task Progress_never_exceeds_the_total()
    {
        // Skipped and parked files still have to advance the bar or it stalls
        // short of 100% — but counting them twice pushes it past the end.
        using var fx = new Fixture();
        for (int i = 0; i < 5; i++) fx.WriteSource($"f{i}.txt", new string('x', 100));
        fx.WriteDestination("f2.txt", "different", TimeSpan.FromHours(1));

        ScanResult scan = Scanner.Scan([fx.Source]);
        long worst = 0;

        var engine = new CopyEngine
        {
            Progress = new Progress<CopyProgress>(p =>
            {
                if (p.BytesDone > worst) worst = p.BytesDone;
            }),
        };
        CopyReport report = await engine.RunAsync(scan, fx.Destination, CopyOperation.Copy);

        Assert.True(worst <= scan.TotalBytes,
            $"progress reached {worst} of a possible {scan.TotalBytes}");

        // And every byte is accounted for exactly once: written, or deliberately not.
        Assert.Equal(scan.TotalBytes, report.BytesCopied + Untouched(scan, report));
    }

    private static long Untouched(ScanResult scan, CopyReport report)
    {
        // Everything the job deliberately did not write. Pending covers every kind
        // now, permission problems included.
        var names = report.Skipped.Select(s => s.Source)
            .Concat(report.Pending.Select(p => p.Source))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return scan.Files.Where(f => names.Contains(f.SourcePath)).Sum(f => f.Size);
    }

    [Fact]
    public void Scan_reports_the_size_histogram()
    {
        using var fx = new Fixture();
        fx.WriteSource("tiny.txt", new string('x', 100));                 // < 64 KB
        fx.WriteSource("small.txt", new string('x', 200_000));            // 64 KB - 1 MB
        fx.WriteLargeSource("medium.bin", megabytes: 2);                  // 1 - 64 MB

        ScanResult scan = Scanner.Scan([fx.Source]);
        var (tiny, small, medium, large) = scan.Histogram;

        Assert.Equal(1, tiny);
        Assert.Equal(1, small);
        Assert.Equal(1, medium);
        Assert.Equal(0, large);
        Assert.Equal(3, scan.FileCount);
    }

    [Fact]
    public async Task Skipped_items_carry_a_reason_code_not_a_sentence()
    {
        // Core is presentation-agnostic: it reports what happened, and the UI
        // decides how to say it.
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "SOURCE");
        fx.WriteDestination("a.txt", "DESTINATION", TimeSpan.FromHours(1));

        CopyReport report = await fx.RunFilesAsync(
            CopyOperation.Copy, new JobPolicies { Conflict = ConflictPolicy.Skip }, "a.txt");

        SkippedItem skipped = Assert.Single(report.Skipped);
        Assert.Equal(SkipReason.AlreadyExists, skipped.Reason);
        Assert.Null(skipped.SystemMessage);
    }
}
