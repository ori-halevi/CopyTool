using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CopyTool.Core.Native;

/// <summary>
/// The Win32 surface the engine needs. Everything the BCL cannot express —
/// unbuffered handles, volume geometry, device type — lives here.
/// </summary>
internal static partial class Win32
{
    // --- CreateFile ---------------------------------------------------------
    internal const uint GENERIC_READ  = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;

    internal const uint FILE_SHARE_READ   = 0x1;
    internal const uint FILE_SHARE_WRITE  = 0x2;

    internal const uint CREATE_ALWAYS = 2;
    internal const uint OPEN_EXISTING = 3;

    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    internal const uint FILE_FLAG_DELETE_ON_CLOSE  = 0x04000000;

    /// <summary>
    /// Bypasses the system cache. Requires sector-aligned buffers, offsets and
    /// lengths. Not exposed by <see cref="FileOptions"/>, but honoured by it — the
    /// managed side passes it as <c>(FileOptions)FILE_FLAG_NO_BUFFERING</c>.
    /// </summary>
    internal const uint FILE_FLAG_NO_BUFFERING = 0x20000000;

    internal const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    // --- file size / timestamps --------------------------------------------
    // FILE_INFO_BY_HANDLE_CLASS
    internal const int FileEndOfFileInfo    = 6;
    internal const int FileIoPriorityHintInfo = 12;

    /// <summary>PRIORITY_HINT. Low-priority I/O is scheduled behind normal I/O.</summary>
    internal enum IoPriorityHint { VeryLow = 0, Low = 1, Normal = 2 }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFileInformationByHandle(
        SafeFileHandle hFile, int fileInformationClass, in long fileInformation, uint dwBufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFileIoPriorityHint(
        SafeFileHandle hFile, int fileInformationClass, in int fileInformation, uint dwBufferSize);

    // --- move ---------------------------------------------------------------
    internal const uint MOVEFILE_REPLACE_EXISTING = 0x1;
    internal const uint MOVEFILE_WRITE_THROUGH    = 0x8;

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, uint dwFlags);

    // --- CopyFileEx ---------------------------------------------------------
    internal delegate uint CopyProgressRoutine(
        long totalFileSize, long totalBytesTransferred, long streamSize, long streamBytesTransferred,
        uint streamNumber, uint callbackReason, nint hSourceFile, nint hDestinationFile, nint lpData);

    [DllImport("kernel32.dll", EntryPoint = "CopyFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CopyFileEx(
        string lpExistingFileName, string lpNewFileName, CopyProgressRoutine? lpProgressRoutine,
        nint lpData, in int pbCancel, uint dwCopyFlags);

    // --- volume geometry ----------------------------------------------------
    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumePathName(string lpszFileName, [Out] char[] lpszVolumePathName, uint cchBufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumeNameForVolumeMountPoint(string lpszVolumeMountPoint, [Out] char[] lpszVolumeName, uint cchBufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpace(
        string lpRootPathName, out uint lpSectorsPerCluster, out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters, out uint lpTotalNumberOfClusters);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpaceEx(
        string lpDirectoryName, out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    // --- device type (SSD vs HDD, bus) -------------------------------------
    internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    internal enum StoragePropertyId
    {
        StorageDeviceProperty = 0,
        StorageDeviceSeekPenaltyProperty = 7,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_PROPERTY_QUERY
    {
        public uint PropertyId;
        public uint QueryType;      // PropertyStandardQuery = 0
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_DEVICE_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        public byte DeviceType;
        public byte DeviceTypeModifier;
        [MarshalAs(UnmanagedType.U1)] public bool RemovableMedia;
        [MarshalAs(UnmanagedType.U1)] public bool CommandQueueing;
        public uint VendorIdOffset;
        public uint ProductIdOffset;
        public uint ProductRevisionOffset;
        public uint SerialNumberOffset;
        public uint BusType;        // STORAGE_BUS_TYPE
        public uint RawPropertiesLength;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        ref STORAGE_PROPERTY_QUERY lpInBuffer, uint nInBufferSize,
        nint lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, nint lpOverlapped);
}
