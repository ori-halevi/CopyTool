using CopyTool.Core;
using Xunit;

namespace CopyTool.Tests;

/// <summary>
/// Verification reads the copy back and compares it to the source.
///
/// The case that matters is the mismatch: a file of the right name and the right
/// length holding the wrong bytes is the one failure every other check misses,
/// including this tool's own "identical" fast path on the next run.
/// </summary>
public class VerifyTests
{
    [Fact]
    public async Task Identical_files_compare_equal()
    {
        using var fx = new Fixture();
        string a = fx.WriteSource("a.bin", new string('x', 100_000));
        string b = fx.WriteDestination("b.bin", new string('x', 100_000));

        Assert.True(await Verifier.AreIdenticalAsync(a, b));
    }

    [Fact]
    public async Task A_single_differing_byte_is_caught()
    {
        using var fx = new Fixture();
        string a = fx.WriteSource("a.bin", new string('x', 100_000));
        string b = fx.WriteDestination("b.bin", new string('x', 100_000));

        // Same length, one byte different, right at the end — the shape of real
        // corruption, and invisible to any size check.
        byte[] bytes = File.ReadAllBytes(b);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(b, bytes);

        Assert.False(await Verifier.AreIdenticalAsync(a, b));
    }

    [Fact]
    public async Task Differing_lengths_are_caught_without_reading_the_files()
    {
        using var fx = new Fixture();
        string a = fx.WriteSource("a.bin", "12345");
        string b = fx.WriteDestination("b.bin", "1234");

        Assert.False(await Verifier.AreIdenticalAsync(a, b));
    }

    [Fact]
    public async Task A_missing_file_is_not_a_match()
    {
        using var fx = new Fixture();
        string a = fx.WriteSource("a.bin", "content");

        Assert.False(await Verifier.AreIdenticalAsync(a, Path.Combine(fx.Destination, "absent.bin")));
    }

    [Fact]
    public async Task Empty_files_match()
    {
        using var fx = new Fixture();
        string a = fx.WriteSource("a.bin", "");
        string b = fx.WriteDestination("b.bin", "");

        Assert.True(await Verifier.AreIdenticalAsync(a, b));
    }

    [Fact]
    public async Task A_verified_small_copy_is_reported_as_verified()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "content");

        CopyReport report = await fx.RunFilesAsync(
            CopyOperation.Copy, new JobPolicies { Verify = VerifyPolicy.EveryFile }, "a.txt");

        Assert.Equal(1, report.FilesCopied);
        Assert.Equal(1, report.Verified);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public async Task A_verified_large_copy_is_reported_as_verified()
    {
        // Above the threshold, so this goes through the unbuffered pipeline.
        using var fx = new Fixture();
        fx.WriteLargeSource("large.bin", megabytes: 20);

        CopyReport report = await fx.RunFilesAsync(
            CopyOperation.Copy, new JobPolicies { Verify = VerifyPolicy.EveryFile }, "large.bin");

        Assert.Equal(1, report.FilesCopied);
        Assert.Equal(1, report.Verified);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public async Task Verification_off_reports_nothing_verified()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "content");

        CopyReport report = await fx.RunFilesAsync(CopyOperation.Copy, "a.txt");

        Assert.Equal(1, report.FilesCopied);
        Assert.Equal(0, report.Verified);
    }

    [Fact]
    public async Task A_same_volume_move_has_nothing_to_verify()
    {
        // A move within one volume is a rename: no bytes travel, and the file at
        // the destination *is* the file that was at the source, same extents and
        // all. Reading it back to compare it with itself would be theatre, so the
        // count stays at zero — and that is the honest answer, not a miss.
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "content");

        CopyReport report = await fx.RunFilesAsync(
            CopyOperation.Move, new JobPolicies { Verify = VerifyPolicy.EveryFile }, "a.txt");

        Assert.Equal(1, report.FilesCopied);
        Assert.Equal(0, report.Verified);
        Assert.False(fx.SourceExists("a.txt"));
        Assert.Equal("content", fx.ReadDestination("a.txt"));
    }

    [Fact]
    public async Task Everything_counted_as_copied_was_also_verified()
    {
        // The pairing that matters: with verification on, a file only counts as
        // copied once it has been read back. If those two could drift apart, a
        // move would delete the source of a file that failed its check.
        using var fx = new Fixture();
        fx.WriteSource("small.txt", "content");
        fx.WriteLargeSource("large.bin", megabytes: 18);

        CopyReport report = await fx.RunFilesAsync(
            CopyOperation.Copy, new JobPolicies { Verify = VerifyPolicy.EveryFile },
            "small.txt", "large.bin");

        Assert.Equal(2, report.FilesCopied);
        Assert.Equal(report.FilesCopied, report.Verified);
        Assert.Empty(report.Failures);
    }
}
