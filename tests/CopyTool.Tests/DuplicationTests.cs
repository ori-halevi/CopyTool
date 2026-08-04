using CopyTool.Core;
using Xunit;

namespace CopyTool.Tests;

/// <summary>
/// Copying something into the folder it already lives in.
///
/// The distinction being drawn is the whole feature: a name clash in *another*
/// folder is two genuinely different files and the person may not know the second
/// one is there, so it parks and asks. A name clash with yourself is one file and
/// a deliberate gesture, so it just happens — under the same name Explorer would
/// have given it, because people already know what that gesture produces.
/// </summary>
public class DuplicationTests
{
    /// <summary>Runs the scan through the same rename step the host applies.</summary>
    private static ScanResult Plan(Fixture fx, params string[] sources)
    {
        ScanResult scan = Scanner.Scan(sources);
        return Duplication.Rename(sources, scan, fx.Source);
    }

    [Fact]
    public void A_file_dropped_on_its_own_folder_becomes_a_Copy()
    {
        using var fx = new Fixture();
        string source = fx.WriteSource("New Text Document.txt", "content");

        ScanResult scan = Plan(fx, source);

        Assert.Equal("New Text Document - Copy.txt", Assert.Single(scan.Files).RelativePath);
    }

    [Fact]
    public void The_second_duplicate_is_numbered()
    {
        using var fx = new Fixture();
        string source = fx.WriteSource("a.txt", "content");
        fx.WriteSource("a - Copy.txt", "the first duplicate");

        ScanResult scan = Plan(fx, source);

        Assert.Equal("a - Copy (2).txt", Assert.Single(scan.Files).RelativePath);
    }

    [Fact]
    public void A_folder_keeps_its_whole_name_and_its_contents()
    {
        // "My.Photos" is a folder, not a file with a ".Photos" extension. Splitting
        // it would produce "My - Copy.Photos".
        using var fx = new Fixture();
        fx.WriteSource(Path.Combine("My.Photos", "one.jpg"), "1");
        fx.WriteSource(Path.Combine("My.Photos", "inner", "two.jpg"), "2");
        string source = Path.Combine(fx.Source, "My.Photos");

        ScanResult scan = Plan(fx, source);

        Assert.Contains(Path.Combine("My.Photos - Copy", "one.jpg"),
                        scan.Files.Select(f => f.RelativePath));
        Assert.Contains(Path.Combine("My.Photos - Copy", "inner", "two.jpg"),
                        scan.Files.Select(f => f.RelativePath));
        Assert.Contains("My.Photos - Copy", scan.Directories);
        Assert.Contains(Path.Combine("My.Photos - Copy", "inner"), scan.Directories);
    }

    [Fact]
    public void A_compound_extension_splits_where_Explorer_splits_it()
    {
        using var fx = new Fixture();
        string source = fx.WriteSource("archive.tar.gz", "content");

        ScanResult scan = Plan(fx, source);

        Assert.Equal("archive.tar - Copy.gz", Assert.Single(scan.Files).RelativePath);
    }

    [Fact]
    public void Every_item_of_a_multiple_selection_is_renamed()
    {
        using var fx = new Fixture();
        string a = fx.WriteSource("a.txt", "A");
        string b = fx.WriteSource("b.txt", "B");

        ScanResult scan = Plan(fx, a, b);

        Assert.Equal(["a - Copy.txt", "b - Copy.txt"],
                     scan.Files.Select(f => f.RelativePath).Order());
    }

    [Fact]
    public void A_different_destination_is_left_alone()
    {
        // The case that must keep asking: a same-named file in another folder is a
        // different file, and silently renaming would hide that from the user.
        using var fx = new Fixture();
        string source = fx.WriteSource("a.txt", "SOURCE");
        fx.WriteDestination("a.txt", "A DIFFERENT FILE", TimeSpan.FromHours(1));

        ScanResult scan = Scanner.Scan([source]);
        ScanResult planned = Duplication.Rename([source], scan, fx.Destination);

        Assert.Equal("a.txt", Assert.Single(planned.Files).RelativePath);
        Assert.Same(scan, planned);      // nothing to do, so nothing rebuilt
    }

    [Fact]
    public async Task End_to_end_the_duplicate_lands_beside_the_original()
    {
        using var fx = new Fixture();
        string source = fx.WriteSource("New Text Document.txt", "content");

        ScanResult scan = Duplication.Rename([source], Scanner.Scan([source]), fx.Source);
        var engine = new CopyEngine();
        CopyReport report = await engine.RunAsync(scan, fx.Source, CopyOperation.Copy);

        // No dialog, no skip, no failure — one new file beside the original.
        Assert.Equal(1, report.FilesCopied);
        Assert.Empty(report.Pending);
        Assert.Empty(report.Skipped);
        Assert.Empty(report.Failures);

        Assert.True(fx.SourceExists("New Text Document.txt"));
        Assert.Equal("content", File.ReadAllText(
            Path.Combine(fx.Source, "New Text Document - Copy.txt")));
    }

    [Fact]
    public async Task Duplicating_twice_produces_two_distinct_copies()
    {
        using var fx = new Fixture();
        string source = fx.WriteSource("a.txt", "content");

        // A fresh engine each time, as the host does: the second run has to notice
        // the first duplicate on disk rather than remember making it.
        for (int i = 0; i < 2; i++)
        {
            ScanResult scan = Duplication.Rename([source], Scanner.Scan([source]), fx.Source);
            await new CopyEngine().RunAsync(scan, fx.Source, CopyOperation.Copy);
        }

        Assert.True(fx.SourceExists("a.txt"));
        Assert.True(fx.SourceExists("a - Copy.txt"));
        Assert.True(fx.SourceExists("a - Copy (2).txt"));
    }

    [Fact]
    public void An_existing_folder_of_the_same_name_is_stepped_over()
    {
        // A directory sitting where the copy wants to write does not merely clash
        // by name — it makes the write fail. It has to count as occupied.
        using var fx = new Fixture();
        string source = fx.WriteSource("a.txt", "content");
        Directory.CreateDirectory(Path.Combine(fx.Source, "a - Copy.txt"));

        ScanResult scan = Plan(fx, source);

        Assert.Equal("a - Copy (2).txt", Assert.Single(scan.Files).RelativePath);
    }
}
