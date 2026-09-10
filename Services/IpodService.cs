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

    public string Key => RootPath ?? Name;
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
                lines.Add(FreeBytes > 0 ? $"{CapacityText} · {FreeText} free" : $"{CapacityText} capacity");
            if (RootPath is not null)
                lines.Add($"Mounted at {RootPath}");
            else if (CapacityBytes == 0)
                lines.Add("Capacity unavailable — Windows can't read a Mac-formatted iPod");
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

        var disk = usb is not null || mounted is not null
            ? RunBounded(IpodDiskProbe.FindIpod, TimeSpan.FromSeconds(12), null)
            : null;

        if (mounted is not null)
            return mounted with
            {
                CapacityBytes = mounted.CapacityBytes > 0 ? mounted.CapacityBytes : disk?.SizeBytes ?? 0,
                Model = DescribeModel(disk) ?? usb?.Model ?? mounted.Model,
                Serial = PickSerial(disk?.Serial, usb?.Serial, mounted.Serial)
            };

        if (usb is null && disk is null) return null;

        var name = usb?.Model is { Length: > 0 } and not "iPod" ? usb!.Model : "iPod";
        return new IpodDevice(
            name, null,
            disk?.SizeBytes ?? 0, 0,
            DescribeModel(disk),
            PickSerial(disk?.Serial, usb?.Serial, null));
    }

    private static string? DescribeModel(IpodDiskProbe.DiskInfo? disk)
    {
        if (disk is null) return null;
        var text = $"{disk.Vendor} {disk.Product}".Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? PickSerial(string? diskSerial, string? usbSerial, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(diskSerial)) return diskSerial;
        if (!string.IsNullOrWhiteSpace(usbSerial)) return usbSerial;
        return fallback;
    }

    private sealed record UsbIpod(string Model, string? Serial, string PnpDeviceId);

    // Apple USB product IDs (VID 05AC) for iPod-family devices.
    private static readonly Dictionary<string, string> IpodModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1201"] = "iPod", ["1203"] = "iPod (3rd gen)", ["1204"] = "iPod mini",
        ["1205"] = "iPod mini (2nd gen)", ["1207"] = "iPod (4th gen)",
        ["1209"] = "iPod (5th gen / classic)", ["120a"] = "iPod nano",
        ["1223"] = "iPod nano (2nd gen)", ["1224"] = "iPod shuffle (2nd gen)",
        ["1240"] = "iPod nano (3rd gen)", ["1250"] = "iPod nano (4th gen)",
        ["1260"] = "iPod nano (5th gen)", ["1261"] = "iPod classic",
        ["1262"] = "iPod nano (6th gen)", ["1263"] = "iPod nano (7th gen)",
        ["1265"] = "iPod shuffle (4th gen)", ["1266"] = "iPod nano (5th gen)",
        ["1300"] = "iPod shuffle (3rd gen)", ["1301"] = "iPod touch",
    };

    private static UsbIpod? FindUsbIpod()
    {
        return Probe(() =>
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%iPod%' OR DeviceID LIKE '%VEN_APPLE&PROD_IPOD%' OR DeviceID LIKE '%VID_05AC&PID_12%'");
            string? model = null, serial = null, deviceId = null;
            foreach (var obj in searcher.Get().OfType<ManagementObject>())
            {
                var id = (obj["DeviceID"] as string) ?? "";
                var name = (obj["Name"] as string) ?? "";
                deviceId ??= id;
                serial ??= ParseSerial(id);
                var byPid = ModelFromPid(id);
                if (byPid is not null) model = byPid;
                else if (model is null && name.Contains("iPod", StringComparison.OrdinalIgnoreCase)) model = name;
            }
            return deviceId is null ? null : new UsbIpod(model ?? "iPod", serial, deviceId);
        }, WmiTimeout);
    }

    private static string? ModelFromPid(string deviceId)
    {
        var marker = deviceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (marker < 0 || marker + 8 > deviceId.Length) return null;
        var pid = deviceId.Substring(marker + 4, 4);
        return IpodModels.GetValueOrDefault(pid);
    }

    private static string? ParseSerial(string deviceId)
    {
        var tail = deviceId.Split('\\').LastOrDefault();
        if (string.IsNullOrWhiteSpace(tail)) return null;
        var serial = tail.Split('&')[0].Trim();
        return string.IsNullOrWhiteSpace(serial) || serial.Length < 6 ? null : serial;
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
