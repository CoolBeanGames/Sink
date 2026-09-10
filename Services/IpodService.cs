using System.IO;
using System.Management;

namespace Sink.Services;

public sealed record IpodDevice(
    string Name,
    string? RootPath,
    long CapacityBytes,
    long FreeBytes,
    string? Model = null,
    string? Serial = null)
{
    private static string Human(long bytes)
    {
        if (bytes <= 0) return "unknown size";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    public string Key => RootPath ?? Serial ?? Name;
    public string CapacityText => Human(CapacityBytes);
    public string FreeText => Human(FreeBytes);

    /// <summary>Single-line summary for status text.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { Name };
            if (CapacityBytes > 0) parts.Add(CapacityText);
            if (FreeBytes > 0) parts.Add($"{FreeText} free");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Multi-line detail for the hover tooltip.</summary>
    public string Tooltip
    {
        get
        {
            var lines = new List<string> { Name };
            if (!string.IsNullOrWhiteSpace(Model) && !string.Equals(Model, Name, StringComparison.OrdinalIgnoreCase))
                lines.Add(Model!);
            if (CapacityBytes > 0)
                lines.Add(FreeBytes > 0
                    ? $"{CapacityText} · {FreeText} free"
                    : $"{CapacityText} capacity");
            if (RootPath is not null) lines.Add($"Mounted at {RootPath}");
            else lines.Add("Storage not mounted (Mac-formatted?)");
            if (!string.IsNullOrWhiteSpace(Serial)) lines.Add($"Serial {Serial}");
            return string.Join("\n", lines);
        }
    }
}

/// <summary>
/// Detects a physically connected iPod.
///
/// Primary detection is at the USB device level via WMI (Win32_PnPEntity), so
/// an iPod is found even when Windows cannot mount its volume — a Mac-formatted
/// iPod classic attaches as a disk with no readable filesystem and therefore no
/// drive letter. When a volume <em>is</em> mounted, the drive letter, label and
/// free space are read from it too.
///
/// Every filesystem/WMI probe runs under a short timeout: an empty card reader,
/// a dead network mapping, or slow disk-geometry queries can otherwise block for
/// a long time.
/// </summary>
public static class IpodService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(6);

    public static IpodDevice? Detect()
    {
        var mounted = TryMountedVolume();
        var usb = FindUsbIpod();

        if (mounted is not null)
            return mounted with { Model = usb?.Model ?? mounted.Model, Serial = usb?.Serial ?? mounted.Serial };

        if (usb is null) return null;

        var capacity = TryDiskCapacity(usb.PnpDeviceId);
        return new IpodDevice("iPod", null, capacity, 0, usb.Model, usb.Serial);
    }

    private sealed record UsbIpod(string Model, string? Serial, string PnpDeviceId);

    private static UsbIpod? FindUsbIpod()
    {
        return Probe(() =>
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%iPod%' OR DeviceID LIKE '%VEN_APPLE&PROD_IPOD%'");
            foreach (var obj in searcher.Get().OfType<ManagementObject>())
            {
                var name = (obj["Name"] as string) ?? "Apple iPod";
                var deviceId = (obj["DeviceID"] as string) ?? "";
                return new UsbIpod(name, ParseSerial(deviceId), deviceId);
            }
            return null;
        }, WmiTimeout);
    }

    private static string? ParseSerial(string deviceId)
    {
        var tail = deviceId.Split('\\').LastOrDefault();
        if (string.IsNullOrWhiteSpace(tail)) return null;
        var serial = tail.Split('&')[0].Trim();
        return string.IsNullOrWhiteSpace(serial) ? null : serial;
    }

    private static long TryDiskCapacity(string pnpDeviceId)
    {
        return RunBounded<long>(() =>
        {
            var escaped = pnpDeviceId.Replace("\\", "\\\\");
            using var searcher = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_PnPEntity.DeviceID='{escaped}'}} WHERE ResultClass=Win32_DiskDrive");
            foreach (var disk in searcher.Get().OfType<ManagementObject>())
                if (disk["Size"] is not null && long.TryParse(disk["Size"].ToString(), out var size))
                    return size;
            return 0;
        }, WmiTimeout, 0);
    }

    private static IpodDevice? TryMountedVolume()
    {
        string[] roots;
        try { roots = Directory.GetLogicalDrives(); }
        catch { return null; }

        foreach (var root in roots)
        {
            var device = Probe(() =>
            {
                if (!Directory.Exists(Path.Combine(root, "iPod_Control")) &&
                    !Directory.Exists(Path.Combine(root, "iTunes_Control"))) return null;
                var drive = new DriveInfo(root);
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "iPod" : drive.VolumeLabel;
                return new IpodDevice(label, root, drive.TotalSize, drive.AvailableFreeSpace);
            });
            if (device is not null) return device;
        }
        return null;
    }

    private static T? Probe<T>(Func<T?> work, TimeSpan? timeout = null) where T : class
    {
        try
        {
            var task = Task.Run(work);
            return task.Wait(timeout ?? ProbeTimeout) ? task.Result : null;
        }
        catch
        {
            return null;
        }
    }

    private static T RunBounded<T>(Func<T> work, TimeSpan timeout, T fallback)
    {
        try
        {
            var task = Task.Run(work);
            return task.Wait(timeout) ? task.Result : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
