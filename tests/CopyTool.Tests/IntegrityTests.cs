using CopyTool.Core;
using Xunit;

namespace CopyTool.Tests;

/// <summary>
/// The bytes that arrive must be the bytes that left.
///
/// The large-file path is an unbuffered pipeline with several I/Os in flight at
/// explicit offsets, a sector-padded tail and a truncate at the end. Every one of
/// those is a chance to produce a file of exactly the right length full of the
/// wrong content — which no size check would catch.
/// </summary>
public class IntegrityTests
{
    [Fact]
    public async Task Small_file_arrives_byte_for_byte()
    {
        using var fx = new Fixture();
        string source = fx.WriteSource("small.bin", new string('x', 4096));

        await fx.RunFilesAsync(CopyOperation.Copy, "small.bin");

        Assert.Equal(Fixture.Sha256(source), Fixture.Sha256(Path.Combine(fx.Destination, "small.bin")));
    }

    [Fact]
    public async Task Large_file_arrives_byte_for_byte()
    {
        // Above the 16 MB threshold, so this exercises the unbuffered pipeline
        // rather than CopyFileEx.
        using var fx = new Fixture();
        string source = fx.WriteLargeSource("large.bin", megabytes: 40);

        CopyReport report = await fx.RunFilesAsync(CopyOperation.Copy, "large.bin");

        string destination = Path.Combine(fx.Destination, "large.bin");
        Assert.Empty(report.Failures);
        Assert.Equal(new FileInfo(source).Length, new FileInfo(destination).Length);
        Assert.Equal(Fixture.Sha256(source), Fixture.Sha256(destination));
    }

    [Fact]
    public async Task A_size_that_is_not_a_whole_number_of_sectors_is_truncated_correctly()
    {
        // The tail chunk is padded up to a sector boundary and the file truncated
        // afterwards. An off-by-one there leaves trailing padding in the file.
        using var fx = new Fixture();
        string source = fx.WriteLargeSource("odd.bin", megabytes: 20);

        using (var stream = new FileStream(source, FileMode.Append))
        {
            stream.Write(new byte[1234]);          // deliberately not sector-aligned
        }

        await fx.RunFilesAsync(CopyOperation.Copy, "odd.bin");

        string destination = Path.Combine(fx.Destination, "odd.bin");
        Assert.Equal(new FileInfo(source).Length, new FileInfo(destination).Length);
        Assert.Equal(Fixture.Sha256(source), Fixture.Sha256(destination));
    }

    [Fact]
    public async Task Empty_file_is_copied_as_an_empty_file()
    {
        using var fx = new Fixture();
        fx.WriteSource("empty.bin", "");

        await fx.RunFilesAsync(CopyOperation.Copy, "empty.bin");

        Assert.True(fx.DestinationExists("empty.bin"));
        Assert.Equal(0, new FileInfo(Path.Combine(fx.Destination, "empty.bin")).Length);
    }

    [Fact]
    public async Task A_directory_tree_is_reproduced_intact()
    {
        using var fx = new Fixture();
        fx.WriteSource(Path.Combine("deep", "one", "a.txt"), "A");
        fx.WriteSource(Path.Combine("deep", "two", "b.txt"), "B");
        fx.WriteSource("top.txt", "T");

        CopyReport report = await fx.RunAsync(CopyOperation.Copy);

        // Scanning a folder makes its own name part of the relative path.
        Assert.Equal("A", File.ReadAllText(Path.Combine(fx.Destination, "src", "deep", "one", "a.txt")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(fx.Destination, "src", "deep", "two", "b.txt")));
        Assert.Equal("T", File.ReadAllText(Path.Combine(fx.Destination, "src", "top.txt")));
        Assert.Equal(3, report.FilesCopied);
    }

    [Fact]
    public async Task Timestamps_are_preserved()
    {
        using var fx = new Fixture();
        string source = fx.WriteSource("stamped.txt", "content", TimeSpan.FromHours(-5));

        await fx.RunFilesAsync(CopyOperation.Copy, "stamped.txt");

        DateTime from = File.GetLastWriteTimeUtc(source);
        DateTime to = File.GetLastWriteTimeUtc(Path.Combine(fx.Destination, "stamped.txt"));
        Assert.True((from - to).Duration() <= TimeSpan.FromSeconds(2), $"{from:o} vs {to:o}");
    }
}
