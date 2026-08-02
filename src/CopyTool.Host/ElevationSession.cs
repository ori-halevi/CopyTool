using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using CopyTool.Core;
using Microsoft.Win32.SafeHandles;

namespace CopyTool.Host;

public enum ElevationOutcome
{
    Completed,
    /// <summary>The user chose No, or the prompt timed out.</summary>
    Declined,
    /// <summary>No admin rights available at all — a standard user account.</summary>
    NotPermitted,
    Failed,
}

/// <summary>Why an elevation attempt failed. A code; the wording lives in the UI.</summary>
public enum ElevationError
{
    None,
    AlreadyRequested,
    WorkerDidNotConnect,
    WorkerStopped,
    Unknown,
}

/// <summary>
/// One consent, held open for the length of a job.
///
/// The user is asked once — at the start when the policy says "elevate now", or
/// the moment they press the banner — and the worker then stays available for
/// whatever the copy runs into later. Asking again per file, which is what a
/// one-shot worker forces, is the behaviour this exists to avoid.
///
/// The host owns the pipe and accepts exactly one connection, and the worker
/// proves itself with a nonce before any command is honoured, so nothing else can
/// reach the elevated process.
/// </summary>
public sealed class ElevationSession : IAsyncDisposable
{
    private const int ERROR_CANCELLED = 1223;

    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private SafeWaitHandle? _worker;
    private string? _settingsPath;

    public bool IsOpen { get; private set; }
    public int Copied { get; private set; }
    public int Failed { get; private set; }
    public long Bytes { get; private set; }

    /// <summary>
    /// Raises the UAC prompt and brings a worker up. Returns the outcome; a
    /// declined prompt is an answer, not an exception.
    /// </summary>
    public async Task<(ElevationOutcome Outcome, ElevationError Error)> OpenAsync(
        bool backgroundIo, CancellationToken ct = default)
    {
        if (IsOpen) return (ElevationOutcome.Completed, ElevationError.None);
        if (!Elevation.CanElevate) return (ElevationOutcome.NotPermitted, ElevationError.None);

        string pipeName = $"CopyTool.Elevated.{Guid.NewGuid():N}";
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _settingsPath = WriteSettings(backgroundIo);

        // Server side, single instance: once the worker is connected the channel
        // is closed to everyone else.
        _pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        _worker = TryLaunchElevated(_settingsPath, pipeName, nonce, out ElevationOutcome? failure);
        if (_worker is null)
        {
            await CleanupAsync();
            return (failure ?? ElevationOutcome.Failed, ElevationError.Unknown);
        }

        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(TimeSpan.FromSeconds(15));
            await _pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false);

            _reader = new StreamReader(_pipe);
            _writer = new StreamWriter(_pipe) { AutoFlush = true };

            string? hello = await _reader.ReadLineAsync(connect.Token).ConfigureAwait(false);
            if (hello != $"hello\t{nonce}")
            {
                HostLog.Write("elevated worker failed the handshake");
                await CleanupAsync();
                return (ElevationOutcome.Failed, ElevationError.WorkerDidNotConnect);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException)
        {
            await CleanupAsync();
            return (ElevationOutcome.Failed, ElevationError.WorkerDidNotConnect);
        }

