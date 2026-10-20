# Plustek SmartOffice S30: how it really works

These are the findings behind this repository. They come from analysing Plustek's driver DLLs and from testing on real hardware. They should help anyone fixing similar problems with this scanner, or with related Plustek models.

**Tested setup:**
- Scanner: Plustek SmartOffice S30, USB `07B3:1927`, firmware model `311U` (GL310 chip family).
- OS: Windows 11.
- Scanner driver: Plustek driver package 6.2.1.4.
- Plustek apps: DocAction II 2.3.0.
- NAPS2: 8.5.1.

None of Plustek's binaries are included here. Where specific addresses are given, they refer to the 64-bit `Scanapi2_gl310_x64.dll` that ships with DocAction II (Jan 2024). The copy installed with the S30 driver (June 2025) behaves the same.

## 1. Driver stack

```
NAPS2 ──WIA──> PkWiaDrv20-II_311_x64.dll ──┐
                (WIA driver, runs inside    ├──> Scanapi2_gl310_x64.dll ──> usbscan.sys (\\.\UsbscanN)
                 the WIA service, as         │     (Plustek "ScanApi2" backend
                 LocalService)               │      for GL310 scanners)
DocAction II ──> AviScanApi.dll ──> ScanApi2_x64.dll ──┘
```

- **USB driver:** the scanner uses Windows' generic `usbscan.sys`, under its usual device name `\\.\UsbscanN`.
- **Talking to the scanner:** every vendor command is a plain `WriteFile`/`ReadFile` on the bulk pipes, plus `DeviceIoControl` with the standard usbscan IOCTLs.
- **WIA driver:** `PkWiaDrv20-II_311_x64.dll` locates its backend from `C:\Windows\System32\PkWiaDrv20-II_311_x64.ini`, through `WIA20ScanApi=…\Scanapi2_gl310_x64.dll`.

The usbscan IOCTLs used, `CTL_CODE(0x8000, 0x800 + n, METHOD_BUFFERED, FILE_ANY_ACCESS)`:

| IOCTL | Code | Used for |
|---|---|---|
| `IOCTL_CANCEL_IO` | `0x80002004` | abort pending transfers on a pipe (`EVENT_PIPE` = 0) |
| `IOCTL_WAIT_ON_DEVICE_EVENT` | `0x80002008` | read the interrupt pipe (button and status events) |
| `IOCTL_GET_DEVICE_DESCRIPTOR` | `0x80002018` | VID / PID / bcdDevice |
| `IOCTL_RESET_PIPE` | `0x8000201C` | reset a pipe |
| `IOCTL_GET_USB_DESCRIPTOR` | `0x80002020` | string descriptors |
| `IOCTL_GET_PIPE_CONFIGURATION` | `0x80002028` | find the interrupt pipe (type 3) and its max packet size (64) |

## 2. The Scan button

**WIA never reports it.** The WIA driver reads a `TicksButton` setting into a field that nothing ever uses, and it never sends WIA scanner events. That's why NAPS2, which does support WIA button events, never sees the button.

**DocAction II reads it directly.** `Scanapi2_gl310_x64.dll` runs a "button detect" thread that keeps an overlapped `IOCTL_WAIT_ON_DEVICE_EVENT` pending on the scanner's handle:
- The thread starts at device initialisation, but only if a callback window has been registered.
- It reports presses to registered windows with `SendMessage`, using the registered window message `PlustekButtonDetect`.
- Other apps can pause it through a named event, `EventSuspendButtonDetect`.

**Interrupt packet format.** The scanner sends one 4-byte packet per press; there's no release packet. If nobody is reading, the scanner holds the press until someone does.

| Bytes | Meaning |
|---|---|
| `04 xx 00 0k` | legacy key code: `k` = button 1 or 2 |
| `04 xx 01 Pk` (also `10` / `11` in byte 2) | panel key: bit 7 of byte 3 = pressed; low bits = the ASCII function digit (`0x31` = `'1'`) |
| `04 xx 00 10` / `11` / `14` | status events (paper sensors and similar), not buttons |

**The S30's Scan button sends `04 00 01 B1`**: a panel key, pressed, function `'1'`. This matches `"PanelKeyCode"="0x01,0x0d,0x31,…"` in the driver's ini.

## 3. Driver bug: the first press after a scan is lost

`Scanapi2_gl310_x64.dll` contains a quick "was a button pressed?" check: in the DocAction II build, the function around `0x180003020`. It does this:

1. `CreateEvent`, then an overlapped `DeviceIoControl(IOCTL_WAIT_ON_DEVICE_EVENT)`.
2. `WaitForSingleObject(event, 0)`, a **zero timeout**.
3. If nothing is waiting: `CloseHandle(event)` and return, **without cancelling the I/O**.

The read request stays queued in usbscan on the WIA service's handle. The WIA driver runs this check during scan sessions, so after a NAPS2 scan one such orphaned wait sits queued ahead of any other reader. It swallows **the next button press**, and the data ends up in a buffer nobody reads.

**Symptom:** you press the button after a scan and nothing happens; you press it again and it works.

