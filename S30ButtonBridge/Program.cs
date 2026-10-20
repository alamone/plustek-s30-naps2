using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace S30ButtonBridge;

/// <summary>
/// Plustek SmartOffice S30 (USB 07B3:1927, GL310 chip) scan button -> NAPS2.
///
/// Reverse-engineered from ScanApi2_gl310_x64.dll (Plustek DocAction II / WIA driver backend):
///  - Button: IOCTL_WAIT_ON_DEVICE_EVENT on the usbscan device; one 4-byte packet per press
///    (S30 sends 04 00 01 B1 for its Scan button). See ParseButton.
///  - Standby: bulk command "PWR\0" + type(0=scanner sleep, 1=lamp off) + minutes%60 + minutes/60 + 0,
///    reply starts with "STA". 0 minutes = standby disabled (what Scanner Utility's "POWERSAVE 0" sends).
/// NAPS2 is triggered the same way a WIA/STI button event would: NAPS2.exe /StiDevice:&lt;WIA id&gt;,
/// which forwards ScanWithDevice to an already-running NAPS2 window via its named pipe.
/// </summary>
static class Program
{
    static ushort Vid = 0x07B3, Pid = 0x1927;  // overridable via UsbVid / UsbPid in the config
    const string StiScanEvent = "{a6c5a715-8c6e-11d2-977a-0000f87a926f}"; // WIA_EVENT_SCAN_IMAGE
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "S30ButtonBridge";

