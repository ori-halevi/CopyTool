using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using CopyTool.Core;

namespace CopyTool.Elevated;

/// <summary>
/// The elevated worker. No window, no console, no COM, no listening endpoint.
///
/// It connects out to a pipe the host already owns and then serves copy commands
/// for the lifetime of one job. That is what lets the user consent once, up front,
/// instead of being asked again every time the copy reaches a protected file.
///
/// It cannot be discovered or reached by anything else: the host is the pipe
/// server and accepts exactly one connection, and this process proves itself with
/// a nonce before the first command is honoured.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string? settingsPath = Files.Arg(args, "--settings");
        string? pipeName = Files.Arg(args, "--pipe");
        string? nonce = Files.Arg(args, "--nonce");
        string? parentPid = Files.Arg(args, "--parent");

        if (settingsPath is null || pipeName is null || nonce is null) return 2;

        try
        {
            return Run(settingsPath, pipeName, nonce, parentPid).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private static async Task<int> Run(string settingsPath, string pipeName, string nonce, string? parentPid)
    {
        ElevatedSettings? settings =
            JsonSerializer.Deserialize<ElevatedSettings>(File.ReadAllText(settingsPath));
        if (settings is null) return 2;

        using var control = new JobControl();

        // If the host dies, this process must not keep writing to protected
        // locations unsupervised. Watching the parent handle is enough: the wait
        // completes the moment it exits, however it exits.
        if (int.TryParse(parentPid, out int pid))
        {
            try
            {
                var parent = Process.GetProcessById(pid);
                _ = parent.WaitForExitAsync().ContinueWith(_ => control.Cancel(), TaskScheduler.Default);
            }
            catch (ArgumentException)
            {
                return 3;   // parent already gone
            }
        }

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(5000, control.Token);
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException)
        {
            return 4;
        }

        using var reader = new StreamReader(pipe);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };

        await writer.WriteLineAsync($"hello\t{nonce}");

        var engine = new CopyEngine
        {
            Control = control,
            Policies = new JobPolicies { BackgroundIo = settings.BackgroundIo },
        };

        // One command per line. The host sends work as it discovers it and ends
        // with "quit"; nothing else can ever reach this loop.
        while (!control.IsCancelled)
        {
            string? line;
            try { line = await reader.ReadLineAsync(control.Token); }
            catch (Exception e) when (e is IOException or OperationCanceledException) { break; }

            if (line is null) break;

            string[] parts = line.Split('\t');
            if (parts[0] == "quit") break;
            if (parts[0] != "copy" || parts.Length < 4) continue;

            string source = parts[1], destination = parts[2];
            if (!long.TryParse(parts[3], out long size)) size = 0;

            try
            {
                CopyReport report = await engine.CopyExplicitAsync(
                    [(source, destination, size)],
                    onFileDone: (path, _) => RestoreOwnership(path, settings.OwnerSid),
                    ct: control.Token);

                if (report.Failures.Count == 0)
                    await writer.WriteLineAsync($"ok\t{source}\t{size}");
                else
                    await writer.WriteLineAsync($"fail\t{source}\t{Sanitise(report.Failures[0].Reason)}");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                Files.TryDelete(destination);
                await writer.WriteLineAsync($"fail\t{source}\t{Sanitise(e.Message)}");
            }
        }

        return 0;
    }

    /// <summary>Tabs and newlines would corrupt the line protocol.</summary>
    private static string Sanitise(string s) =>
        s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>
    /// Gives the file back to the user who asked for the copy. Best-effort: in a
    /// genuinely protected location the elevated owner is the correct outcome.
    /// </summary>
    private static void RestoreOwnership(string path, string? ownerSid)
    {
        if (ownerSid is null) return;
        try
        {
            var sid = new System.Security.Principal.SecurityIdentifier(ownerSid);
            var info = new FileInfo(path);
            System.Security.AccessControl.FileSecurity security = info.GetAccessControl();
            security.SetOwner(sid);
            info.SetAccessControl(security);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException
                                   or System.Security.Principal.IdentityNotMappedException
                                   or InvalidOperationException or ArgumentException)
        {
        }
    }
}

internal sealed record ElevatedSettings
{
    public bool BackgroundIo { get; init; }
    /// <summary>SID of the user the host runs as, so created files can be given back.</summary>
    public string? OwnerSid { get; init; }
}
