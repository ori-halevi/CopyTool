using System.Collections.Concurrent;
using CopyTool.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace CopyTool.Core;

public enum MediaKind { Unknown, Ssd, Hdd, Removable, Network }

/// <summary>STORAGE_BUS_TYPE. Part of the public profile, so it lives here rather than in the P/Invoke layer.</summary>
public enum StorageBusType : uint
{
    Unknown = 0, Scsi = 1, Atapi = 2, Ata = 3, Ieee1394 = 4, Ssa = 5, Fibre = 6,
    Usb = 7, Raid = 8, Iscsi = 9, Sas = 0xA, Sata = 0xB, Sd = 0xC, Mmc = 0xD,
    Virtual = 0xE, FileBackedVirtual = 0xF, Spaces = 0x10, Nvme = 0x11, Scm = 0x12, Ufs = 0x13,
}

/// <summary>
/// What the engine knows about one volume. Probed once and cached: the answers
/// only change when hardware does, and the probe costs a device round-trip.
/// </summary>
public sealed record VolumeProfile
{
    public required string  Root          { get; init; }   // "C:\"
    public required string  Id            { get; init; }   // volume GUID path, stable across drive-letter changes
    public required string  FileSystem    { get; init; }   // NTFS / ReFS / exFAT / FAT32
    public required int     BytesPerSector{ get; init; }
    public required int     ClusterSize   { get; init; }
    public required MediaKind Media       { get; init; }
    public required StorageBusType Bus    { get; init; }

    /// <summary>Files at or above this size go down the unbuffered pipeline.</summary>
    public long LargeFileThreshold => Media switch
    {
        MediaKind.Hdd     => 32L * 1024 * 1024,
        MediaKind.Network => 64L * 1024 * 1024,
        _                 => 16L * 1024 * 1024,
    };

    /// <summary>
    /// Starting worker count for many-small-files work. This is only a seed —
    /// <see cref="ThroughputController"/> tunes the real value by measurement.
    /// </summary>
    public int InitialWorkers
    {
        get
        {
            // USB bridges frequently refuse the seek-penalty query, leaving Media
            // as Unknown. The link is the bottleneck either way, and guessing high
            // on a mechanical USB disk costs far more than guessing low on a USB
            // SSD — so the bus overrides an unknown medium.
            if (Bus is StorageBusType.Usb or StorageBusType.Ieee1394 && Media != MediaKind.Ssd)
                return 2;

            return Media switch
            {
                MediaKind.Hdd       => 1,   // a mechanical head cannot serve parallel streams
                MediaKind.Removable => 2,
                MediaKind.Network   => 8,   // latency hiding, not bandwidth
                MediaKind.Ssd       => Math.Min(8, Environment.ProcessorCount),
                _                   => 4,
            };
        }
    }

    /// <summary>I/O size for the large-file pipeline.</summary>
    public int ChunkSize => Media switch
    {
        MediaKind.Hdd     => 1 << 20,   // 1 MB
        MediaKind.Network => 4 << 20,   // 4 MB — fewer round trips
        _                 => 2 << 20,   // 2 MB
    };

    /// <summary>
    /// How many I/Os the large-file pipeline keeps in flight.
    ///
    /// NVMe needs a deep queue to reach full bandwidth, but on a mechanical or
    /// USB-attached disk the same depth causes seek thrashing and starves every
    /// other reader on the device — a copy should not make a video stutter.
    /// </summary>
    public int QueueDepth
    {
        get
        {
            if (Bus is StorageBusType.Usb or StorageBusType.Ieee1394 && Media != MediaKind.Ssd) return 2;

            return Media switch
            {
                MediaKind.Hdd       => 2,
                MediaKind.Removable => 2,
                MediaKind.Network   => 8,
                MediaKind.Ssd       => 8,
                _                   => 4,
            };
        }
    }

    public bool SupportsBlockCloning => FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        $"{Root} {FileSystem} {Media}/{Bus} sector={BytesPerSector} cluster={ClusterSize}";
}

public static class VolumeProfiler
{
    private static readonly ConcurrentDictionary<string, VolumeProfile> Cache = new(StringComparer.OrdinalIgnoreCase);

    // Resolving a path to its volume root is itself expensive — GetFullPath plus a
    // 32k char buffer plus a syscall — and it used to run before the cache was ever
    // consulted, so the cache saved only the device probe. Keyed on the raw path,
    // repeated lookups for files in one job cost a dictionary hit and nothing else.
    private static readonly ConcurrentDictionary<string, string> RootCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many paths the root cache keeps before it is thrown away and rebuilt.
    ///
    /// The key is a full path and a job hands us one per file, so unbounded this
    /// grew for the entire life of the host process: a 200,000-file move retained
    /// 200,000 strings that no collection could reclaim, and a process advertising
    /// a 0.4 MB idle footprint stopped having one. Dropping the whole cache on
    /// overflow rather than evicting entries keeps this to one comparison per
    /// miss — it is a cache, and rebuilding it costs a syscall per path.
    /// </summary>
    private const int RootCacheLimit = 4096;
    private static int _rootCacheEntries;

    /// <summary>Profile of the volume that holds <paramref name="path"/>.</summary>
    public static VolumeProfile ForPath(string path)
    {
        string root = RootCache.GetOrAdd(path, ResolveRoot);
        return Cache.GetOrAdd(root, Probe);
    }

    private static string ResolveRoot(string path)
    {
        if (Interlocked.Increment(ref _rootCacheEntries) > RootCacheLimit)
        {
            RootCache.Clear();
            Volatile.Write(ref _rootCacheEntries, 1);
        }

        return GetVolumeRoot(path);
    }

