# ScanKiosk

A full-screen touch app for scanning with NAPS2 profiles and a Plustek S30. For the full setup, see [docs/KIOSK.md](../docs/KIOSK.md).

## How it works

- **Tiles:** one per NAPS2 profile for the scanner, read from `%APPDATA%\NAPS2\profiles.xml`.
- **Scanning:** each scan runs `NAPS2.Console.exe -p "<profile>" -o <temp.pdf> -v`, and the page count updates live from NAPS2's output.
- **Saving:** finished PDFs are copied to the profile's NAPS2 Auto Save path, with NAPS2 placeholders such as `$(YYYY)` and `$(nnn)` filled in. They're copied under a temporary name and renamed when complete, so programs watching the folder never see a half-written file.
- **Add more pages:** re-runs the scan with `-i <current pdf>`, so the existing pages come first.
- **Scan button:** read directly over USB, using the same code as S30ButtonBridge. That includes the fix for the driver bug that loses the first press after a scan. Each time the scanner reconnects, the kiosk also turns off standby.
- **Previews:** rendered with PDFium (Docnet.Core). Windows' own PDF renderer clips NAPS2's oversized S30 pages; see [REVERSE-ENGINEERING.md §5](../docs/REVERSE-ENGINEERING.md).

## Command line

| Command | What it does |
|---|---|
| *(no args)* | Runs full screen with the scan button active. |
| `--windowed` | Runs in a normal window instead of full screen. |
| `--no-button` | Doesn't listen to the scanner button. |
| `--install` / `--uninstall` | Turns starting at logon on or off for the current user. |
| `--snapshot <dir> [sample.pdf]` | Renders each screen to a PNG file, for layout checks. |

## Configuration: `ScanKiosk.json` next to the exe

Every setting is optional; see [`ScanKiosk.example.json`](ScanKiosk.example.json).

| Key | Default | Meaning |
|---|---|---|
| `Tiles` | all S30 profiles | A list of `{ "Profile": "<NAPS2 profile name>", "Label": "…", "Description": "…" }` |
| `DefaultOutputPattern` | `%USERPROFILE%\Documents\Scans\$(YYYY)-$(MM)-$(DD)_$(nnn).pdf` | Where to save scans from profiles that have no Auto Save path |
| `Naps2ConsolePath` | `C:\Program Files\NAPS2\NAPS2.Console.exe` | Location of NAPS2.Console |
| `FullScreen` | `true` | Run full screen |
| `AutoReturnSeconds` | `20` | Seconds before the review screen returns to the start screen |
| `ExitHoldSeconds` | `3` | How long to hold the top-left corner to exit |
| `ScanButtonEnabled` | `true` | Listen to the scanner's button |
| `UsbVid`, `UsbPid` | `07B3`, `1927` | The scanner's USB IDs, for trying related Plustek models |
| `ScannerStandbyMinutes` | `0` | Standby timeout sent on each connect; `0` = never, `-1` = leave it alone |
| `OrphanSweepSeconds` | `2` | How often to clear the driver's leftover waits (the lost-press fix) |
| `ButtonDebounceSeconds` | `3` | Ignores repeat presses within this time |

## Build

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```
