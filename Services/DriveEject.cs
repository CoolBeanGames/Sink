using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sink.Services;

/// <summary>
/// Sends the same volume-level eject Windows Explorer issues on "Safely
/// Remove" — clicking Eject in Sink only cleared in-app state and left the
/// iPod mounted in Windows (task 142).
/// </summary>
public static class DriveEject
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageEjectMedia = 0x2D4808;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>Best-effort; a device that doesn't support the IOCTL (or is already gone) just returns false.</summary>
    public static bool TryEject(string driveRoot)
    {
        var letter = driveRoot.TrimEnd('\\', '/');
        if (letter.Length == 0) return false;
        try
        {
            using var handle = CreateFile($@"\\.\{letter}", GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) return false;
            return DeviceIoControl(handle, IoctlStorageEjectMedia, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Drive eject failed for {letter}: {ex.Message}");
            return false;
        }
    }
}
