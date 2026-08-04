using System.IO;
using System.IO.Pipes;
using CopyTool.Core;

namespace CopyTool.Host;

/// <summary>
/// Entry point. Decides whether this process becomes the host or simply hands its
/// job to the one already running, then gets out of the way.
/// </summary>
internal static class Program
{
    private const string MutexName = @"Local\CopyTool.Host.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        // Exercises the broker end to end — launch, pipe, wait, result — without a
        // window or the job queue. Run it elevated and no UAC prompt appears, which
        // makes everything except the consent dialog itself testable.
        if (args.Length >= 3 && args[0].Equals("--selftest-elevate", StringComparison.OrdinalIgnoreCase))
            return SelfTestElevate(args[1], args[2]);

        string? jobPath = ParseJobArgument(args);
        TimeSpan idleTimeout = ParseIdleTimeout(args) ?? TimeSpan.FromMinutes(15);

        // One host per session. Losing this race is the normal case, not an error:
        // it means a host is already up and should receive the job instead.
        using var single = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);

        if (!isFirstInstance)
        {
            if (jobPath is null)
            {
                HostLog.Write("another host owns the session; nothing to deliver");
                return 0;
            }

            bool delivered = TryDeliver(jobPath);
            HostLog.Write($"delivered to running host: {jobPath} ({(delivered ? "ok" : "FAILED")})");
            return delivered ? 0 : 1;
        }

        var app = new App(jobPath, idleTimeout);
        return app.Run();
    }

    /// <summary>
    /// Drives a real elevation session — prompt, handshake, two streamed copies,
    /// shutdown. The second copy is the point: it proves one consent covers work
    /// that was not known when the prompt was answered.
    /// </summary>
    private static int SelfTestElevate(string source, string destinationDir)
    {
        try
        {
            return SelfTestElevateAsync(source, destinationDir).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            HostLog.Write($"SELFTEST elevate THREW: {e}");
            return 2;
        }
    }

    private static async Task<int> SelfTestElevateAsync(string source, string destinationDir)
    {
        await using var session = new ElevationSession();

        (ElevationOutcome outcome, ElevationError error) = await session.OpenAsync(backgroundIo: false);
        HostLog.Write($"SELFTEST open: outcome={outcome} error={error}");
        if (outcome != ElevationOutcome.Completed) return 1;

        long size = new FileInfo(source).Length;
        (bool first, _) = await session.CopyAsync(
            source, Path.Combine(destinationDir, Path.GetFileName(source)), size);

        // Sent only after the first one landed: a one-shot worker could not do this.
        (bool second, _) = await session.CopyAsync(
            source, Path.Combine(destinationDir, "second-" + Path.GetFileName(source)), size);

        HostLog.Write($"SELFTEST elevate: first={first} second={second} " +
                      $"copied={session.Copied} failed={session.Failed} bytes={session.Bytes}");

        return first && second ? 0 : 1;
    }

    private static string? ParseJobArgument(string[] args) => Files.Arg(args, "--job");

    /// <summary>Shortens the idle window so tests do not have to wait 15 minutes.</summary>
    private static TimeSpan? ParseIdleTimeout(string[] args) =>
        int.TryParse(Files.Arg(args, "--idle-seconds"), out int seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    /// <summary>Hands a job path to the host that already owns the pipe.</summary>
    private static bool TryDeliver(string path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", App.PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(path);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
