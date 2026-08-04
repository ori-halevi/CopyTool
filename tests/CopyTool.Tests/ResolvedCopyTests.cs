using CopyTool.Core;
using Xunit;

namespace CopyTool.Tests;

/// <summary>
/// <see cref="CopyEngine.CopyExplicitAsync"/> — the path taken once a question has
/// been answered, and the path the elevated worker runs.
///
/// Everything it touches is a destination the user has *chosen to replace*, which
/// means they still have it and are trading it for the source. It used to copy
/// straight over that file and delete it on any failure, so a copy that never
/// started — an unreadable source, a lock taken a second earlier — left the user
/// with neither the old file nor the new one.
/// </summary>
public class ResolvedCopyTests
{
    private static string[] Leftovers(string directory) =>
        Directory.GetFiles(directory, "*.copytool-part", SearchOption.AllDirectories);

    [Fact]
    public async Task A_failed_replace_leaves_the_existing_destination_alone()
    {
        using var fx = new Fixture();
        fx.WriteDestination("a.txt", "THE ONLY COPY");

        // A source that is not there: the copy cannot start, so the destination was
        // never touched and must survive.
        string missing = Path.Combine(fx.Source, "never-existed.txt");
        string destination = Path.Combine(fx.Destination, "a.txt");

        var engine = new CopyEngine();
        CopyReport report = await engine.CopyExplicitAsync([(missing, destination, 13)]);

        Assert.Single(report.Failures);
        Assert.Equal(0, report.FilesCopied);
        Assert.Equal("THE ONLY COPY", fx.ReadDestination("a.txt"));
        Assert.Empty(Leftovers(fx.Destination));
    }

    [Fact]
    public async Task A_successful_replace_swaps_the_file_and_names_its_source()
    {
        using var fx = new Fixture();
        string source = fx.WriteSource("a.txt", "NEW");
        fx.WriteDestination("a.txt", "OLD");

        var arrived = new List<string>();
        var engine = new CopyEngine();

        CopyReport report = await engine.CopyExplicitAsync(
            [(source, Path.Combine(fx.Destination, "a.txt"), 3)],
            onFileDone: (s, _, _) => arrived.Add(s));

        Assert.Empty(report.Failures);
        Assert.Equal("NEW", fx.ReadDestination("a.txt"));

        // The source is reported so a move can delete it. Nothing else knows these
        // files arrived — the engine's own pass finished before this ran.
        Assert.Equal([source], arrived);
        Assert.Empty(Leftovers(fx.Destination));
    }

    [Fact]
    public async Task A_replaced_large_file_arrives_byte_for_byte()
    {
        // Above the threshold, so this goes down the unbuffered pipeline and then
        // through the rename — the two halves have to agree about the staging name.
        using var fx = new Fixture();
        string source = fx.WriteLargeSource("big.bin", megabytes: 18);
        fx.WriteDestination("big.bin", "a small stale file");

        string destination = Path.Combine(fx.Destination, "big.bin");
        var engine = new CopyEngine();

        CopyReport report = await engine.CopyExplicitAsync(
            [(source, destination, new FileInfo(source).Length)]);

        Assert.Empty(report.Failures);
        Assert.Equal(Fixture.Sha256(source), Fixture.Sha256(destination));
        Assert.Empty(Leftovers(fx.Destination));
    }

    [Fact]
    public async Task A_read_only_destination_is_still_replaced()
    {
        // Renaming over a read-only file fails, and a read-only flag is not a
        // reason to refuse a replacement the user explicitly asked for.
        using var fx = new Fixture();
        string source = fx.WriteSource("a.txt", "NEW");
        string destination = fx.WriteDestination("a.txt", "OLD");
        File.SetAttributes(destination, FileAttributes.ReadOnly);

        var engine = new CopyEngine();
        CopyReport report = await engine.CopyExplicitAsync([(source, destination, 3)]);

        Assert.Empty(report.Failures);
        Assert.Equal("NEW", fx.ReadDestination("a.txt"));
    }

    [Fact]
    public async Task A_missing_destination_directory_is_created_and_reported()
    {
        using var fx = new Fixture();
        string source = fx.WriteSource("a.txt", "NEW");
        string destination = Path.Combine(fx.Destination, "one", "two", "a.txt");

        var made = new List<string>();
        var engine = new CopyEngine();

        CopyReport report = await engine.CopyExplicitAsync(
            [(source, destination, 3)], onDirectoryCreated: made.Add);

        Assert.Empty(report.Failures);
        Assert.True(File.Exists(destination));

        // Every level, parents first. The elevated worker hands ownership back with
        // this, and a level left owned by Administrators is a folder the user
        // afterwards cannot write to.
        Assert.Equal(
            [Path.Combine(fx.Destination, "one"), Path.Combine(fx.Destination, "one", "two")],
            made);
    }
}