        IsOpen = true;
        HostLog.Write("elevated session open");
        return (ElevationOutcome.Completed, ElevationError.None);
    }

    /// <summary>Copies one item through the worker. False means it did not land.</summary>
    public async Task<bool> CopyAsync(string source, string destination, long size, CancellationToken ct = default)
    {
        if (!IsOpen || _writer is null || _reader is null) return false;

        try
        {
            await _writer.WriteLineAsync($"copy\t{source}\t{destination}\t{size}").ConfigureAwait(false);
            string? reply = await _reader.ReadLineAsync(ct).ConfigureAwait(false);

            if (reply is null)
            {
                IsOpen = false;                      // worker went away
                return false;
            }

            if (reply.StartsWith("ok\t", StringComparison.Ordinal))
            {
                Copied++;
                Bytes += size;
                return true;
            }

            Failed++;
            HostLog.Write($"  elevated FAILED {source}: {reply}");
            return false;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            IsOpen = false;
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsOpen && _writer is not null)
        {
            try { await _writer.WriteLineAsync("quit").ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
        await CleanupAsync();
    }

    private async Task CleanupAsync()
    {
        IsOpen = false;

        // Give the worker a moment to exit cleanly before dropping the pipe.
        if (_worker is not null)
        {
            await WaitForHandleAsync(_worker, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            _worker.Dispose();
            _worker = null;
        }

        _writer?.Dispose(); _writer = null;
        _reader?.Dispose(); _reader = null;
        _pipe?.Dispose();   _pipe = null;

        if (_settingsPath is not null) { Files.TryDelete(_settingsPath); _settingsPath = null; }
        HostLog.Write("elevated session closed");
    }

    private static string WriteSettings(bool backgroundIo)
    {
        string dir = Path.Combine(HostLog.Directory, "jobs");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"elevated-{Guid.NewGuid():N}.json");

        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            BackgroundIo = backgroundIo,
            OwnerSid = WindowsIdentity.GetCurrent().User?.Value,
        }));
        return path;
    }

    /// <summary>
    /// ShellExecuteEx with the "runas" verb is what raises the UAC prompt.
    /// Process.Start cannot do it without UseShellExecute, and the handle it
    /// returns is the only way a medium-IL process can wait on an elevated one.
    /// </summary>
    private static SafeWaitHandle? TryLaunchElevated(
        string settingsPath, string pipeName, string nonce, out ElevationOutcome? failure)
    {
        failure = null;

        string exe = Path.Combine(AppContext.BaseDirectory, "CopyTool.Elevated.exe");
        if (!File.Exists(exe))
        {
            HostLog.Write($"elevated worker missing: {exe}");
            failure = ElevationOutcome.Failed;
            return null;
        }

        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_FLAG_NO_UI,
            lpVerb = Elevation.IsElevated || Elevation.UacDisabled ? "open" : "runas",
            lpFile = exe,
            lpParameters = $"--settings \"{settingsPath}\" --pipe {pipeName} " +
                           $"--nonce {nonce} --parent {Environment.ProcessId}",
            lpDirectory = AppContext.BaseDirectory,
            nShow = SW_HIDE,
        };

        if (!ShellExecuteEx(ref info))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_CANCELLED)
            {
                HostLog.Write("elevation declined by user");
                failure = ElevationOutcome.Declined;
            }
            else
            {
                HostLog.Write($"ShellExecuteEx failed: win32={err}");
                failure = ElevationOutcome.Failed;
            }
            return null;
        }

        return info.hProcess == 0 ? null : new SafeWaitHandle(info.hProcess, ownsHandle: true);
    }

    /// <summary>Awaits a raw waitable handle without going through Process.</summary>
    private static Task WaitForHandleAsync(SafeWaitHandle handle, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var carrier = new ManualResetEvent(false);
        SafeWaitHandle original = carrier.SafeWaitHandle;
        carrier.SafeWaitHandle = handle;

        RegisteredWaitHandle? registration = null;
        registration = ThreadPool.RegisterWaitForSingleObject(
            carrier,
            (_, _) =>
            {
                registration?.Unregister(null);
                // Detach before disposing, or the carrier would close our handle.
                carrier.SafeWaitHandle = original;
                carrier.Dispose();
                tcs.TrySetResult();
            },
            state: null, timeout: timeout, executeOnlyOnce: true);

        return tcs.Task;
    }

    // --- ShellExecuteEx ----------------------------------------------------
    private const int SEE_MASK_NOCLOSEPROCESS = 0x00000040;
    private const int SEE_MASK_FLAG_NO_UI = 0x00000400;
    private const int SW_HIDE = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public int fMask;
        public nint hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIcon;
        public nint hProcess;
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);
}
