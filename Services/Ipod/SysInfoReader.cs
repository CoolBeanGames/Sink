using System.IO;
using System.Xml.Linq;

namespace Sink.Services.Ipod;

public sealed record IpodSysInfo(
    string? ModelNumber,
    string? SerialNumber,
    string? FirmwareVersion,
    long CapacityBytes);

/// <summary>
/// Reads <c>iPod_Control/Device/SysInfo</c> (plain <c>Key: Value</c> lines) and
/// <c>SysInfoExtended</c> (an XML plist) for model, serial, firmware and capacity.
/// </summary>
public static class SysInfoReader
{
    public static IpodSysInfo? Read(string ipodRoot)
    {
        var deviceDir = Path.Combine(ipodRoot, "iPod_Control", "Device");
        string? model = null, serial = null, firmware = null;
        long capacity = 0;

        var sysInfo = Path.Combine(deviceDir, "SysInfo");
        if (File.Exists(sysInfo))
        {
            foreach (var line in File.ReadAllLines(sysInfo))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();
                switch (key)
                {
                    case "ModelNumStr": model = value.TrimStart('x', 'X'); break;
                    case "pszSerialNumber": serial ??= value; break;
                    case "buildID" or "VisibleBuildID": firmware ??= value; break;
                }
            }
        }

        var extended = Path.Combine(deviceDir, "SysInfoExtended");
        if (File.Exists(extended))
        {
            try
            {
                var plist = XDocument.Load(extended);
                var dict = PlistDict(plist);
                model ??= dict.GetValueOrDefault("ModelNumber");
                serial ??= dict.GetValueOrDefault("SerialNumber");
                firmware ??= dict.GetValueOrDefault("VisibleBuildID") ?? dict.GetValueOrDefault("BuildID");
                if (long.TryParse(dict.GetValueOrDefault("TotalCapacity"), out var cap) && cap > 0) capacity = cap;
            }
            catch { /* malformed plist — fall back to SysInfo values */ }
        }

        if (capacity == 0 && model is not null) capacity = CapacityForModel(model);

        if (model is null && serial is null && capacity == 0) return null;
        return new IpodSysInfo(model, serial, firmware, capacity);
    }

    private static Dictionary<string, string> PlistDict(XDocument doc)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dict = doc.Descendants("dict").FirstOrDefault();
        if (dict is null) return result;
        string? key = null;
        foreach (var el in dict.Elements())
        {
            if (el.Name == "key") key = el.Value.Trim();
            else if (key is not null) { result[key] = el.Value.Trim(); key = null; }
        }
        return result;
    }

    private static long CapacityForModel(string model)
    {
        // ModelNumber prefixes → nominal capacity (bytes, decimal GB as iTunes shows).
        const long gb = 1_000_000_000;
        var m = model.ToUpperInvariant();
        return m switch
        {
            _ when m.StartsWith("MA002") || m.StartsWith("MA146") => 30 * gb, // iPod video 30GB
            _ when m.StartsWith("MA003") || m.StartsWith("MA147") => 60 * gb, // iPod video 60GB
            _ when m.StartsWith("MA444") || m.StartsWith("MA446") => 30 * gb, // 5.5G 30GB
            _ when m.StartsWith("MA448") || m.StartsWith("MA450") => 80 * gb, // 5.5G 80GB
            _ when m.StartsWith("MB029") || m.StartsWith("MB147") => 80 * gb, // classic 6G 80GB
            _ when m.StartsWith("MB145") || m.StartsWith("MB150") => 160 * gb, // classic 6G 160GB
            _ when m.StartsWith("MC293") || m.StartsWith("MC297") => 160 * gb, // classic 7G 160GB
            _ => 0
        };
    }
}
