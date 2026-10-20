# Setting up a touch-screen scanning kiosk

This turns a Windows tablet or laptop with a Plustek S30 attached into a scanning station:

- **Touch tiles:** one big tile per NAPS2 profile.
- **Physical button:** the scanner's Scan button repeats the last preset used.
- **Review screen:** after each scan, with *Add more pages* / *Done* / *Discard*.
- **No standby:** the scanner never drops off USB into standby.

It was designed on a Surface Book (3000 × 2000 at 200% scaling), and scales to other screens.

On a kiosk, **ScanKiosk replaces both the NAPS2 window and S30ButtonBridge**. Don't run the bridge on the same PC: two programs listening to the button cancel each other's listeners. ScanKiosk refuses to listen while the bridge is running, and shows that on screen.

## Steps

Do everything signed in as the account that will be logged in at the kiosk.

1. **Install the Plustek SmartOffice S30 driver** from Plustek's website. You don't need DocAction II; if it gets installed, step 5 stops it starting automatically.
2. **Install [NAPS2](https://www.naps2.com/)** (8.x).
3. **Create one NAPS2 profile per kiosk tile.** Use the **WIA** driver and pick the S30, which shows up as *A3 ADF2 Scanner(311)*. Turn on **Auto Save** with the file name pattern you want, for example `\\server\scans\$(YYYY)-$(MM)-$(DD)_$(nnn).pdf`. The kiosk saves each scan there.
   - Tiles appear in NAPS2's profile order.
   - Profile names like `Plustek S30 (300 DPI)` show as just "300 DPI".
   - You can also choose and label the tiles yourself in `ScanKiosk.json`; see the [ScanKiosk README](../ScanKiosk/README.md).
4. **Do one test scan in NAPS2.** That makes the Windows scanning service create its own copy of the driver's settings file, which step 5 needs to fix.
5. **From an admin PowerShell, run the standby fix:**
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Fix-S30Standby.ps1
   ```
   It turns off standby in every copy of the driver's settings (the originals are backed up as `*.bak`), removes DocAction II from startup, and restarts the WIA service.
6. **Copy the `ScanKiosk` folder somewhere permanent,** for example `C:\Tools\ScanKiosk`. Then register it for logon and start it:
   ```powershell
   & 'C:\Tools\ScanKiosk\ScanKiosk.exe' --install
   Start-Process 'C:\Tools\ScanKiosk\ScanKiosk.exe'
   ```
7. **Optional:** set the account to sign in automatically, so the kiosk recovers on its own after a reboot.

## Using it

- **Tap a tile to scan with that profile.** The tile you used last gets the **SCAN BUTTON** badge: the physical button now uses that preset.
- **On the review screen:**
  - **+ Add more pages** scans more pages into the same PDF.
  - **Done** returns to the start screen.
  - **Discard** moves the PDF into a `_discarded` folder next to the scans.

  The review screen returns to the start screen automatically after 20 seconds without a touch.
- **To exit the kiosk, press and hold the top-left corner for 3 seconds,** then confirm.

## Logs

- `%LOCALAPPDATA%\ScanKiosk\kiosk.log`: every scan, every NAPS2 message, and button events.
- `%LOCALAPPDATA%\ScanKiosk\state.json`: remembers the last preset.
