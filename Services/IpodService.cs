using System.IO;
using System.Management;

namespace Sink.Services;

public sealed record IpodDevice(
    string RootPath,
    string Name,
    long CapacityBytes,
    long FreeBytes,
    string? Model = null,
    string? Serial = null,
    string? LibraryRoot = null)
{
    public string Key => RootPath;

    /// <summary>True when the iPod's iTunes database is present and readable.</summary>
    public bool CanReadDatabase => LibraryRoot is not null;

    public string CapacityText => Bytes(CapacityBytes);
    public string FreeText => Bytes(FreeBytes);

    public string Summary => CapacityBytes > 0
        ? $"{Name} · {CapacityText} · {FreeText} free"
        : Name;

    public string Tooltip
    {
        get
        {
            var lines = new List<string> { Name };
            if (!string.IsNullOrWhiteSpace(Model)) lines.Add($"Model {Model}");
            if (CapacityBytes > 0) lines.Add($"{CapacityText} · {FreeText} free");
            if (CanReadDatabase) lines.Add("Library readable");
            else if (LibraryRoot is null && CapacityBytes == 0)
                lines.Add("Connected, but Windows can't read this iPod — restore it on Windows to sync");
            else lines.Add("iTunes database not found");
            if (!string.IsNullOrWhiteSpace(Serial)) lines.Add($"Serial {Serial}");
            return string.Join("\n", lines);
        }
    }

    // Decimal units — matches how iTunes and Apple state iPod capacity.
    private static string Bytes(long value)
    {
        if (value <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = value;
        var u = 0;
        while (v >= 1000 && u < units.Length - 1) { v /= 1000; u++; }
        return $"{v:0.#} {units[u]}";
    }
}

/// <summary>
/// Finds a connected iPod: a mounted volume with an <c>iPod_Control</c> folder.
/// A stock-OS iPod set up on Windows is a FAT32 drive with a letter, so a plain
/// drive scan is all that is needed.
/// </summary>
public static class IpodService
{
    public static IpodDevice? Detect()
    {
        var over = TryOverrideRoot();
        if (over is not null) return over;

        // Enumerate drive *letters* (instant) and probe each for iPod_Control.
        // Reading DriveInfo metadata up front can block for a long time on an
        // unresponsive card reader; Directory.Exists just returns false.
        foreach (var root in SafeGetLogicalDrives())
        {
            try
            {
                if (!Directory.Exists(Path.Combine(root, "iPod_Control"))) continue;
                return Build(root, null);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // No mounted iPod_Control volume. The iPod may still be physically
        // connected but unreadable by Windows (Mac/HFS+ format, or MTP mode).
        // Fall back to a USB-device probe so the UI can still show it.
        return FindUsbIpod();
    }

    // Apple USB product IDs (VID 05AC) for iPod-family devices.
    private static readonly Dictionary<string, string> IpodModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1201"] = "iPod", ["1203"] = "iPod (3rd gen)", ["1204"] = "iPod mini",
        ["1205"] = "iPod mini (2nd gen)", ["1207"] = "iPod (4th gen)",
        ["1209"] = "iPod (5th gen)", ["120a"] = "iPod nano",
        ["1223"] = "iPod nano (2nd gen)", ["1224"] = "iPod shuffle (2nd gen)",
        ["1240"] = "iPod nano (3rd gen)", ["1250"] = "iPod nano (4th gen)",
        ["1260"] = "iPod nano (5th gen)", ["1261"] = "iPod classic",
        ["1262"] = "iPod nano (6th gen)", ["1263"] = "iPod nano (7th gen)",
        ["1265"] = "iPod shuffle (4th gen)", ["1266"] = "iPod nano (5th gen)",
        ["1300"] = "iPod shuffle (3rd gen)", ["1301"] = "iPod touch",
    };

    /// <summary>
    /// WMI probe at the USB level: finds an Apple iPod even when its volume is
    /// not mounted. Bounded so a slow WMI service can't hang the poll.
    /// </summary>
    private static IpodDevice? FindUsbIpod()
    {
        try
        {
            var task = Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%iPod%' " +
                    "OR DeviceID LIKE '%VEN_APPLE&PROD_IPOD%' OR DeviceID LIKE '%VID_05AC&PID_12%'");
                foreach (var obj in searcher.Get().OfType<ManagementObject>())
                {
                    var id = (obj["DeviceID"] as string) ?? "";
                    var name = (obj["Name"] as string) ?? "";
                    var model = ModelFromPid(id)
                        ?? (name.Contains("iPod", StringComparison.OrdinalIgnoreCase) ? name : "iPod");
                    var serial = ParseSerial(id);
                    return new IpodDevice(id.Length > 0 ? id : "usb-ipod", model, 0, 0, model, serial);
                }
                return null;
            });
            return task.Wait(TimeSpan.FromSeconds(6)) ? task.Result : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ModelFromPid(string deviceId)
    {
        var marker = deviceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (marker < 0 || marker + 8 > deviceId.Length) return null;
        return IpodModels.GetValueOrDefault(deviceId.Substring(marker + 4, 4));
    }

    private static string? ParseSerial(string deviceId)
    {
        var tail = deviceId.Split('\\').LastOrDefault();
        if (string.IsNullOrWhiteSpace(tail)) return null;
        var serial = tail.Split('&')[0].Trim();
        return serial.Length < 6 ? null : serial;
    }

    private static IpodDevice Build(string root, string? labelOverride)
    {
        long capacity = 0, free = 0;
        string? label = labelOverride;
        try
        {
            var drive = new DriveInfo(root);
            capacity = drive.TotalSize;
            free = drive.AvailableFreeSpace;
            label ??= drive.VolumeLabel;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var name = string.IsNullOrWhiteSpace(label) ? "iPod" : label!.Trim();
        var libraryRoot = Ipod.IpodReader.HasDatabase(root) ? root : null;
        var sysInfo = ReadSysInfo(root);
        return new IpodDevice(root, name, capacity, free, sysInfo.model, sysInfo.serial, libraryRoot);
    }

    private static (string? model, string? serial) ReadSysInfo(string root)
    {
        try
        {
            var file = Path.Combine(root, "iPod_Control", "Device", "SysInfo");
            if (!File.Exists(file)) return (null, null);
            string? model = null, serial = null;
            foreach (var line in File.ReadAllLines(file))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();
                if (key == "ModelNumStr") model = value.TrimStart('x', 'X');
                else if (key == "pszSerialNumber") serial = value;
            }
            return (model, serial);
        }
        catch (IOException) { return (null, null); }
        catch (UnauthorizedAccessException) { return (null, null); }
    }

    private static string[] SafeGetLogicalDrives()
    {
        try { return Directory.GetLogicalDrives(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Power-user / test hook: a folder path in <c>%AppData%/Sink/ipod-root.txt</c>
    /// is treated as a mounted iPod.
    /// </summary>
    private static IpodDevice? TryOverrideRoot()
    {
        try
        {
            var pointer = Path.Combine(LibraryStore.Directory, "ipod-root.txt");
            if (!File.Exists(pointer)) return null;
            var root = File.ReadAllText(pointer).Trim();
            if (root.Length == 0 || !Directory.Exists(root)) return null;
            return Build(root, new DirectoryInfo(root).Name);
        }
        catch
        {
            return null;
        }
    }
}
