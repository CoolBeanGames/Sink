using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Sink.Services.Ipod;

public static unsafe class DeviceSysInfoReader
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint ShareRead = 1, ShareWrite = 2, OpenExisting = 3;
    private const uint IoctlStorageGetDeviceNumber = 0x002D1080, IoctlScsiPassThrough = 0x0004D004;

    public static string Read(string rootPath)
    {
        var drivePath = @"\\.\" + rootPath.TrimEnd(Path.DirectorySeparatorChar);
        uint physicalNumber;
        using (var volume = CreateFile(drivePath, GenericRead, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
        {
            if (volume.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the iPod volume.");
            var number = new StorageDeviceNumber(); uint returned;
            if (!DeviceIoControl(volume, IoctlStorageGetDeviceNumber, null, 0, &number, (uint)sizeof(StorageDeviceNumber), out returned, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not locate the physical iPod device.");
            physicalNumber = number.DeviceNumber;
        }

        using var device = CreateFile(@"\\.\PhysicalDrive" + physicalNumber, GenericRead | GenericWrite, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (device.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Administrator access is required to query this iPod.");
        var pages = Query(device, 0xC0);
        if (pages.Length < 5) throw new InvalidDataException("The iPod returned incomplete device information.");
        var firstPage = pages[4];
        var lastIndex = 3 + pages[3];
        if (lastIndex >= pages.Length) throw new InvalidDataException("The iPod returned an invalid device-information page list.");
        var lastPage = pages[lastIndex];
        using var output = new MemoryStream();
        for (var page = (int)firstPage; page <= lastPage; page++)
        {
            var data = Query(device, (byte)page);
            if (data.Length < 4) continue;
            var length = Math.Min(data[3], data.Length - 4);
            output.Write(data, 4, length);
        }
        if (output.Length == 0) throw new InvalidDataException("This iPod did not return extended device information.");
        return Encoding.UTF8.GetString(output.ToArray()).TrimEnd('\0');
    }

    private static byte[] Query(SafeFileHandle device, byte page)
    {
        var packet = new ScsiPacket();
        packet.PassThrough.Length = (ushort)sizeof(ScsiPassThrough);
        packet.PassThrough.TargetId = 1;
        packet.PassThrough.CdbLength = 6;
        packet.PassThrough.SenseInfoLength = 32;
        packet.PassThrough.DataIn = 1;
        packet.PassThrough.DataTransferLength = 255;
        packet.PassThrough.TimeOutValue = 2;
        var packetPointer = (byte*)&packet;
        packet.PassThrough.DataBufferOffset = (nuint)(packet.Data - packetPointer);
        packet.PassThrough.SenseInfoOffset = (uint)(packet.Sense - packetPointer);
        packet.PassThrough.Cdb[0] = 0x12;
        packet.PassThrough.Cdb[1] = 1;
        packet.PassThrough.Cdb[2] = page;
        packet.PassThrough.Cdb[4] = 255;
        var outputSize = (uint)(packet.Data - packetPointer + 255);
        uint returned;
        if (!DeviceIoControl(device, IoctlScsiPassThrough, &packet, (uint)sizeof(ScsiPassThrough), &packet, outputSize, out returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The iPod device-information query failed.");
        var result = new byte[255];
        for (var i = 0; i < result.Length; i++) result[i] = packet.Data[i];
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceNumber { public uint DeviceType, DeviceNumber, PartitionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScsiPassThrough
    {
        public ushort Length; public byte ScsiStatus, PathId, TargetId, Lun, CdbLength, SenseInfoLength, DataIn;
        public uint DataTransferLength, TimeOutValue; public nuint DataBufferOffset; public uint SenseInfoOffset;
        public fixed byte Cdb[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScsiPacket { public ScsiPassThrough PassThrough; public uint Filler; public fixed byte Sense[32]; public fixed byte Data[255]; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, void* input, uint inputSize, void* output, uint outputSize, out uint returned, IntPtr overlapped);
}
