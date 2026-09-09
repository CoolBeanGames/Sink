using System.IO;

namespace Sink.Services;

public sealed record IpodDevice(string Name, string RootPath, long CapacityBytes, long FreeBytes)
{
    private static string Human(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    public string CapacityText => Human(CapacityBytes);
    public string FreeText => Human(FreeBytes);
    public string Summary => $"{Name} · {CapacityText} · {FreeText} free";
}

/// <summary>
/// Detects a physically connected iPod. An iPod classic mounts as a USB mass
/// storage volume with an "iPod_Control" folder at its root.
/// </summary>
public static class IpodService
{
    public static IpodDevice? Detect()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (IOException) { return null; }

        foreach (var drive in drives)
        {
            try
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType is not (DriveType.Removable or DriveType.Fixed)) continue;
                var control = Path.Combine(drive.RootDirectory.FullName, "iPod_Control");
                if (!Directory.Exists(control)) continue;
                var name = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "iPod" : drive.VolumeLabel;
                return new IpodDevice(name, drive.RootDirectory.FullName, drive.TotalSize, drive.AvailableFreeSpace);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return null;
    }
}
