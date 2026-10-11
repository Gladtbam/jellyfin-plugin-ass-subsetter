using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

internal static class NativeFontFileSystem
{
    internal const int AtFileDescriptorCurrentWorkingDirectory = -100;
    internal const uint StatxBasicStats = 0x000007ff;

    internal static string? GetFileSystemIdentity(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                const uint backupSemantics = 0x02000000;
                using SafeFileHandle handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, 3, backupSemantics, IntPtr.Zero);
                return !handle.IsInvalid && GetFileInformationByHandle(handle, out ByHandleFileInformation information)
                    ? $"win:{information.VolumeSerialNumber:x8}" : null;
            }

            if (OperatingSystem.IsLinux())
            {
                const uint mountIdMask = 0x00001000;
                return Statx(AtFileDescriptorCurrentWorkingDirectory, path, 0, StatxBasicStats | mountIdMask, out StatxBuffer information) == 0
                    && (information.Mask & mountIdMask) != 0
                    ? $"unix:{information.DeviceMajor:x8}:{information.DeviceMinor:x8}:{information.MountId:x16}" : null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }

        return null;
    }

    internal static bool? CreateSingleDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateDirectory(path, IntPtr.Zero) ? true : Marshal.GetLastWin32Error() == 183 && Directory.Exists(path) ? false : null;
        }

        if (OperatingSystem.IsLinux())
        {
            return Mkdir(path, 0x1ff) == 0 ? true : Marshal.GetLastWin32Error() == 17 && Directory.Exists(path) ? false : null;
        }

        return null;
    }

    internal static void MoveFileAtomically(string source, string destination)
    {
        try
        {
            bool moved = OperatingSystem.IsWindows()
                ? MoveFileEx(source, destination, 0)
                : OperatingSystem.IsLinux() && RenameAt2(AtFileDescriptorCurrentWorkingDirectory, source, AtFileDescriptorCurrentWorkingDirectory, destination, 1) == 0;
            if (!moved)
            {
                throw new IOException("Atomic no-overwrite font move failed.", new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new IOException("Atomic no-overwrite font move is unavailable.", exception);
        }
    }

    internal static void MoveRestoreFileWithoutOverwrite(string source, string destination)
    {
        try
        {
            MoveFileAtomically(source, destination);
        }
        catch (IOException exception) when (OperatingSystem.IsLinux()
            && (exception.InnerException is Win32Exception { NativeErrorCode: 22 or 38 or 95 }
                or EntryPointNotFoundException))
        {
            // link() creates the destination atomically and rejects occupied paths and cross-volume moves.
            // Keep both links if unlink fails; the durable Restoring intent reconciles that state.
            if (Link(source, destination) != 0)
            {
                throw new IOException("No-overwrite restore hard-link promotion failed.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            if (Unlink(source) != 0)
            {
                throw new IOException("Restore destination was created, but source link removal failed; reconciliation is required.", new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);

    [DllImport("libc", EntryPoint = "unlink", SetLastError = true)]
    private static extern int Unlink([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, uint flags);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(int sourceDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string source, int targetDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    internal static extern int Statx(int directoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out StatxBuffer buffer);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, FileShare share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr security);

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int Mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    // Linux uapi statx: device IDs at 0x88/0x8c, mount ID at 0x90.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct StatxBuffer
    {
        [FieldOffset(0)]
        public uint Mask;

        [FieldOffset(16)]
        public uint NumberOfLinks;

        [FieldOffset(32)]
        public ulong Inode;

        [FieldOffset(136)]
        public uint DeviceMajor;

        [FieldOffset(140)]
        public uint DeviceMinor;

        [FieldOffset(144)]
        public ulong MountId;
    }
}