    static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    static readonly string LogPath = Path.Combine(DataDir, "bridge.log");
    static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "S30ButtonBridge.json");
    static bool _console;

    static int Main(string[] args)
    {
        Directory.CreateDirectory(DataDir);
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (cmd is "--probe" or "--orphan-test" or "--set-sleep" or "--install" or "--uninstall" or "--help" or "-h" or "/?")
        {
            if (!Native.AttachConsole(-1)) Native.AllocConsole();
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            _console = true;
            Console.WriteLine();
        }

        var cfg = Config.Load(ConfigPath);
        Vid = Convert.ToUInt16(cfg.UsbVid, 16);
        Pid = Convert.ToUInt16(cfg.UsbPid, 16);
        try
        {
            switch (cmd)
            {
                case "--probe": return Probe(reset: !args.Contains("--no-reset", StringComparer.OrdinalIgnoreCase));
                case "--orphan-test": return OrphanTest();
                case "--set-sleep": return SetSleep(args.Length > 1 ? int.Parse(args[1]) : 0);
                case "--install": return Install();
                case "--uninstall": return Uninstall();
                case "": return Run(cfg);
                default:
                    Console.WriteLine("""
                        S30ButtonBridge - Plustek S30 scan button -> NAPS2
                          (no args)          run in background (log: %LOCALAPPDATA%\S30ButtonBridge\bridge.log)
                          --probe            print raw interrupt packets from the scanner (close DocAction II first)
                          --set-sleep <min>  set scanner standby timeout now (0 = never)
                          --install          start automatically at logon (HKCU Run)
                          --uninstall        remove the logon entry
                        """);
                    return 0;
            }
        }
        catch (Exception ex)
        {
            Log("Fatal: " + ex);
            return 1;
        }
    }

    // ---------------------------------------------------------------- run mode

    static int Run(Config cfg)
    {
        using var single = new Mutex(true, @"Local\" + AppName, out bool first);
        if (!first) { Log("Another instance is already running; exiting."); return 0; }

        string? wiaId = cfg.WiaDeviceId ?? FindWiaDeviceId();
        Log($"Started. NAPS2={cfg.Naps2Path} WiaDeviceId={wiaId ?? "(not found)"} SleepMinutes={cfg.SleepMinutes} OrphanSweepSeconds={cfg.OrphanSweepSeconds}");
        if (Process.GetProcessesByName("DocAction II").Length > 0)
            Log("Warning: DocAction II is running and will compete for button events. Quit it / remove it from startup.");

        using var stop = new ManualResetEvent(false);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { Log("Exiting."); stop.Set(); };
        DateTime lastTrigger = DateTime.MinValue;
        bool loggedMissing = false;

        while (!stop.WaitOne(0))
        {
            using var dev = UsbScanDevice.Find(Vid, Pid);
            if (dev == null)
            {
                if (!loggedMissing) Log("Scanner not connected; waiting...");
                loggedMissing = true;
                stop.WaitOne(2000);
                continue;
            }
            loggedMissing = false;
            Log($"Connected: {dev.Path} (bcdDevice {dev.BcdDevice:X4}, interrupt packet {dev.InterruptPacketSize} bytes)");
            TryResetEventPipe(dev);

            if (cfg.SleepMinutes >= 0)
            {
                // Let the WIA service finish its own init (it pushes its ini's ScannerSleepTime) before overriding.
                stop.WaitOne(TimeSpan.FromSeconds(cfg.SleepApplyDelaySeconds));
                TryApplySleep(dev, cfg.SleepMinutes);
            }

            int fastErrors = 0;
            var sweep = cfg.OrphanSweepSeconds > 0 ? TimeSpan.FromSeconds(cfg.OrphanSweepSeconds) : Timeout.InfiniteTimeSpan;
            while (!stop.WaitOne(0))
            {
                byte[]? pkt;
                bool swept;
                try { pkt = dev.WaitForEvent(stop, sweep, out swept); }
                catch (Win32Exception ex)
                {
                    Log($"Device wait failed ({ex.NativeErrorCode}: {ex.Message}); reconnecting.");
                    break;
                }
                if (pkt == null) break;
                if (swept) continue;  // periodic clean-up of the driver's orphaned waits; nothing arrived
                if (pkt.Length == 0)
                {
                    if (++fastErrors > 50) { Log("Too many empty events; reconnecting."); break; }
                    Thread.Sleep(100);
                    continue;
                }
                fastErrors = 0;

                int? button = UsbScanDevice.ParseButton(pkt);
                Log($"Event {Hex(pkt)}{(button is int b ? $" -> button {b}" : "")}");
                if (button == null || !cfg.Buttons.Contains(button.Value)) continue;

                if (DateTime.UtcNow - lastTrigger < TimeSpan.FromSeconds(cfg.DebounceSeconds))
                {
                    Log("Ignored (debounce).");
                    continue;
                }
                lastTrigger = DateTime.UtcNow;
                TriggerNaps2(cfg, wiaId ??= FindWiaDeviceId());
            }
            stop.WaitOne(1000);
        }
        return 0;
    }

    static void TriggerNaps2(Config cfg, string? wiaId)
    {
        if (wiaId == null) { Log("Cannot trigger NAPS2: WIA device id unknown (set WiaDeviceId in config)."); return; }
        try
        {
            // A cold-started "NAPS2.exe /StiDevice:..." opens its window but doesn't scan, whereas a request
            // forwarded to an already-running window works. So start NAPS2 first and wait until it is ready.
            if (!Naps2Ready())
            {
                Log("NAPS2 is not running; starting it first.");
                using (Process.Start(new ProcessStartInfo(cfg.Naps2Path) { UseShellExecute = false })) { }
                var sw = Stopwatch.StartNew();
                while (!Naps2Ready() && sw.Elapsed < TimeSpan.FromSeconds(cfg.Naps2StartTimeoutSeconds))
                    Thread.Sleep(250);
                if (Naps2Ready())
                {
                    Thread.Sleep(TimeSpan.FromSeconds(cfg.Naps2SettleSeconds));
                    Log($"NAPS2 ready after {sw.Elapsed.TotalSeconds:F1}s.");
                }
                else Log("NAPS2 did not become ready in time; sending the scan request anyway.");
            }

            var psi = new ProcessStartInfo(cfg.Naps2Path) { UseShellExecute = false };
            psi.ArgumentList.Add("/StiDevice:" + wiaId);
            psi.ArgumentList.Add("/StiEvent:" + StiScanEvent);
            using var p = Process.Start(psi);
            Log($"Triggered NAPS2 scan (pid {p?.Id}).");
        }
        catch (Exception ex) { Log("Failed to start NAPS2: " + ex.Message); }
    }

    /// <summary>True when a NAPS2 window is running and accepting requests: it serves \\.\pipe\NAPS2_PIPE_v2_&lt;pid&gt;.</summary>
    static bool Naps2Ready()
    {
        try
        {
            foreach (var pipe in Directory.GetFiles(@"\\.\pipe\"))
            {
                var name = Path.GetFileName(pipe);
                if (!name.StartsWith("NAPS2_PIPE_v2_") || !int.TryParse(name["NAPS2_PIPE_v2_".Length..], out int pid))
                    continue;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (!p.HasExited) return true;
                }
                catch (ArgumentException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>The WIA device id NAPS2 stores in its profile, e.g. {6BDD1FC6-810F-11D0-BEC7-08002BE2092F}\0001.</summary>
    static string? FindWiaDeviceId()
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
                    m.Contains($"vid_{Vid:x4}&pid_{Pid:x4}", StringComparison.OrdinalIgnoreCase))
                    return k.GetValue("DeviceID") as string;
            }
            catch (System.Security.SecurityException) { }
        }
        return null;
    }

    // ---------------------------------------------------------------- standby

    static bool TryApplySleep(UsbScanDevice dev, int minutes)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var reply = dev.SetStandby(minutes);
                bool ok = UsbScanDevice.IsStandbyReplyOk(reply);
                Log($"Set standby to {(minutes == 0 ? "never" : minutes + " min")}: reply {Hex(reply)} {(ok ? "OK" : "unexpected")}");
                if (ok) return true;
            }
            catch (Exception ex) when (ex is Win32Exception or TimeoutException)
            {
                Log($"Set standby attempt {attempt} failed: {ex.Message}");
            }
            Thread.Sleep(2000);
        }
        return false;
    }

    static void TryResetEventPipe(UsbScanDevice dev)
    {
        try
        {
            dev.ResetEventPipe();
            Log("Event pipe reset.");
        }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException)
        {
            Log($"Event pipe reset failed: {ex.Message}");
        }
    }

    static int SetSleep(int minutes)
    {
        using var dev = UsbScanDevice.Find(Vid, Pid);
        if (dev == null) { Console.WriteLine("Scanner not found."); return 2; }
        return TryApplySleep(dev, minutes) ? 0 : 1;
    }

    // ---------------------------------------------------------------- probe

    static int Probe(bool reset)
    {
        if (Process.GetProcessesByName("DocAction II").Length > 0)
            Console.WriteLine("WARNING: DocAction II is running; it will swallow most button packets. Quit it first.");
        using var dev = UsbScanDevice.Find(Vid, Pid);
        if (dev == null) { Console.WriteLine("Scanner 07B3:1927 not found on any \\\\.\\UsbscanN."); return 2; }
        Console.WriteLine($"Opened {dev.Path}  VID={dev.VendorId:X4} PID={dev.ProductId:X4} bcd={dev.BcdDevice:X4} " +
                          $"interrupt packet={dev.InterruptPacketSize}");
        Console.WriteLine($"WIA device id: {FindWiaDeviceId() ?? "(not found)"}");
        if (reset) TryResetEventPipe(dev);
        else Console.WriteLine("Event pipe NOT reset (--no-reset).");
        Console.WriteLine("Press the scanner button(s). Ctrl+C to quit.");

        using var stop = new ManualResetEvent(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        while (true)
        {
            var pkt = dev.WaitForEvent(stop);
            if (pkt == null) return 0;
            int? b = UsbScanDevice.ParseButton(pkt);
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {Hex(pkt)}{(b is int n ? $"   <- button {n}" : "")}");
            Log("Probe " + Hex(pkt));
        }
    }

    // ---------------------------------------------------------------- orphan test

    /// <summary>
    /// Reproduces the lost-press bug: Plustek's driver polls the button with a zero-timeout event wait and
    /// never cancels it, leaving an orphaned wait that swallows the next press. Handle A plays the driver,
    /// handle B the bridge. Then checks whether cancelling / resetting the event pipe from B clears A's orphan.
    /// </summary>
    static int OrphanTest()
    {
        using var a = UsbScanDevice.Find(Vid, Pid);
        using var b = UsbScanDevice.Find(Vid, Pid);
        if (a == null || b == null) { Console.WriteLine("Scanner not found."); return 2; }
        var orphans = new List<UsbScanDevice.PendingWait>();
        UsbScanDevice.PendingWait? mine = null;
        try
        {
            Step("PART 1 - reproduce: an orphaned wait (like the WIA driver leaves) is queued before ours");
            orphans.Add(a.BeginWait()); Thread.Sleep(300);
            mine = b.BeginWait();
            string r1 = PressAndSee(orphans, ref mine, b);
            Console.WriteLine($"  Result: {r1}");
            Log($"Orphan test part 1: {r1}");

            foreach (var (name, fix) in new (string, Action)[] {
                         ("cancel all event waits (IOCTL_CANCEL_IO)", b.CancelEventPipe),
                         ("reset the event pipe (IOCTL_RESET_PIPE)", b.ResetEventPipe) })
            {
                Step($"PART - fix attempt: {name} from the bridge's handle");
                orphans.Add(a.BeginWait()); Thread.Sleep(300);
                if (mine == null || mine.Done) { mine?.Dispose(); mine = b.BeginWait(); Thread.Sleep(300); }
                int before = orphans.Count(o => !o.Done);
                try { fix(); Console.WriteLine("  Fix call succeeded."); }
                catch (Exception ex) { Console.WriteLine($"  Fix call failed: {ex.Message}"); }
                Thread.Sleep(500);
                int after = orphans.Count(o => !o.Done);
                string cleared = $"orphans pending before {before}, after {after}; our wait {(mine.Done ? "was cancelled too" : "still pending")}";
                Console.WriteLine($"  {cleared}");
                if (mine.Done) { mine.Dispose(); mine = b.BeginWait(); }
                string r = PressAndSee(orphans, ref mine, b);
                Console.WriteLine($"  Result: {r}");
                Log($"Orphan test, {name}: {cleared}; {r}");
                if (r.StartsWith("BRIDGE")) break;
            }
        }
        finally
        {
            foreach (var o in orphans) o.Dispose();
            mine?.Dispose();
        }
        Console.WriteLine();
        Console.WriteLine("Test finished.");
        return 0;
    }

    static void Step(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
    }

    static string PressAndSee(List<UsbScanDevice.PendingWait> orphans, ref UsbScanDevice.PendingWait? mine, UsbScanDevice b)
    {
        mine ??= b.BeginWait();
        Console.WriteLine("  >>> Press the Scan button ONCE now <<<");
        Console.Beep(880, 250);
        var handles = orphans.Where(o => !o.Done).Select(o => o.Handle).Append(mine.Handle).ToArray();
        int i = WaitHandle.WaitAny(handles, TimeSpan.FromSeconds(45));
        if (i == WaitHandle.WaitTimeout) return "nothing received within 45 s";
        Thread.Sleep(200);
        if (mine.Done)
        {
            var pkt = mine.Result(out int err);
            return pkt != null ? $"BRIDGE received the press ({Hex(pkt)})" : $"bridge wait ended with error {err}";
        }
        var got = orphans.First(o => o.Done && o.Result(out _) != null);
        return $"LOST - the orphaned wait swallowed the press ({Hex(got.Result(out _)!)})";
    }

    // ---------------------------------------------------------------- install

    static int Install()
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        string exe = Environment.ProcessPath!;
        k.SetValue(AppName, $"\"{exe}\"");
        Console.WriteLine($"Added logon entry: {exe}");
        return 0;
    }

    static int Uninstall()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        k?.DeleteValue(AppName, throwOnMissingValue: false);
        Console.WriteLine("Removed logon entry.");
        return 0;
    }

    // ---------------------------------------------------------------- util

    static string Hex(byte[] b) => string.Join(' ', b.Select(x => x.ToString("X2")));

    static readonly object LogLock = new();
    static void Log(string msg)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}";
        if (_console) Console.WriteLine(line);
        lock (LogLock)
        {
            try
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(LogPath, LogPath + ".old", overwrite: true);
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException) { }
        }
    }
}

