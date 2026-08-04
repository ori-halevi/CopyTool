using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
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

        await writer.WriteLineAsync(ElevatedProtocol.Encode("hello", nonce));

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

            // An exact field count, not a minimum: a command that does not decode
            // to precisely four fields is one this worker does not understand, and
            // guessing at a path it is about to write as administrator is the one
            // thing it must never do.
            string[] parts = ElevatedProtocol.Decode(line);
            if (parts.Length == 0) continue;
            if (parts[0] == "quit") break;
            if (parts[0] != "copy" || parts.Length != 4) continue;

            string source = parts[1], destination = parts[2];
            if (!long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long size))
                size = 0;

            try
            {
                CopyReport report = await engine.CopyExplicitAsync(
                    [(source, destination, size)],
                    onFileDone: (_, path, _) => RestoreOwnership(path, settings.OwnerSid),
                    onDirectoryCreated: path => RestoreOwnership(path, settings.OwnerSid),
                    ct: control.Token);

                await writer.WriteLineAsync(report.Failures.Count == 0
                    ? ElevatedProtocol.Encode("ok", source, size.ToString(CultureInfo.InvariantCulture))
                    : ElevatedProtocol.Encode("fail", source, report.Failures[0].Reason));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                await writer.WriteLineAsync(ElevatedProtocol.Encode("fail", source, e.Message));
            }
        }

        return 0;
    }

    /// <summary>
    /// Gives what we created back to the user who asked for the copy. Best-effort:
    /// in a genuinely protected location the elevated owner is the correct outcome.
    ///
    /// Directories matter as much as files here. A folder this worker created stays
    /// owned by Administrators otherwise, and the user is left with a destination
    /// they cannot write to or delete — with nothing on screen saying why.
    /// </summary>
    private static void RestoreOwnership(string path, string? ownerSid)
    {
        if (ownerSid is null) return;
        try
        {
            var sid = new SecurityIdentifier(ownerSid);

            if (Directory.Exists(path))
            {
                var dir = new DirectoryInfo(path);
                DirectorySecurity security = dir.GetAccessControl();
                security.SetOwner(sid);
                dir.SetAccessControl(security);
                return;
            }

            var info = new FileInfo(path);
            FileSecurity fileSecurity = info.GetAccessControl();
            fileSecurity.SetOwner(sid);
            info.SetAccessControl(fileSecurity);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException
                                   or IdentityNotMappedException
                                   or InvalidOperationException or ArgumentException
                                   or PrivilegeNotHeldException)
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
