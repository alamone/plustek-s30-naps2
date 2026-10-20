using System.ComponentModel;
using System.Diagnostics;
using S30ButtonBridge;

namespace ScanKiosk;

/// <summary>
/// Listens for the S30's physical Scan button (same mechanism as S30ButtonBridge: usbscan event pipe, with the
/// sweep that clears orphaned waits left by Plustek's driver) and keeps the scanner's standby disabled.
/// </summary>
public sealed class ButtonService : IDisposable
{
    readonly KioskConfig _cfg;
    readonly ManualResetEvent _stop = new(false);
    Thread? _thread;
    DateTime _last = DateTime.MinValue;

    public event Action? Pressed;
    /// <summary>(connected, message)</summary>
    public event Action<bool, string>? StatusChanged;

    public ButtonService(KioskConfig cfg) => _cfg = cfg;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "S30 button" };
        _thread.Start();
    }

    void Status(bool ok, string msg) => StatusChanged?.Invoke(ok, msg);

    void Run()
    {
        string? lastStatus = null;
        void Report(bool ok, string msg)
        {
            if (msg == lastStatus) return;
            lastStatus = msg;
            Log.Write("Scanner: " + msg);
            Status(ok, msg);
        }

        var sweep = _cfg.OrphanSweepSeconds > 0 ? TimeSpan.FromSeconds(_cfg.OrphanSweepSeconds) : Timeout.InfiniteTimeSpan;
        while (!_stop.WaitOne(0))
        {
            if (Process.GetProcessesByName("S30ButtonBridge").Length > 0)
            {
                // Two listeners would cancel each other's waits; let the user decide which one runs.
                Report(false, "Scan button disabled: S30ButtonBridge is running");
                _stop.WaitOne(5000);
                continue;
            }
            using var dev = UsbScanDevice.Find(Convert.ToUInt16(_cfg.UsbVid, 16), Convert.ToUInt16(_cfg.UsbPid, 16));
            if (dev == null)
            {
                Report(false, "Scanner not connected");
                _stop.WaitOne(2000);
                continue;
            }
            Report(true, "Scanner ready");
            try { dev.ResetEventPipe(); } catch (Exception ex) when (ex is Win32Exception or TimeoutException) { }
            if (_cfg.ScannerStandbyMinutes >= 0)
            {
                _stop.WaitOne(3000); // let the WIA service finish its own device init first
                try
                {
                    var reply = dev.SetStandby(_cfg.ScannerStandbyMinutes);
                    Log.Write($"Standby set to {_cfg.ScannerStandbyMinutes} min: {(UsbScanDevice.IsStandbyReplyOk(reply) ? "OK" : "unexpected reply")}");
                }
                catch (Exception ex) when (ex is Win32Exception or TimeoutException) { Log.Write($"Standby: {ex.Message}"); }
            }

            while (!_stop.WaitOne(0))
            {
                byte[]? pkt;
                bool swept;
                try { pkt = dev.WaitForEvent(_stop, sweep, out swept); }
                catch (Win32Exception ex)
                {
                    Log.Write($"Button wait failed ({ex.NativeErrorCode}); reconnecting");
                    break;
                }
                if (pkt == null) break;
                if (swept)
                {
                    if (Process.GetProcessesByName("S30ButtonBridge").Length > 0) break;
                    continue;
                }
                if (pkt.Length == 0) { Thread.Sleep(100); continue; }
                int? button = UsbScanDevice.ParseButton(pkt);
                Log.Write($"Scanner event {string.Join(' ', pkt.Select(b => b.ToString("X2")))}{(button != null ? $" -> button {button}" : "")}");
                if (button == null) continue;
                if (DateTime.UtcNow - _last < TimeSpan.FromSeconds(_cfg.ButtonDebounceSeconds)) continue;
                _last = DateTime.UtcNow;
                Pressed?.Invoke();
            }
            _stop.WaitOne(1000);
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(3000);
        _stop.Dispose();
    }
}
