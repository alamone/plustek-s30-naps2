# ScanProcessor

A desktop app for cleaning up the PDFs NAPS2 saves. It lists the PDFs in an incoming folder; you pick which ones to process, choose how, and save them.

- **Results** are saved to the output folder.
- **Originals** are moved to an `_originals` folder inside the incoming folder, so nothing is ever lost.
- Both folders are set from the toolbar. They default to `Documents\Scans` and `Documents\Scans\Processed`.

It works with any NAPS2 scans. It also fixes two quirks of the Plustek S30: the black border around every page, and the wrong resolution stored in the PDF.

## Install

```powershell
py -3 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
winget install UB-Mannheim.TesseractOCR    # OCR and auto-rotate; add the jpn language data for Japanese
```

## Run

Double-click `ScanProcessor.pyw`. Or, from this folder:

```powershell
.\.venv\Scripts\pythonw.exe ScanProcessor.pyw
```

## Using it

1. **Tick** the PDFs you want to process. **Click** one to see its scanned pages.
2. Choose a **Preset**. You can adjust any individual option afterwards.
3. Either:
   - **Preview selected file**, then check the result in the *Result preview* tab. There you can rotate, delete or reorder pages; right-click a page for the menu, or double-click it to view it large. Then **Save preview**.
   - Or **Process checked files** to run the current settings on every ticked file without reviewing each one.

## Processing options

| Option | What it does |
|---|---|
| **Layout: Duplex** | Front/back sheets in scan order. *Swap page pairs* fixes sheets that were fed back side first. |
| **Layout: Single-sided** | Pages are kept in the order they were scanned. |
| **Layout: Booklet** | For a booklet with the staples removed, scanned duplex as a nested stack. Each sheet side is cut in half and the halves are put back in reading order. Tick *innermost sheet first* or *back side first* if the stack was fed that way. If there's an odd number of sheet sides (a scanner dropped a blank inside cover), a blank is assumed. |
| **Layout: Manual duplex** | All fronts, then all backs in reverse order, interleaved. |
| **Sections** | Splits one scan into parts, for example `1-13 booklet; 14-15 booklet; 16-17 single`. Page numbers are scanned pages. Pages you don't list are kept as single pages. |
| **Crop & deskew** | Finds the paper on the black scanner background, straightens it and crops to it. If a page's edge is ambiguous (dark artwork running to the edge), it uses the size and position of the nearby pages in the same scan. |
| **Auto-rotate** | Detects text orientation with Tesseract, for English and Japanese. Pages it isn't sure about follow the other pages in the document. |
| **Remove blank pages** | Drops pages with almost no ink, such as blank backs. *Sensitivity* is the maximum fraction of dark pixels a page can have and still count as blank. |
| **Output color** | **Color (default) is lossless.** The scanner's JPEG is embedded byte-for-byte. Crop, deskew, rotation and booklet splitting are stored as PDF drawing instructions, and booklet halves share one image. **Grayscale** re-encodes the pages as JPEG (smaller files). **Black & white** creates new 1-bit images, stored losslessly as PNG (smallest files). |
| **JPEG quality** | Only used for grayscale output. Image quality is mostly set at scan time, by the Quality setting in the NAPS2 profile (90 for the S30). |
| **Page size DPI** | Only sets how many inches each page measures. The image is never rescaled. *Auto* uses the PDF's own resolution when it's plausible. The Plustek WIA driver reports pixels per *metre*, so NAPS2 stores 300 DPI scans as about 8 DPI and 600 DPI scans as about 15 DPI (pages claim to be 444 inches wide). Auto converts those back to 300 and 600, and falls back to 300 if the value can't be decoded. Enter a number to override it. |
| **OCR** | Adds a searchable text layer using OCRmyPDF and Tesseract. *Auto* uses `jpn+eng` for Japanese documents and `eng` otherwise. |

## Requirements

- **Python 3.14** with the packages in `requirements.txt`, installed in `.venv`.
- **Tesseract OCR** from UB Mannheim (`winget install UB-Mannheim.TesseractOCR`), with `eng`, `jpn` and `osd` language data.
- **OCRmyPDF.** It's installed with the Python packages, and doesn't need Ghostscript because the output isn't PDF/A.

To check the processing engine from the command line:

```powershell
.\.venv\Scripts\python.exe -m scanproc.engine in.pdf out.pdf booklet --no-ocr
```