sealed class Config
{
    public string Naps2Path { get; set; } = @"C:\Program Files\NAPS2\NAPS2.exe";
    /// <summary>USB vendor/product id of the scanner (hex). Plustek SmartOffice S30 = 07B3/1927.</summary>
    public string UsbVid { get; set; } = "07B3";
    public string UsbPid { get; set; } = "1927";
    /// <summary>WIA device id as stored in NAPS2's profile; null = auto-detect from the registry.</summary>
    public string? WiaDeviceId { get; set; }
    /// <summary>Which hardware buttons trigger a scan.</summary>
    public int[] Buttons { get; set; } = { 1, 2 };
    public double DebounceSeconds { get; set; } = 3;
    /// <summary>When NAPS2 isn't running: how long to wait for it to start, then how long to let it settle.</summary>
    public double Naps2StartTimeoutSeconds { get; set; } = 30;
    public double Naps2SettleSeconds { get; set; } = 2;
    /// <summary>
    /// How often to abort orphaned event waits left by Plustek's driver (each would swallow one press after a
    /// scan). 0 disables.
    /// </summary>
    public double OrphanSweepSeconds { get; set; } = 2;
    /// <summary>Standby timeout to apply on every (re)connect. 0 = never sleep, -1 = leave alone.</summary>
    public int SleepMinutes { get; set; } = 0;
    public double SleepApplyDelaySeconds { get; set; } = 5;

    public static Config Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(path),
                    new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? new();
        }
        catch (Exception) { }
        return new();
    }
}
