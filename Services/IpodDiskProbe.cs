using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sink.Services;

/// <summary>
/// Reads a raw physical disk's product strings and byte length directly through
/// Win32 IOCTLs. Unlike WMI's <c>Win32_DiskDrive.Size</c>, which comes back empty
/// for a Mac-formatted iPod classic, <c>IOCTL_DISK_GET_LENGTH_INFO</c> reports the
/// true capacity of a disk Windows cannot mount. Neither IOCTL needs elevation.
/// </summary>
internal static class IpodDiskProbe
{
    public sealed record DiskInfo(string Vendor, string Product, string Serial, long SizeBytes);

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    private const uint IOCTL_STORAGE_READ_CAPACITY = 0x002D5140;
    private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_SHARE_READ_WRITE = 0x00000003;

    public static DiskInfo? FindIpod()
    {
        for (var drive = 0; drive < 16; drive++)
        {
            var info = QueryWithTimeout(drive);
            if (info is null) continue;
            var haystack = $"{info.Vendor} {info.Product}";
            if (haystack.Contains("ipod", StringComparison.OrdinalIgnoreCase) ||
                info.Vendor.Contains("apple", StringComparison.OrdinalIgnoreCase))
                return info;
        }
        return null;
    }

    private static DiskInfo? QueryWithTimeout(int drive)
    {
        try
        {
            var task = Task.Run(() => Query(drive));
            return task.Wait(TimeSpan.FromSeconds(1.5)) ? task.Result : null;
        }
        catch
        {
            return null;
        }
    }

    private static DiskInfo? Query(int drive)
    {
        using var handle = CreateFileW($@"\\.\PhysicalDrive{drive}", 0, FILE_SHARE_READ_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;

        var (vendor, product, serial) = ReadDeviceStrings(handle);
        if (vendor.Length == 0 && product.Length == 0) return null;
        var size = ReadCapacity(handle);
        if (size <= 0) size = ReadLength(handle);
        if (size <= 0) size = ReadGeometry(handle);
        return new DiskInfo(vendor, product, serial, size);
    }

    /// <summary>SCSI READ CAPACITY through the USB mass-storage bus — answered by the iPod firmware regardless of filesystem.</summary>
    private static long ReadCapacity(SafeFileHandle handle)
    {
        var buf = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.WriteInt32(buf, 0, 32); // Version
            Marshal.WriteInt32(buf, 4, 32); // Size
            if (!DeviceIoControl(handle, IOCTL_STORAGE_READ_CAPACITY, buf, 32, buf, 32, out _, IntPtr.Zero))
                return 0;
            return Marshal.ReadInt64(buf, 24); // DiskLength
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static long ReadGeometry(SafeFileHandle handle)
    {
        var buf = Marshal.AllocHGlobal(24);
        try
        {
            if (!DeviceIoControl(handle, IOCTL_DISK_GET_DRIVE_GEOMETRY, IntPtr.Zero, 0, buf, 24, out _, IntPtr.Zero))
                return 0;
            var cylinders = Marshal.ReadInt64(buf, 0);
            var tracksPerCylinder = Marshal.ReadInt32(buf, 12);
            var sectorsPerTrack = Marshal.ReadInt32(buf, 16);
            var bytesPerSector = Marshal.ReadInt32(buf, 20);
            return cylinders * tracksPerCylinder * sectorsPerTrack * bytesPerSector;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static (string vendor, string product, string serial) ReadDeviceStrings(SafeFileHandle handle)
    {
        var query = new STORAGE_PROPERTY_QUERY { PropertyId = 0, QueryType = 0 };
        var buffer = Marshal.AllocHGlobal(1024);
        try
        {
            var queryBytes = StructToBytes(query);
            var queryPtr = Marshal.AllocHGlobal(queryBytes.Length);
            Marshal.Copy(queryBytes, 0, queryPtr, queryBytes.Length);
            try
            {
                if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, queryPtr, (uint)queryBytes.Length,
                        buffer, 1024, out _, IntPtr.Zero))
                    return ("", "", "");
            }
            finally { Marshal.FreeHGlobal(queryPtr); }

            var header = Marshal.PtrToStructure<STORAGE_DEVICE_DESCRIPTOR>(buffer);
            var raw = new byte[1024];
            Marshal.Copy(buffer, raw, 0, 1024);
            return (
                AnsiAt(raw, header.VendorIdOffset),
                AnsiAt(raw, header.ProductIdOffset),
                AnsiAt(raw, header.SerialNumberOffset));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static long ReadLength(SafeFileHandle handle)
    {
        var outBuf = Marshal.AllocHGlobal(8);
        try
        {
            return DeviceIoControl(handle, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, outBuf, 8, out _, IntPtr.Zero)
                ? Marshal.ReadInt64(outBuf)
                : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(outBuf);
        }
    }

    private static string AnsiAt(byte[] buffer, uint offset)
    {
        if (offset == 0 || offset >= buffer.Length) return "";
        var end = (int)offset;
        while (end < buffer.Length && buffer[end] != 0) end++;
        return Encoding.ASCII.GetString(buffer, (int)offset, end - (int)offset).Trim();
    }

    private static byte[] StructToBytes<T>(T value) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var bytes = new byte[size];
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            Marshal.Copy(ptr, bytes, 0, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_DEVICE_DESCRIPTOR
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
        public uint BusType;
        public uint RawPropertiesLength;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode,
        IntPtr inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize,
        out uint bytesReturned, IntPtr overlapped);
}