**Fix (in S30ButtonBridge and ScanKiosk):** whenever 2 seconds pass with no event, abort every wait on the event pipe with `IOCTL_CANCEL_IO(EVENT_PIPE)`, then immediately re-arm your own wait.
- `IOCTL_CANCEL_IO` aborts the pipe itself, so it also cancels waits issued through other handles.
- No press is lost in the moment between abort and re-arm, because the scanner holds a press until the next read collects it.

`S30ButtonBridge.exe --orphan-test` reproduces the bug and checks the fix on your own scanner.

Two other theories were tested and ruled out:
- **USB data-toggle desync** after killing a reader: 10 of 10 presses arrived across forced kills, with and without a pipe reset.
- **An idle or sleep state of the button:** presses after nearly 3 minutes idle arrived.

## 4. Standby ("aggressive power saving")

**The command.** The standby timeout is set with an 8-byte bulk command, answered by an 8-byte reply that starts with `STA`:

```
50 57 52 00 tt mm hh 00      "PWR" 0, type, minutes % 60, minutes / 60, 0
53 54 41 ...                 reply "STA..."
```

- **Type 0** is the scanner sleep timer, the one that drops the USB connection. **Type 1** is the lamp-off timer.
- **0 minutes disables standby.** This is what Plustek's Scanner Utility sends for `POWERSAVE 0`.
- The utility's "Disable standby" option is **hidden for some regions**. Its `Scanner Utility.ini` contains `Show Disable Standby Mode except for GeoID=244,…`, and GeoID 244 is the United States.

**Why the setting "doesn't stick."** Every time the driver initialises the scanner, it re-sends `ScannerSleepTime` from its per-account ini file:

```
%APPDATA%\Plustek\311U\Scanapi2_gl310_x64.ini      [...\VxD\VID_07b3&PID_1927\Add-On]  "ScannerSleepTime"="10"
```

The WIA service, and therefore NAPS2, runs as **LocalService**, so its copy is a different file:

```
C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\Plustek\311U\Scanapi2_gl310_x64.ini
```

Scanner Utility only edits the current user's copy. The next WIA session re-sends 10 minutes, and the template that new profiles are copied from is also 10:

```
C:\Program Files (x86)\Plustek II\Plustek SmartOffice S30\Scanapi2_gl310_x64.ini
```

**The fix:**
- `Fix-S30Standby.ps1` sets the value to 0 in every copy.
- The bridge and the kiosk also send `PWR … 0` each time the scanner reconnects.

## 5. Wrong resolution in NAPS2's PDFs

The WIA driver reports resolution in **pixels per metre** instead of pixels per inch. As a result:
- 300 DPI scans are stored as about 8 DPI.
- 600 DPI scans are stored as about 15.2 DPI.
- PDF pages claim to be about **444 inches** wide.

That breaks software in two ways:
- **Page sizes are wrong** in any viewer.
- **Some renderers clip the page.** The PDF spec limits pages to 200 inches, and Windows' built-in `Windows.Data.Pdf` renderer only draws part of the page. PDFium and MuPDF render it fine.

The real resolution is roughly the claimed value × 39.37 (inches per metre), rounded to a standard DPI. ScanProcessor applies this fix automatically.

## 6. Image quality

- **"Maximum quality" in NAPS2 is really lossless.** The driver transfers raw pixels: there's no 8×8 JPEG block pattern in Max-quality scans.
- **600 DPI is real optical resolution, not upscaled 300.** There's about 2.5× more detail beyond what 300 DPI can capture.
- **What it costs,** for a letter page after cropping:

| Setting | Size per page |
|---|---|
| 300 DPI, JPEG 90 | about 1.5 MB |
| 600 DPI, JPEG 95 | about 9 MB |
| 600 DPI lossless | about 50 MB |

## 7. NAPS2 integration

- **The launch command.** `NAPS2.exe /StiDevice:<WIA device id> /StiEvent:<event guid>` is NAPS2's own scanner-button entry point.
  - It runs `ScanWithDevice`, which scans with the default profile if that profile's device ID matches. Otherwise it uses the first profile that matches.
  - If no profile matches, it opens the "new profile" dialog.
- **Forwarding to a running window.** If NAPS2 is already open, the new process forwards the request through the named pipe `\\.\pipe\NAPS2_PIPE_v2_<pid>` (with a 100 ms timeout) and exits.
- **The WIA device ID** is the `DeviceID` value under `HKLM\SYSTEM\CurrentControlSet\Control\Class\{6bdd1fc6-810f-11d0-bec7-08002be2092f}\NNNN`, for the subkey whose `MatchingDeviceId` is `usb\vid_07b3&pid_1927`.
- **Cold starts.** One cold start, with NAPS2 not running, opened the window without scanning. A later retest with debug logging scanned correctly. The bridge therefore starts NAPS2 first and waits for its pipe before sending the request, as a safeguard.
- **Scanning without the GUI.** `NAPS2.Console.exe -p "<profile>" -o <file.pdf> -v` scans with a named profile. With `-v` it prints `Scanned page N.`, and it prints `No scanned pages to export.` when the feeder is empty. ScanKiosk is built on it.
