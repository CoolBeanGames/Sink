using System.IO;
using System.Management;

namespace Sink.Services;

public sealed record IpodDevice(string Name, string? RootPath, long CapacityBytes, long FreeBytes)
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

    public string Summary
    {
        get
        {
            var parts = new List<string> { Name };
            if (CapacityBytes > 0) parts.Add(CapacityText);
            if (FreeBytes > 0) parts.Add($"{FreeText} free");
            else if (RootPath is null) parts.Add("connected");
            return string.Join(" · ", parts);
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
/// Every filesystem probe runs under a short timeout: an empty card reader or a
/// dead network mapping can make <see cref="DriveInfo"/> / <see cref="Directory"/>
/// calls block for minutes.
/// </summary>
public static class IpodService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    public static IpodDevice? Detect()
    {
        var mounted = TryMountedVolume();
        if (mounted is not null) return mounted;

        return IsIpodPluggedIn() ? new IpodDevice("iPod", null, 0, 0) : null;
    }

    /// <summary>True if any USB device that looks like an iPod is attached.</summary>
    private static bool IsIpodPluggedIn()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%iPod%' OR DeviceID LIKE '%VEN_APPLE&PROD_IPOD%'");
            return searcher.Get().Count > 0;
        }
        catch
        {
            return false;
        }
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

    private static T? Probe<T>(Func<T?> work) where T : class
    {
        try
        {
            var task = Task.Run(work);
            return task.Wait(ProbeTimeout) ? task.Result : null;
        }
        catch
        {
            return null;
        }
    }
}
