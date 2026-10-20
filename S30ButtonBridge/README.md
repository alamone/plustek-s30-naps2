# S30ButtonBridge

Makes the **Scan** button on a Plustek SmartOffice S30 (USB `07B3:1927`) start a scan in [NAPS2](https://www.naps2.com/), and keeps the scanner from going into standby. It runs in the background with no window.

Use it on a normal desktop PC alongside the NAPS2 window. For a touch-screen station, use [ScanKiosk](../ScanKiosk/README.md) instead. Never run both on one PC.

## Setup

1. **Install the Plustek SmartOffice S30 driver and NAPS2.** In NAPS2, create a profile using the **WIA** driver with the S30 (shown as *A3 ADF2 Scanner(311)*), and make it the **default** profile. The button scans with that profile.
2. **From an admin PowerShell, run the standby fix:**
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Fix-S30Standby.ps1
   ```
   It does three things:
   - Sets `ScannerSleepTime` to 0 (never) in every copy of the driver's settings file, including the WIA service's own copy. The originals are backed up as `*.bak`.
   - Removes DocAction II from startup, since it would compete for the button. The Run key is backed up to `DocAction_II-Run-backup.reg`. Pass `-KeepDocAction` to skip this.
   - Restarts the WIA service.

   Do one scan in NAPS2 **before** running the script, so the WIA service's settings file exists.
3. **Put `S30ButtonBridge.exe` somewhere permanent,** then register it for logon and start it:
   ```powershell
   .\S30ButtonBridge.exe --install
   .\S30ButtonBridge.exe
   ```
   `--uninstall` removes the logon entry.

**Log file:** `%LOCALAPPDATA%\S30ButtonBridge\bridge.log`.

## Commands

| Command | What it does |
|---|---|
| *(no args)* | Runs in the background. Waits for button presses and triggers NAPS2, and sets standby to "never" each time the scanner (re)connects. |
| `--install` / `--uninstall` | Turns starting at logon on or off (HKCU Run). |
| `--probe` | Opens a console that prints the raw packets the scanner sends. Close DocAction II and the bridge first. |
| `--set-sleep <min>` | Sends the standby timeout to the scanner right away. `0` = never. |
| `--orphan-test` | Reproduces the driver's lost-press bug and checks the fix. Interactive; press the button when asked. |

## Configuration: `S30ButtonBridge.json` next to the exe

Every setting is optional; see [`S30ButtonBridge.example.json`](S30ButtonBridge.example.json).

| Key | Default | Meaning |
|---|---|---|
| `Naps2Path` | `C:\Program Files\NAPS2\NAPS2.exe` | Location of NAPS2 |
| `WiaDeviceId` | auto-detected | The scanner's WIA device ID, as NAPS2 stores it in its profiles |
| `UsbVid`, `UsbPid` | `07B3`, `1927` | The scanner's USB IDs, for trying related Plustek models |
| `Buttons` | `[1, 2]` | Which button numbers trigger a scan |
| `DebounceSeconds` | `3` | Ignores repeat presses within this time |
| `SleepMinutes` | `0` | Standby timeout sent on each connect; `0` = never, `-1` = leave it alone |
| `SleepApplyDelaySeconds` | `5` | Waits this long after connecting before sending it, so the WIA service's own setup finishes first |
| `OrphanSweepSeconds` | `2` | How often to clear the driver's leftover waits (the lost-press fix); `0` = off |
| `Naps2StartTimeoutSeconds` | `30` | When NAPS2 isn't running: how long to wait for it to start |
| `Naps2SettleSeconds` | `2` | When NAPS2 isn't running: extra wait once it has started |

## How it works

The details are in [docs/REVERSE-ENGINEERING.md](../docs/REVERSE-ENGINEERING.md). In short:

- **Reading the button.** The bridge keeps an `IOCTL_WAIT_ON_DEVICE_EVENT` read pending on the scanner's usbscan device. A press arrives as `04 00 01 B1`.
- **Triggering NAPS2.** It then runs `NAPS2.exe /StiDevice:<WIA id>`, NAPS2's own scanner-button entry point. That hands the request to the NAPS2 window that's already open. If NAPS2 isn't running, the bridge starts it first and waits for its named pipe.
- **Fixing the lost-press bug.** Every 2 seconds with no press, the bridge aborts all waits on the event pipe, which clears the orphaned wait Plustek's driver leaves behind after each scan.
- **Keeping standby off.** On every connect, it sends the vendor `PWR` command with 0 minutes.

## Build

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```