    /// <summary>
    /// Free space on the volume holding <paramref name="path"/>, right now.
    ///
    /// Deliberately not part of <see cref="VolumeProfile"/>: that is cached for
    /// the life of the process because geometry does not change, and free space
    /// does — constantly, including because of the copy this call is about to
    /// authorise. Cached, it meant a second job onto the same disk was measured
    /// against the space that existed before the first one ran, and that space
    /// freed after a "not enough room" refusal was never noticed.
    /// </summary>
    public static long FreeBytes(string path)
    {
        string root = RootCache.GetOrAdd(path, ResolveRoot);
        return Win32.GetDiskFreeSpaceEx(root, out ulong available, out _, out _) ? (long)available : 0;
    }

    /// <summary>True when both paths sit on the same volume — the rename fast path for moves.</summary>
    public static bool SameVolume(string a, string b) =>
        string.Equals(ForPath(a).Id, ForPath(b).Id, StringComparison.OrdinalIgnoreCase);

    private static string GetVolumeRoot(string path)
    {
        string full = Path.GetFullPath(path);
        var buf = new char[1024];
        if (Win32.GetVolumePathName(full, buf, (uint)buf.Length))
            return new string(buf, 0, Array.IndexOf(buf, '\0') is var i && i >= 0 ? i : buf.Length);

        return Path.GetPathRoot(full) ?? full;
    }

    private static VolumeProfile Probe(string root)
    {
        // UNC paths have no volume geometry to query; treat them as network.
        bool isUnc = root.StartsWith(@"\\", StringComparison.Ordinal);

        string id = root;
        if (!isUnc)
        {
            var idBuf = new char[512];
            if (Win32.GetVolumeNameForVolumeMountPoint(root, idBuf, (uint)idBuf.Length))
            {
                int end = Array.IndexOf(idBuf, '\0');
                id = new string(idBuf, 0, end >= 0 ? end : idBuf.Length);
            }
        }

        int bytesPerSector = 4096, clusterSize = 4096;
        if (!isUnc && Win32.GetDiskFreeSpace(root, out uint spc, out uint bps, out _, out _) && bps > 0)
        {
            bytesPerSector = (int)bps;
            clusterSize    = (int)(spc * bps);
        }

        string fs = "Unknown";
        var driveKind = DriveType.Unknown;
        try
        {
            var di = new DriveInfo(root);
            fs = di.DriveFormat;
            driveKind = di.DriveType;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unformatted, disconnected or permission-denied volume: defaults stand.
        }

        var (media, bus) = isUnc || driveKind == DriveType.Network
            ? (MediaKind.Network, StorageBusType.Unknown)
            : ProbeDevice(root, driveKind);

        return new VolumeProfile
        {
            Root = root, Id = id, FileSystem = fs,
            BytesPerSector = bytesPerSector, ClusterSize = clusterSize,
            Media = media, Bus = bus,
        };
    }

    /// <summary>
    /// Asks the storage stack whether the device incurs a seek penalty (i.e. is
    /// mechanical) and what bus it hangs off. Opening the volume with zero access
    /// rights is a query-only handle and needs no elevation.
    /// </summary>
    private static unsafe (MediaKind, StorageBusType) ProbeDevice(string root, DriveType driveKind)
    {
        string device = @"\\.\" + root.TrimEnd('\\');

        using SafeFileHandle h = Win32.CreateFile(
            device, 0, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, 0,
            Win32.OPEN_EXISTING, 0, 0);

        if (h.IsInvalid)
            return (driveKind == DriveType.Removable ? MediaKind.Removable : MediaKind.Unknown, StorageBusType.Unknown);

        var bus = StorageBusType.Unknown;
        var query = new Win32.STORAGE_PROPERTY_QUERY
        {
            PropertyId = (uint)Win32.StoragePropertyId.StorageDeviceProperty,
            QueryType = 0,
        };

        int descSize = sizeof(Win32.STORAGE_DEVICE_DESCRIPTOR) + 1024;
        nint buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(descSize);
        try
        {
            if (Win32.DeviceIoControl(h, Win32.IOCTL_STORAGE_QUERY_PROPERTY, ref query,
                                      (uint)sizeof(Win32.STORAGE_PROPERTY_QUERY), buffer, (uint)descSize, out _, 0))
            {
                var desc = *(Win32.STORAGE_DEVICE_DESCRIPTOR*)buffer;
                bus = (StorageBusType)desc.BusType;
            }
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }

        // Bus type alone settles the common cases; NVMe is never mechanical and
        // USB says nothing about the medium behind it.
        if (bus == StorageBusType.Nvme) return (MediaKind.Ssd, bus);

        var penaltyQuery = new Win32.STORAGE_PROPERTY_QUERY
        {
            PropertyId = (uint)Win32.StoragePropertyId.StorageDeviceSeekPenaltyProperty,
            QueryType = 0,
        };

        int penSize = sizeof(Win32.DEVICE_SEEK_PENALTY_DESCRIPTOR);
        nint penBuf = System.Runtime.InteropServices.Marshal.AllocHGlobal(penSize);
        try
        {
            if (Win32.DeviceIoControl(h, Win32.IOCTL_STORAGE_QUERY_PROPERTY, ref penaltyQuery,
                                      (uint)sizeof(Win32.STORAGE_PROPERTY_QUERY), penBuf, (uint)penSize, out _, 0))
            {
                var pen = *(Win32.DEVICE_SEEK_PENALTY_DESCRIPTOR*)penBuf;
                return (pen.IncursSeekPenalty ? MediaKind.Hdd : MediaKind.Ssd, bus);
            }
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(penBuf); }

        if (driveKind == DriveType.Removable) return (MediaKind.Removable, bus);
        return (MediaKind.Unknown, bus);
    }
}
