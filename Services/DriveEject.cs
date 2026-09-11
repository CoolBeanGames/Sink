using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sink.Services;

/// <summary>
/// Performs the same lock → dismount → eject sequence Windows Explorer's
/// "Safely Remove" uses — clicking Eject in Sink used to only clear in-app
/// state and left the iPod mounted in Windows (task 142). A bare
/// IOCTL_STORAGE_EJECT_MEDIA without locking/dismounting first can eject
/// while a Windows-buffered write is still sitting in the OS cache, silently
/// losing a just-finished sync (task 144) — locking the volume forces a
/// flush and fails outright if anything still has it open.
/// </summary>
public static class DriveEject
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const uint FsctlLockVolume = 0x00090018;
    private const uint FsctlDismountVolume = 0x00090020;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle hFile);

    /// <summary>Forces any Windows-buffered writes on the volume out to the physical media without unmounting it.</summary>
    public static void Flush(string driveRoot)
    {
        var letter = driveRoot.TrimEnd('\\', '/');
        if (letter.Length == 0) return;
        try
        {
            using var handle = CreateFile(
                $@"\\.\{letter}", GenericRead | GenericWrite, FileShareReadWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (!handle.IsInvalid) FlushFileBuffers(handle);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
    }

    /// <summary>
    /// Best-effort. Returns false (and leaves the media mounted) if the
    /// volume is still busy or the device doesn't support the IOCTL — that's
    /// safer than forcing an eject that could lose unflushed writes.
    /// </summary>
    public static bool TryEject(string driveRoot)
    {
        var letter = driveRoot.TrimEnd('\\', '/');
        if (letter.Length == 0) return false;
        try
        {
            using var handle = CreateFile(
                $@"\\.\{letter}", GenericRead | GenericWrite, FileShareReadWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) return false;

            FlushFileBuffers(handle);
            if (!DeviceIoControl(handle, FsctlLockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                Log.Warn($"Drive eject for {letter} couldn't lock the volume — still in use, leaving it mounted");
                return false;
            }
            DeviceIoControl(handle, FsctlDismountVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            return DeviceIoControl(handle, IoctlStorageEjectMedia, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Drive eject failed for {letter}: {ex.Message}");
            return false;
        }
    }
}
