using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ScanKiosk;

public sealed record Naps2Profile(
    string Name, bool IsDefault, string? DeviceId, string? DeviceName, string Driver,
    string Resolution, string BitDepth, string PaperSource, bool MaxQuality, int Quality, string? AutoSavePath)
{
    /// <summary>"300 DPI · Color · Duplex · JPEG 95" style summary of the profile.</summary>
    public string Summary
    {
        get
        {
            var dpi = Regex.Match(Resolution, @"\d+").Value;
            string depth = BitDepth switch
            {
                "C24Bit" => "Color",
                "Grayscale" => "Grayscale",
                "BlackWhite" => "Black & white",
                _ => BitDepth,
            };
            string quality = MaxQuality ? "Lossless" : $"JPEG {Quality}";
            if (BitDepth == "BlackWhite") quality = "";
            return string.Join("  ·  ", new[] { dpi.Length > 0 ? dpi + " DPI" : "", depth, PaperSource, quality }
                .Where(s => s.Length > 0));
        }
    }
}

public static class Naps2Profiles
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NAPS2", "profiles.xml");

    public static List<Naps2Profile> Load(string? path)
    {
        path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
        var doc = XDocument.Load(path);
        return doc.Root!.Elements("ScanProfile").Select(p => new Naps2Profile(
            Name: (string?)p.Element("DisplayName") ?? "(unnamed)",
            IsDefault: (bool?)p.Element("IsDefault") ?? false,
            DeviceId: (string?)p.Element("Device")?.Element("ID"),
            DeviceName: (string?)p.Element("Device")?.Element("Name"),
            Driver: (string?)p.Element("DriverName") ?? "",
            Resolution: (string?)p.Element("Resolution") ?? "",
            BitDepth: (string?)p.Element("BitDepth") ?? "",
            PaperSource: (string?)p.Element("PaperSource") ?? "",
            MaxQuality: (bool?)p.Element("MaxQuality") ?? false,
            Quality: (int?)p.Element("Quality") ?? 75,
            AutoSavePath: (bool?)p.Element("EnableAutoSave") == true
                ? (string?)p.Element("AutoSaveSettings")?.Element("FilePath") : null)).ToList();
    }

    /// <summary>The S30's WIA device id (what NAPS2 stores as Device/ID), from the still-image class key.</summary>
    public static string? FindWiaId(string vid, string pid)
    {
        using var cls = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Class\{6bdd1fc6-810f-11d0-bec7-08002be2092f}");
        if (cls == null) return null;
        foreach (var name in cls.GetSubKeyNames())
        {
            try
            {
                using var k = cls.OpenSubKey(name);
                if (k?.GetValue("MatchingDeviceId") is string m &&
                    m.Contains($"vid_{vid}&pid_{pid}", StringComparison.OrdinalIgnoreCase))
                    return k.GetValue("DeviceID") as string;
            }
            catch (System.Security.SecurityException) { }
        }
        return null;
    }

    /// <summary>Tiles to show: the configured list, or every profile for the S30 (all profiles if none match).</summary>
    public static List<(Naps2Profile Profile, string Label, string Description)> Tiles(
        List<Naps2Profile> profiles, KioskConfig cfg)
    {
        var result = new List<(Naps2Profile, string, string)>();
        if (cfg.Tiles.Count > 0)
        {
            foreach (var t in cfg.Tiles)
            {
                var p = profiles.FirstOrDefault(x => x.Name.Equals(t.Profile, StringComparison.OrdinalIgnoreCase));
                if (p == null) { Log.Write($"Configured tile profile '{t.Profile}' not found in NAPS2"); continue; }
                result.Add((p, t.Label ?? FriendlyLabel(p.Name, profiles), t.Description ?? p.Summary));
            }
            return result;
        }
        var s30 = FindWiaId(cfg.UsbVid, cfg.UsbPid);
        var list = profiles.Where(p => s30 != null && string.Equals(p.DeviceId, s30, StringComparison.OrdinalIgnoreCase)).ToList();
        if (list.Count == 0) list = profiles;
        return list.Select(p => (p, FriendlyLabel(p.Name, list), p.Summary)).ToList();
    }

    /// <summary>"Plustek S30 (600DPI 95Q)" -> "600DPI 95Q" when all names share the "Plustek S30" prefix.</summary>
    static string FriendlyLabel(string name, List<Naps2Profile> all)
    {
        var m = Regex.Match(name, @"^(.*?)\s*\((.+)\)\s*$");
        if (m.Success && all.Count(p => p.Name.StartsWith(m.Groups[1].Value + " (", StringComparison.Ordinal)) > 1)
            return m.Groups[2].Value;
        return name;
    }
}
