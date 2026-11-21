using System;
using System.IO;
using Microsoft.Win32.SafeHandles;
using static Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.NativeFontFileSystem;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// Uses native volume/device and file/inode identifiers to recognize hard-linked paths.
/// </summary>
public sealed class PlatformPhysicalFileIdentityProvider : IPhysicalFileIdentityProvider
{
    /// <summary>Reads native hard-link evidence without guessing when the platform cannot supply it.</summary>
    /// <param name="path">The existing file path.</param>
    /// <returns>The native count, or null on unsupported platforms or failed reads.</returns>
    public uint? GetLinkCount(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return GetFileInformationByHandle(handle, out ByHandleFileInformation information) ? information._numberOfLinks : null;
            }

            if (OperatingSystem.IsLinux())
            {
                const uint LinkCountMask = 0x00000004;
                return Statx(AtFileDescriptorCurrentWorkingDirectory, path, 0, LinkCountMask, out StatxBuffer information) == 0
                    && (information._mask & LinkCountMask) != 0 ? information._numberOfLinks : null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }

        return null;
    }

    /// <summary>Returns a native physical file identity, falling back conservatively to the normalized path.</summary>
    /// <param name="path">An existing physical font path.</param>
    /// <returns>The platform volume/device and file/inode identity.</returns>
    public string GetIdentity(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            {
                ulong fileIndex = ((ulong)information._fileIndexHigh << 32) | information._fileIndexLow;
                return $"win:{information._volumeSerialNumber:x8}:{fileIndex:x16}";
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            StatxBuffer information = default;
            if (Statx(AtFileDescriptorCurrentWorkingDirectory, path, 0, StatxBasicStats, out information) == 0)
            {
                return $"unix:{information._deviceMajor:x8}:{information._deviceMinor:x8}:{information._inode:x16}";
            }
        }

        return Path.GetFullPath(path);
    }
}
