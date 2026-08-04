using CopyTool.Core;
using Xunit;

namespace CopyTool.Tests;

/// <summary>
/// Checks that are cheap now and expensive to discover late: no space, a name the
/// destination cannot represent, a folder copied into itself.
/// </summary>
public class PreflightTests
{
    private static PreflightResult Run(Fixture fx, string source, string destination,
                                       CopyOperation operation = CopyOperation.Copy)
    {
        ScanResult scan = Scanner.Scan([source]);
        return Preflight.Run([source], destination, scan, operation);
    }

    [Fact]
    public void A_normal_copy_raises_nothing()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "content");

        PreflightResult result = Run(fx, fx.Source, fx.Destination);

        Assert.True(result.CanProceed);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Source_equal_to_destination_is_blocking()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "content");

        PreflightResult result = Run(fx, fx.Source, fx.Source);

        Assert.False(result.CanProceed);
        Assert.Contains(result.Blocking, i => i.Code == PreflightCode.SameSourceAndDestination);
    }

    [Fact]
    public void Destination_inside_the_source_is_blocking()
    {
        // Copying a folder into itself walks forever, re-copying what it just
        // wrote. Cheap to detect, impossible to recover from later.
        using var fx = new Fixture();
        fx.WriteSource("a.txt", "content");
        string inner = Path.Combine(fx.Source, "inner");
        Directory.CreateDirectory(inner);

        PreflightResult result = Run(fx, fx.Source, inner);

        Assert.False(result.CanProceed);
        Assert.Contains(result.Blocking, i => i.Code == PreflightCode.DestinationInsideSource);
    }

    [Fact]
    public void A_reserved_windows_name_is_a_warning_not_a_block()
    {
        using var fx = new Fixture();
        fx.WriteSource("CON.txt", "content");

        PreflightResult result = Run(fx, fx.Source, fx.Destination);

        Assert.True(result.CanProceed);
        Assert.Contains(result.Warnings, i => i.Code == PreflightCode.ReservedNames);
    }

    [Fact]
    public void Issues_carry_numbers_rather_than_prose()
    {
        using var fx = new Fixture();
        fx.WriteSource("CON.txt", "a");
        fx.WriteSource("PRN.txt", "b");

        PreflightResult result = Run(fx, fx.Source, fx.Destination);

        PreflightIssue issue = Assert.Single(result.Warnings, i => i.Code == PreflightCode.ReservedNames);
        Assert.Equal(2, issue.Count);
    }

    [Fact]
    public void A_job_larger_than_the_disk_is_blocking()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", new string('x', 5000));

        ScanResult scan = Scanner.Scan([fx.Source]);
        PreflightResult result = Preflight.Run(
            [fx.Source], fx.Destination, scan, CopyOperation.Copy, freeSpace: _ => 100);

        Assert.False(result.CanProceed);
        PreflightIssue issue = Assert.Single(result.Blocking, i => i.Code == PreflightCode.NotEnoughSpace);
        Assert.Equal(5000, issue.Bytes);
        Assert.Equal(100, issue.OtherBytes);
    }

    [Fact]
    public void Bytes_the_destination_already_holds_are_not_demanded_again()
    {
        // Re-copying a tree that is already there writes nothing, so refusing it
        // for want of room is a refusal to do nothing — which is exactly what a
        // near-full backup drive would have hit on every repeat run.
        using var fx = new Fixture();
        fx.WriteSource("a.txt", new string('x', 5000));
        fx.WriteDestination(Path.Combine("src", "a.txt"), new string('x', 5000));

        ScanResult scan = Scanner.Scan([fx.Source]);
        PreflightResult result = Preflight.Run(
            [fx.Source], fx.Destination, scan, CopyOperation.Copy, freeSpace: _ => 100);

        Assert.True(result.CanProceed);
        Assert.DoesNotContain(result.Issues, i => i.Code == PreflightCode.NotEnoughSpace);
    }

    [Fact]
    public void A_destination_file_that_differs_still_needs_room()
    {
        // Same name, different content: this one really will be written, so the
        // shortcut must not swallow it.
        using var fx = new Fixture();
        fx.WriteSource("a.txt", new string('x', 5000));
        fx.WriteDestination(Path.Combine("src", "a.txt"), new string('y', 20), TimeSpan.FromHours(3));

        ScanResult scan = Scanner.Scan([fx.Source]);
        PreflightResult result = Preflight.Run(
            [fx.Source], fx.Destination, scan, CopyOperation.Copy, freeSpace: _ => 100);

        Assert.False(result.CanProceed);
    }

    [Fact]
    public void Plenty_of_room_raises_nothing()
    {
        using var fx = new Fixture();
        fx.WriteSource("a.txt", new string('x', 5000));

        ScanResult scan = Scanner.Scan([fx.Source]);
        PreflightResult result = Preflight.Run(
            [fx.Source], fx.Destination, scan, CopyOperation.Copy, freeSpace: _ => 1L << 40);

        Assert.True(result.CanProceed);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void A_move_onto_its_own_folder_is_blocking()
    {
        using var fx = new Fixture();
        string file = fx.WriteSource("a.txt", "content");

        ScanResult scan = Scanner.Scan([file]);
        PreflightResult result = Preflight.Run([file], fx.Source, scan, CopyOperation.Move);

        Assert.False(result.CanProceed);
        Assert.Contains(result.Blocking, i => i.Code == PreflightCode.AlreadyAtDestination);
    }
}
