# plustek-s30-naps2

Use the **Plustek SmartOffice S30** document scanner properly with [NAPS2](https://www.naps2.com/). This repo fixes the problems Plustek's own software leaves behind.

| Problem with the S30 | Fixed by |
|---|---|
| The physical **Scan button doesn't work with NAPS2**: it isn't exposed as a WIA event, and only Plustek's DocAction II sees it | **S30ButtonBridge**, or **ScanKiosk** |
| The scanner **drops off USB after ~10 minutes** of standby, and Plustek's Scanner Utility setting **doesn't stick** | `Fix-S30Standby.ps1`, plus the bridge or kiosk re-sending "never" |
| The **first button press after each scan is ignored** (a Plustek driver bug) | S30ButtonBridge / ScanKiosk |
| NAPS2's PDFs from the S30 have **444-inch pages**: the driver reports resolution per metre instead of per inch | ScanProcessor (when processing), ScanKiosk (for previews) |

How all of this was worked out is in **[docs/REVERSE-ENGINEERING.md](docs/REVERSE-ENGINEERING.md)**: the button protocol, the standby command, the driver bug, and the DPI bug.

## What's here

| Folder | What it is |
|---|---|
| [`S30ButtonBridge`](S30ButtonBridge/README.md) | A small background app. The scanner's Scan button starts a scan in the NAPS2 window. Includes `Fix-S30Standby.ps1`. **Start here on a normal PC.** |
| [`ScanKiosk`](ScanKiosk/README.md) | A full-screen touch app: big tiles for your NAPS2 profiles, the scanner button, and review / add pages / discard. Setup guide: [docs/KIOSK.md](docs/KIOSK.md). |
| [`ScanProcessor`](ScanProcessor/README.md) | A desktop app (Python/Qt) for cleaning up scanned PDFs: crop and deskew, auto-rotate (English and Japanese), duplex and booklet page order, blank page removal, and OCR. Colour output is lossless. |

## Quick start (button + NAPS2 on a desktop PC)

1. Install the **Plustek SmartOffice S30 driver** from Plustek and **NAPS2 8.x**.
2. In NAPS2, create a profile using the **WIA** driver with the S30 (shown as *A3 ADF2 Scanner(311)*), and make it the **default** profile. Do one scan with it.
3. Download `S30ButtonBridge-win-x64.zip` from [Releases](../../releases) and unzip it somewhere permanent. No .NET install is needed.
4. From an admin PowerShell, run the standby fix:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Fix-S30Standby.ps1
   ```
5. Then start the bridge, and register it to start at logon:
   ```powershell
   .\S30ButtonBridge.exe --install
   .\S30ButtonBridge.exe
   ```
6. Press the Scan button.

**If the button does nothing:**
- Check `%LOCALAPPDATA%\S30ButtonBridge\bridge.log`.
- Close the bridge and run `S30ButtonBridge.exe --probe` to see whether the scanner sends anything. A press shows up as `04 00 01 B1`.
- Make sure DocAction II isn't running, since it competes for the button.

## Compatibility

- **Tested on:** Plustek SmartOffice S30 (USB `07B3:1927`), Windows 11, Plustek driver 6.2.1.4, NAPS2 8.5.1.
- **Related models:** other Plustek scanners built on the same GL310 chip (model `311U` and its siblings) probably behave similarly, but haven't been tested. The bridge and kiosk accept `UsbVid` / `UsbPid` settings for trying them. `--probe` and `--orphan-test` show quickly whether a model works the same way. Reports are welcome.

## Building from source

- **The two C# apps** (.NET 8 SDK): `dotnet publish -c Release` in `S30ButtonBridge/` or `ScanKiosk/`; their READMEs give the exact command.
- **ScanProcessor:** see its README (Python 3.12+).
- **Releases:** pushing a `v*` tag builds the self-contained zips through GitHub Actions ([`.github/workflows/release.yml`](.github/workflows/release.yml)).

## Disclaimer

This project is not affiliated with or endorsed by Plustek Inc. "Plustek" and "SmartOffice" are trademarks of their owner.
- **No Plustek software is included.** The findings come from analysing the installed driver, for interoperability.
- **It changes driver settings and scanner settings.** Specifically, the driver's ini files and the scanner's standby timer. Use it at your own risk. `Fix-S30Standby.ps1` backs up every file it changes.

Licensed under the [MIT License](LICENSE).

**Third-party components:**
- **ScanKiosk** uses [Docnet.Core](https://github.com/GowenGit/docnet) (MIT), which bundles [PDFium](https://pdfium.googlesource.com/pdfium/) (BSD-3-Clause; its licence ships in the release as `THIRD-PARTY-NOTICES-PDFium.txt`).
- **ScanProcessor** uses PyMuPDF (AGPL-3.0), OpenCV, PySide6 (LGPL-3.0), OCRmyPDF (MPL-2.0) and Tesseract (Apache-2.0). These are installed by the user from PyPI / winget, not bundled. If you distribute a *packaged* ScanProcessor that includes PyMuPDF, the AGPL applies to that package.
