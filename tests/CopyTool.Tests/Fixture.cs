using CopyTool.Core;

namespace CopyTool.Tests;

/// <summary>
/// A throwaway source/destination pair on the real filesystem.
///
/// The engine is almost entirely about what the filesystem actually does —
/// timestamps at 2-second granularity, sharing violations, preallocation — so
/// these tests use real files rather than an abstraction that would agree with
/// whatever the engine believes.
/// </summary>
public sealed class Fixture : IDisposable
{
    public Fixture(string? name = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "CopyToolTests", name ?? Guid.NewGuid().ToString("N")[..8]);
        Source = Path.Combine(Root, "src");
        Destination = Path.Combine(Root, "dst");

        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Destination);
    }

    public string Root { get; }
    public string Source { get; }
    public string Destination { get; }

    /// <summary>Baseline for every timestamp, so "newer" and "older" are exact.</summary>
    public static readonly DateTime Epoch = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public string WriteSource(string name, string content, TimeSpan? age = null)
        => Write(Path.Combine(Source, name), content, age);

    public string WriteDestination(string name, string content, TimeSpan? age = null)
        => Write(Path.Combine(Destination, name), content, age);

    private static string Write(string path, string content, TimeSpan? age)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, Epoch + (age ?? TimeSpan.Zero));
        return path;
    }

    /// <summary>A file large enough to take the unbuffered pipeline rather than CopyFileEx.</summary>
    public string WriteLargeSource(string name, int megabytes, int seed = 1)
    {
        string path = Path.Combine(Source, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var random = new Random(seed);
        var buffer = new byte[1 << 20];

        using (var stream = File.Create(path))
        {
            for (int i = 0; i < megabytes; i++)
            {
                random.NextBytes(buffer);          // incompressible, so nothing can cheat
                stream.Write(buffer);
            }
        }

        File.SetLastWriteTimeUtc(path, Epoch);
        return path;
    }

    public string ReadDestination(string name) => File.ReadAllText(Path.Combine(Destination, name));
    public bool SourceExists(string name) => File.Exists(Path.Combine(Source, name));
    public bool DestinationExists(string name) => File.Exists(Path.Combine(Destination, name));

    public string[] DestinationNames() =>
        Directory.Exists(Destination)
            ? Directory.GetFiles(Destination, "*", SearchOption.AllDirectories)
                       .Select(p => Path.GetRelativePath(Destination, p)).Order().ToArray()
            : [];

    public static string Sha256(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    /// <summary>Runs a job over the whole source tree.</summary>
    public async Task<CopyReport> RunAsync(CopyOperation operation, JobPolicies? policies = null)
    {
        ScanResult scan = Scanner.Scan([Source]);
        var engine = new CopyEngine { Policies = policies ?? new JobPolicies() };
        return await engine.RunAsync(scan, Destination, operation);
    }

    /// <summary>
    /// Runs a job over named files rather than the folder, so they land directly
    /// in the destination. Scanning a folder makes its own name part of the
    /// relative path, which is correct but not what most of these tests mean.
    /// </summary>
    public Task<CopyReport> RunFilesAsync(CopyOperation operation, params string[] names)
        => RunFilesAsync(operation, new JobPolicies(), names);

    /// <inheritdoc cref="RunFilesAsync(CopyOperation, string[])"/>
    public async Task<CopyReport> RunFilesAsync(
        CopyOperation operation, JobPolicies policies, params string[] names)
    {
        string[] sources = names.Select(n => Path.Combine(Source, n)).ToArray();
        ScanResult scan = Scanner.Scan(sources);
        var engine = new CopyEngine { Policies = policies };
        return await engine.RunAsync(scan, Destination, operation);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                foreach (string f in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch (IOException) { }
                }
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
