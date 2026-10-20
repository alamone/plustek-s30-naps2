using System.Text.Json;

namespace ScanKiosk;

/// <summary>Settings from ScanKiosk.json next to the exe (all optional).</summary>
public sealed class KioskConfig
{
    public string Naps2ConsolePath { get; set; } = @"C:\Program Files\NAPS2\NAPS2.Console.exe";
    /// <summary>NAPS2 profiles file; empty = %APPDATA%\NAPS2\profiles.xml.</summary>
    public string ProfilesPath { get; set; } = "";
    /// <summary>Used when a profile has no NAPS2 Auto Save path. NAPS2 placeholders are supported.</summary>
    public string DefaultOutputPattern { get; set; } = @"%USERPROFILE%\Documents\Scans\$(YYYY)-$(MM)-$(DD)_$(nnn).pdf";
    /// <summary>Optional explicit tiles; empty = every NAPS2 profile for the S30, in NAPS2's order.</summary>
    public List<TileConfig> Tiles { get; set; } = new();
    public bool FullScreen { get; set; } = true;
    public int AutoReturnSeconds { get; set; } = 20;
    public double ExitHoldSeconds { get; set; } = 3;

    // physical button
    public bool ScanButtonEnabled { get; set; } = true;
    /// <summary>USB vendor/product id of the scanner (hex). Plustek SmartOffice S30 = 07B3/1927.</summary>
    public string UsbVid { get; set; } = "07B3";
    public string UsbPid { get; set; } = "1927";
    public int ScannerStandbyMinutes { get; set; } = 0;   // 0 = never, -1 = leave alone
    public double OrphanSweepSeconds { get; set; } = 2;
    public double ButtonDebounceSeconds { get; set; } = 3;

    public static KioskConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ScanKiosk.json");
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<KioskConfig>(File.ReadAllText(path), new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    PropertyNameCaseInsensitive = true,
                }) ?? new();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read {path}: {ex.Message}");
        }
        return new();
    }
}

public sealed class TileConfig
{
    public string Profile { get; set; } = "";   // NAPS2 profile display name
    public string? Label { get; set; }          // big text on the tile
    public string? Description { get; set; }    // small text; default is derived from the profile
}

/// <summary>Per-machine state that survives restarts (last preset tapped).</summary>
public sealed class KioskState
{
    public string? LastProfile { get; set; }

    static string PathName => System.IO.Path.Combine(Log.Dir, "state.json");

    public static KioskState Load()
    {
        try { return JsonSerializer.Deserialize<KioskState>(File.ReadAllText(PathName)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try { File.WriteAllText(PathName, JsonSerializer.Serialize(this)); }
        catch (Exception ex) { Log.Write($"Could not save state: {ex.Message}"); }
    }
}

public static class Log
{
    public static readonly string Dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanKiosk");
    static readonly object Lock = new();

    public static void Write(string msg)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}";
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var path = System.IO.Path.Combine(Dir, "kiosk.log");
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 2_000_000) File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (IOException) { }
        }
    }
}
