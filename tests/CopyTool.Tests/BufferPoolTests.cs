using CopyTool.Core;
using Xunit;

namespace CopyTool.Tests;

/// <summary>
/// The pool exists so a job stops allocating and freeing ~16 MB of aligned
/// memory per large file. It must hand back blocks of exactly the shape the
/// pipeline asks for, or every rent misses and it becomes an elaborate way to
/// allocate.
/// </summary>
public class BufferPoolTests
{
    private static (VolumeProfile Source, VolumeProfile Destination) Profiles()
    {
        VolumeProfile p = VolumeProfiler.ForPath(Path.GetTempPath());
        return (p, p);
    }

    [Fact]
    public void The_pool_is_sized_for_what_the_pipeline_will_request()
    {
        // Both sides derive the shape from the same helper, so a rent hits rather
        // than quietly falling back to a fresh allocation on every slot.
        var (src, dst) = Profiles();
        (int chunk, int sector) = FileCopier.PlanChunk(src, dst);

        using var pool = CopyBufferPool.For(src, dst);

        Assert.Equal(chunk, pool.Length);
        Assert.Equal(sector, pool.Alignment);
        Assert.Equal(0, pool.Length % pool.Alignment);   // unbuffered I/O demands it
    }

    [Fact]
    public async Task Several_large_files_in_one_job_still_arrive_intact()
    {
        // The pool means the same blocks are handed to file after file. If a
        // returned buffer were reused without being fully overwritten, the second
        // file would carry fragments of the first — and be exactly the right size
        // while doing so.
        using var fx = new Fixture();
        string a = fx.WriteLargeSource("a.bin", megabytes: 20, seed: 1);
        string b = fx.WriteLargeSource("b.bin", megabytes: 20, seed: 2);
        string c = fx.WriteLargeSource("c.bin", megabytes: 20, seed: 3);

        CopyReport report = await fx.RunFilesAsync(CopyOperation.Copy, "a.bin", "b.bin", "c.bin");

        Assert.Empty(report.Failures);
        Assert.Equal(3, report.FilesCopied);

        foreach (string name in new[] { "a.bin", "b.bin", "c.bin" })
        {
            Assert.Equal(Fixture.Sha256(Path.Combine(fx.Source, name)),
                         Fixture.Sha256(Path.Combine(fx.Destination, name)));
        }
    }

    [Fact]
    public async Task Files_of_differing_sizes_share_the_pool_without_bleeding()
    {
        // Sizes chosen so the tail chunk differs between files: a stale byte in a
        // reused buffer shows up in the padding of the shorter one.
        using var fx = new Fixture();
        fx.WriteLargeSource("big.bin", megabytes: 24, seed: 7);

        string small = fx.WriteLargeSource("small.bin", megabytes: 17, seed: 8);
        using (var stream = new FileStream(small, FileMode.Append))
        {
            stream.Write(new byte[999]);
        }

        CopyReport report = await fx.RunFilesAsync(CopyOperation.Copy, "big.bin", "small.bin");

        Assert.Empty(report.Failures);
        foreach (string name in new[] { "big.bin", "small.bin" })
        {
            Assert.Equal(new FileInfo(Path.Combine(fx.Source, name)).Length,
                         new FileInfo(Path.Combine(fx.Destination, name)).Length);
            Assert.Equal(Fixture.Sha256(Path.Combine(fx.Source, name)),
                         Fixture.Sha256(Path.Combine(fx.Destination, name)));
        }
    }

    [Fact]
    public async Task A_disposed_pool_does_not_break_a_later_copy()
    {
        var (src, dst) = Profiles();
        var pool = CopyBufferPool.For(src, dst);
        pool.Dispose();

        using var fx = new Fixture();
        string source = fx.WriteLargeSource("after.bin", megabytes: 18);

        // The engine builds its own pool per run; this only checks that disposing
        // one does not poison anything shared.
        await fx.RunFilesAsync(CopyOperation.Copy, "after.bin");

        Assert.Equal(Fixture.Sha256(source), Fixture.Sha256(Path.Combine(fx.Destination, "after.bin")));
    }
}
