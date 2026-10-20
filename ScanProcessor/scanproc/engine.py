"""Scan post-processing engine: PDF in -> cleaned-up PDF out.

Pipeline per document:
  load page images -> crop black scanner border + deskew -> layout (duplex / booklet / ...)
  -> auto-rotate -> drop blank pages -> colour conversion -> write PDF at the true DPI -> optional OCR.

NAPS2 + the Plustek WIA driver record a bogus ~8 DPI in the PDF (pages claim to be ~444x635 in),
so page sizes are always rebuilt from the configured scan DPI.
"""
from __future__ import annotations

import io
import os
import re
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass, field, asdict
from pathlib import Path
from typing import Callable

import cv2
import numpy as np
import pymupdf
from PIL import Image

TESSERACT_DIRS = [r"C:\Program Files\Tesseract-OCR", r"C:\Program Files (x86)\Tesseract-OCR"]
NO_WINDOW = 0x08000000 if os.name == "nt" else 0  # CREATE_NO_WINDOW

LAYOUTS = {
    "duplex": "Duplex document (front/back sheets)",
    "simplex": "Single-sided pages",
    "booklet": "Booklet, staples removed (2-up spreads)",
    "manual_duplex": "Manual duplex (all fronts, then all backs reversed)",
}


@dataclass
class Options:
    layout: str = "duplex"
    dpi: int = 0                       # page-size DPI override; 0 = auto (trust the PDF unless it's implausible)
    crop_deskew: bool = True
    auto_rotate: bool = True
    remove_blank: bool = True
    blank_sensitivity: float = 0.003   # max fraction of "ink" pixels for a page to count as blank
    color: str = "color"               # color | gray | bw
    jpeg_quality: int = 80
    ocr: bool = True
    ocr_language: str = "auto"         # auto | eng | jpn+eng | jpn+jpn_vert+eng
    booklet_inner_first: bool = False  # sheets were stacked innermost sheet first
    booklet_back_first: bool = False   # first scanned side of each sheet is the back (pages 2 / N-1)
    duplex_swap: bool = False          # duplex: each sheet was fed back side first -> swap page pairs
    sections: str = ""                 # e.g. "1-13 booklet; 14-15 booklet; 16-17 single" (blank = whole file)
    reverse_order: bool = False

    def to_dict(self) -> dict:
        return asdict(self)

    @classmethod
    def from_dict(cls, d: dict) -> "Options":
        known = {k: v for k, v in d.items() if k in cls.__dataclass_fields__}
        return cls(**known)


PRESETS: dict[str, Options] = {
    "Duplex document": Options(layout="duplex"),
    "Single-sided document": Options(layout="simplex", remove_blank=False),
    "Booklet (staples removed)": Options(layout="booklet", remove_blank=False),
    "Manual duplex": Options(layout="manual_duplex"),
}


@dataclass(eq=False)
class SourceImage:
    """The scanner's original embedded image, kept byte-for-byte for lossless output."""
    data: bytes
    width: int
    height: int


@dataclass
class OutPage:
    image: Image.Image
    label: str                 # e.g. "scan p3" or "booklet p7"
    rotation: int = 0          # extra user rotation (degrees clockwise), applied on save
    notes: list[str] = field(default_factory=list)
    script: str = ""           # OSD script (Latin, Japanese, ...) when auto-rotate ran
    dpi: int = 0               # resolution used to size this page in the output PDF
    src: SourceImage | None = None
    # 3x3 affine: original source pixel coords -> pixel coords of `image` (crop/deskew/turn/split so far)
    geom: np.ndarray = field(default_factory=lambda: np.eye(3))

    def transformed(self, matrix: np.ndarray, image: Image.Image) -> None:
        """Record that `image` is the current image transformed by `matrix` (current px -> new px)."""
        self.geom = matrix @ self.geom
        self.image = image

    def final_image(self) -> Image.Image:
        return rotate_cw(self.image, self.rotation)

    def final_geom(self) -> np.ndarray:
        return rotation_matrix(self.image.width, self.image.height, self.rotation) @ self.geom


Progress = Callable[[str], None]


# --------------------------------------------------------------------------- loading

FALLBACK_DPI = 300  # the S30 NAPS2 profile scans at 300 DPI
STANDARD_DPIS = (75, 100, 150, 200, 240, 300, 400, 600, 1200)


def effective_dpi(pdf_dpi: float, override: int) -> tuple[int, str]:
    """The resolution used to size output pages. Pixels are never resampled; this only sets how many
    inches each page measures. NAPS2 + the S30 WIA driver record ~8 DPI, so implausible values fall back."""
    if override > 0:
        return override, f"page size at {override} DPI (override)"
    if 72 <= pdf_dpi <= 2400:
        return int(round(pdf_dpi)), ""
    if 0 < pdf_dpi < 72:
        # The Plustek WIA driver reports pixels per *metre* (300 -> 7.6-8 "DPI", 600 -> 15.2 "DPI").
        guess = pdf_dpi * 39.3701
        best = min(STANDARD_DPIS, key=lambda d: abs(d - guess))
        if abs(best - guess) <= 0.12 * best:
            return best, f"PDF claims {pdf_dpi:.1f} DPI (pixels per metre); sizing pages at {best} DPI"
    return FALLBACK_DPI, f"PDF claims {pdf_dpi:.1f} DPI; sizing pages at {FALLBACK_DPI} DPI"


def load_page_images(pdf_path: str | Path, dpi: int = FALLBACK_DPI) -> list[Image.Image]:
    return [img for img, _, _ in load_pages(pdf_path, dpi)]


def load_pages(pdf_path: str | Path, dpi: int = FALLBACK_DPI) -> list[tuple[Image.Image, float, SourceImage | None]]:
    """Returns (image, resolution the PDF claims, original image bytes) per page, using the embedded scan
    image when there is exactly one. `dpi` is only used to render pages without a single embedded image."""
    images = []
    with pymupdf.open(pdf_path) as doc:
        for page in doc:
            imgs = page.get_images(full=True)
            img = None
            src = None
            if len(imgs) == 1 and page.rotation == 0:
                try:
                    raw = doc.extract_image(imgs[0][0])
                    img = Image.open(io.BytesIO(raw["image"]))
                    img.load()
                    if raw.get("ext") in ("jpeg", "jpg", "png") and not raw.get("smask"):
                        src = SourceImage(raw["image"], img.width, img.height)
                except Exception:
                    img = None
            if img is None:
                # Fallback: render at the embedded image's native density (the page size may be bogus),
                # or at the scan DPI for pages without images.
                scale = dpi / 72.0
                if imgs and imgs[0][2] and page.rect.width:
                    scale = imgs[0][2] / page.rect.width
                pix = page.get_pixmap(matrix=pymupdf.Matrix(scale, scale))
                img = Image.frombytes("RGB", (pix.width, pix.height), pix.samples)
            if img.mode not in ("RGB", "L"):
                img = img.convert("RGB")
            images.append((img, img.width * 72.0 / page.rect.width if page.rect.width else 0.0, src))
    return images


# --------------------------------------------------------------------------- geometry

def _to_gray_np(img: Image.Image, max_side: int | None = None) -> tuple[np.ndarray, float]:
    g = img.convert("L")
    scale = 1.0
    if max_side and max(g.size) > max_side:
        scale = max_side / max(g.size)
        g = g.resize((max(1, int(g.width * scale)), max(1, int(g.height * scale))), Image.BILINEAR)
    return np.asarray(g), scale


@dataclass
class PaperInfo:
    """Where the paper sits in a scan (full-resolution pixels, axis-aligned after deskew)."""
    status: str                 # "confident" | "ambiguous" | "none"
    reason: str
    cx: float = 0.0
    cy: float = 0.0
    w: float = 0.0
    h: float = 0.0
    angle: float = 0.0          # deskew angle (degrees, cv2 convention)
    bg: tuple[float, float] = (5.0, 2.0)  # scanner background (median, std) measured outside the paper


def _gray_and_scale(img: Image.Image) -> tuple[np.ndarray, float]:
    return _to_gray_np(img, 1200)


def _outside_pixels(gray: np.ndarray, box: np.ndarray) -> np.ndarray:
    outside = np.ones(gray.shape, np.uint8)
    cv2.fillPoly(outside, [np.int32(box)], 0)
    outside = cv2.erode(outside, np.ones((7, 7), np.uint8))  # ignore the paper edge itself
    return gray[outside.astype(bool)]


def _looks_like_background(px: np.ndarray, bg: tuple[float, float]) -> bool:
    """Scanner background is near-black and very uniform (S30: median 0-6, std < 3); artwork is not."""
    if px.size == 0:
        return True
    med, std = bg
    return (np.median(px) <= med + 10 and px.std() <= max(4.0, 2.5 * std)
            and np.percentile(px, 95) <= med + 25)


def analyze_paper(img: Image.Image) -> PaperInfo:
    """Finds the (bright) paper on the (black) scanner background without changing the image."""
    gray, s = _gray_and_scale(img)
    b = max(2, int(min(gray.shape) * 0.01))
    frame = np.concatenate([gray[:b].ravel(), gray[-b:].ravel(), gray[:, :b].ravel(), gray[:, -b:].ravel()])
    if np.median(frame) > 90:
        return PaperInfo("none", "no dark scanner border (already cropped)")
    blur = cv2.GaussianBlur(gray, (5, 5), 0)
    _, th = cv2.threshold(blur, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    th = cv2.morphologyEx(th, cv2.MORPH_CLOSE, np.ones((9, 9), np.uint8))
    contours, _ = cv2.findContours(th, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not contours:
        return PaperInfo("ambiguous", "no paper outline found")
    c = max(contours, key=cv2.contourArea)
    rect = cv2.minAreaRect(c)
    (cx, cy), (w, h), angle = rect
    # minAreaRect's width runs along `angle`; express it as a small skew with axis-aligned w/h.
    if angle > 45:
        angle -= 90; w, h = h, w
    elif angle < -45:
        angle += 90; w, h = h, w
    if abs(angle) > 10:  # implausible; probably not a clean rectangle
        angle = 0.0
    info = PaperInfo("confident", "", cx / s, cy / s, w / s, h / s, angle)
    outside = _outside_pixels(gray, cv2.boxPoints(rect))
    if outside.size:
        info.bg = (float(np.median(outside)), float(outside.std()))
    if cv2.contourArea(c) < 0.02 * gray.size:
        info.status, info.reason = "ambiguous", "no clear paper outline"
    elif outside.size and np.percentile(outside, 95) > 80:
        # Bright content outside the outline: the outline has cut into the page (dark artwork at the edge).
        info.status, info.reason = "ambiguous", "border is not plain scanner background"
    return info


def resolve_with_neighbours(infos: list[PaperInfo], images: list[Image.Image], window: int = 4) -> list[PaperInfo]:
    """Pages from one scan are normally the same paper size in the same feeder position. Ambiguous
    pages, and confident ones that came out clearly smaller than their neighbours while the strip
    between the two outlines is not plain background, take the nearby confident pages' median rect."""
    out = []
    for i, info in enumerate(infos):
        near = [infos[j] for j in sorted(range(len(infos)), key=lambda j: abs(j - i))
                if j != i and infos[j].status == "confident" and abs(j - i) <= window]
        if info.status == "none" or not near:
            out.append(info)
            continue
        mw = float(np.median([n.w for n in near])); mh = float(np.median([n.h for n in near]))
        mcx = float(np.median([n.cx for n in near])); mcy = float(np.median([n.cy for n in near]))
        ref = PaperInfo("confident", "", mcx, mcy, mw, mh, info.angle if info.status == "confident" else 0.0)
        if info.status == "ambiguous":
            ref.reason = f"{info.reason}; using size/position of nearby pages"
            out.append(ref)
            continue
        smaller = info.w < 0.95 * mw or info.h < 0.95 * mh
        if smaller:
            gray, s = _gray_and_scale(images[i])
            ring = np.zeros(gray.shape, np.uint8)
            big = cv2.boxPoints(((mcx * s, mcy * s), (mw * s, mh * s), info.angle))
            small = cv2.boxPoints(((info.cx * s, info.cy * s), (info.w * s, info.h * s), info.angle))
            cv2.fillPoly(ring, [np.int32(big)], 1)
            cv2.fillPoly(ring, [np.int32(small)], 0)
            ring = cv2.erode(ring, np.ones((5, 5), np.uint8))
            # Reference background: outside the neighbours' (larger) outline, which can't be page content.
            outside_big = _outside_pixels(gray, big)
            bg = (float(np.median(outside_big)), float(outside_big.std())) if outside_big.size > 500 else (5.0, 2.0)
            if ring.any() and not _looks_like_background(gray[ring.astype(bool)], bg):
                ref.reason = "smaller than nearby pages and the difference is page content; using their size"
                out.append(ref)
                continue
        out.append(info)
    return out


def apply_crop(img: Image.Image, info: PaperInfo) -> tuple[Image.Image, str, np.ndarray]:
    """-> (cropped image, note, 3x3 matrix mapping input px -> output px)."""
    if info.status != "confident":
        return img, info.reason + "; not cropped", np.eye(3)
    full = np.asarray(img)
    cx, cy, w, h = info.cx, info.cy, info.w, info.h
    rot = np.eye(3)
    if abs(info.angle) > 0.05:
        m = cv2.getRotationMatrix2D((cx, cy), info.angle, 1.0)
        rot = np.vstack([m, [0, 0, 1]])
        full = cv2.warpAffine(full, m, (full.shape[1], full.shape[0]), flags=cv2.INTER_CUBIC,
                              borderMode=cv2.BORDER_CONSTANT, borderValue=0)
    inset = 0.004 * max(w, h)  # trim the shadowed paper edge
    x0 = int(max(0, cx - w / 2 + inset)); x1 = int(min(full.shape[1], cx + w / 2 - inset))
    y0 = int(max(0, cy - h / 2 + inset)); y1 = int(min(full.shape[0], cy + h / 2 - inset))
    out = Image.fromarray(np.ascontiguousarray(full[y0:y1, x0:x1]))
    note = f"cropped {img.width}x{img.height} -> {out.width}x{out.height}, deskew {info.angle:+.2f}°"
    shift = np.array([[1, 0, -x0], [0, 1, -y0], [0, 0, 1]], float)
    return out, note + (f" ({info.reason})" if info.reason else ""), shift @ rot


def crop_and_deskew(img: Image.Image) -> tuple[Image.Image, str]:
    """Single-page crop (no neighbour information)."""
    out, note, _ = apply_crop(img, analyze_paper(img))
    return out, note


def crop_document(images: list[Image.Image]) -> list[tuple[Image.Image, str, np.ndarray]]:
    infos = resolve_with_neighbours([analyze_paper(im) for im in images], images)
    return [apply_crop(im, info) for im, info in zip(images, infos)]


def ink_fraction(img: Image.Image) -> float:
    gray, _ = _to_gray_np(img, 1000)
    h, w = gray.shape
    my, mx = int(h * 0.04), int(w * 0.04)  # ignore edges (shadows, punch holes)
    core = cv2.GaussianBlur(gray[my:h - my, mx:w - mx], (3, 3), 0)
    return float((core < 150).mean()) if core.size else 0.0


# --------------------------------------------------------------------------- orientation

def find_tesseract() -> str | None:
    exe = shutil.which("tesseract")
    if exe:
        return exe
    for d in TESSERACT_DIRS:
        p = Path(d) / "tesseract.exe"
        if p.exists():
            return str(p)
    return None


def detect_rotation(img: Image.Image) -> tuple[int, float]:
    """Tesseract OSD. Returns (degrees clockwise to rotate so text is upright, confidence)."""
    rot, conf, _ = detect_osd(img)
    return rot, conf


def detect_osd(img: Image.Image) -> tuple[int, float, str]:
    """Tesseract OSD: (degrees clockwise to rotate upright, orientation confidence, script e.g. Latin/Japanese)."""
    exe = find_tesseract()
    if not exe:
        return 0, 0.0, ""
    g = img.convert("L")
    if max(g.size) > 3600:  # OSD needs near-full resolution; at ~2000px it is unreliable on dense forms
        f = 3600 / max(g.size)
        g = g.resize((int(g.width * f), int(g.height * f)), Image.LANCZOS)
    with tempfile.TemporaryDirectory() as td:
        p = os.path.join(td, "osd.png")
        g.save(p)
        try:
            r = subprocess.run([exe, p, "-", "--psm", "0"], capture_output=True, text=True, encoding="utf-8", errors="replace",
                               timeout=60, creationflags=NO_WINDOW)
        except (subprocess.TimeoutExpired, OSError):
            return 0, 0.0, ""
    out = r.stdout + r.stderr
    rot = re.search(r"Rotate:\s*(\d+)", out)
    conf = re.search(r"Orientation confidence:\s*([\d.]+)", out)
    script = re.search(r"Script:\s*(\w+)", out)
    if not rot:
        return 0, 0.0, ""
    return int(rot.group(1)) % 360, float(conf.group(1)) if conf else 0.0, script.group(1) if script else ""


CJK_SCRIPTS = {"Japanese", "Han", "HanS", "HanT", "Hiragana", "Katakana"}
OCR_LANGUAGES = {
    "auto": "Auto (detect English / Japanese)",
    "eng": "English",
    "jpn+eng": "Japanese + English",
    "jpn+jpn_vert+eng": "Japanese incl. vertical model + English",
}


def resolve_ocr_language(setting: str, scripts: list[str]) -> str:
    if setting != "auto":
        return setting
    known = [s for s in scripts if s]
    cjk = sum(s in CJK_SCRIPTS for s in known)
    return "jpn+eng" if known and cjk * 2 >= len(known) else "eng"


def rotation_matrix(w: int, h: int, degrees: int) -> np.ndarray:
    """Pixel-coordinate mapping for rotate_cw of a w x h image."""
    return {0: np.eye(3),
            90: np.array([[0, -1, h], [1, 0, 0], [0, 0, 1]], float),
            180: np.array([[-1, 0, w], [0, -1, h], [0, 0, 1]], float),
            270: np.array([[0, 1, 0], [-1, 0, w], [0, 0, 1]], float)}[degrees % 360]


def rotate_cw(img: Image.Image, degrees: int) -> Image.Image:
    degrees %= 360
    if degrees == 0:
        return img
    return img.transpose({90: Image.ROTATE_270, 180: Image.ROTATE_180, 270: Image.ROTATE_90}[degrees])


MIN_OSD_CONFIDENCE = 3.0


def orient_all(images: list[Image.Image]) -> list[tuple[Image.Image, str, str, int]]:
    """Auto-rotates each image -> (image, note, script, degrees clockwise). Pages where OSD is unsure follow the majority
    of the confident pages in the same document (a stack is normally fed the same way)."""
    osd = [detect_osd(im) for im in images]
    det = [(r, c) for r, c, _ in osd]

    def majority(indexes) -> int | None:
        votes = [det[i][0] for i in indexes if det[i][1] >= MIN_OSD_CONFIDENCE]
        return max(set(votes), key=votes.count) if votes else None

    out = []
    for i, (im, (rot, conf)) in enumerate(zip(images, det)):
        if conf >= MIN_OSD_CONFIDENCE:
            use, why = rot, f"confidence {conf:.1f}"
        else:
            # Duplex backs may come out flipped relative to fronts, so prefer pages on the same side.
            same_side = majority(range(i % 2, len(det), 2))
            use = same_side if same_side is not None else (majority(range(len(det))) or 0)
            why = f"low confidence {conf:.1f}, following the other pages"
        out.append((rotate_cw(im, use), f"rotated {use}° ({why})" if use else f"upright ({why})", osd[i][2], use))
    return out


# --------------------------------------------------------------------------- layouts

def split_spread(img: Image.Image) -> tuple[Image.Image, Image.Image, int]:
    """Splits a 2-up spread down the middle, snapping to the fold line if one is clearly visible.

    Booklet sheets are symmetric, so the centre is the right default. A fold shows up as a narrow
    column darker than its neighbourhood; only that (within ±2% of centre) moves the cut.
    """
    gray, s = _to_gray_np(img, 1600)
    w = gray.shape[1]
    x = img.width // 2
    lo, hi = int(w * 0.48), int(w * 0.52)
    if hi - lo > 6:
        h = gray.shape[0]
        cols = gray[int(h * 0.1):int(h * 0.9), lo:hi].astype(np.float32).mean(axis=0)
        i = int(np.argmin(cols))
        if np.median(cols) - cols[i] > 12:
            x = int((lo + i) / s)
    return img.crop((0, 0, x, img.height)), img.crop((x, 0, img.width, img.height)), x


def booklet_order(spread_count: int, inner_first: bool, back_first: bool) -> list[tuple[int, int]]:
    """Booklet page numbers (1-based) for (left, right) half of each scanned spread, in scan order.

    A saddle-stitched booklet of S sheets has N = 4S pages. Sheet k (0 = outermost) carries
    front = (N-2k | 2k+1) and back = (2k+2 | N-2k-1), as (left | right) when viewed upright.
    """
    sheets = (spread_count + 1) // 2
    n = 4 * sheets
    out = []
    for s in range(spread_count):
        k = s // 2
        if inner_first:
            k = sheets - 1 - k
        is_front = (s % 2 == 0) != back_first
        out.append((n - 2 * k, 2 * k + 1) if is_front else (2 * k + 2, n - 2 * k - 1))
    return out


# --------------------------------------------------------------------------- pipeline

def process(pdf_path: str | Path, opt: Options, log: Progress = print) -> list[OutPage]:
    name = Path(pdf_path).name
    log(f"{name}: loading")
    loaded = load_pages(pdf_path, opt.dpi or FALLBACK_DPI)
    raw = [img for img, _, _ in loaded]
    dpis = [effective_dpi(d, opt.dpi) for _, d, _ in loaded]
    for note in sorted({n for _, n in dpis if n}):
        log(f"{name}: {note}")
    log(f"{name}: {len(raw)} scanned page(s)")

    pages = [OutPage(img, f"scan p{i}", dpi=d, src=src)
             for i, ((img, _, src), (d, _)) in enumerate(zip(loaded, dpis), 1)]
    if opt.crop_deskew:
        for p, (img, note, m) in zip(pages, crop_document(raw)):
            p.transformed(m, img)
            p.notes.append(note)

    out: list[OutPage] = []
    for kind, part in split_sections(pages, opt, log, name):
        if kind == "booklet":
            out += _layout_booklet(part, opt, log, name)
        else:
            out += _layout_flat(part, kind, opt, log, name)
    pages = out

    if opt.remove_blank:
        kept = []
        for p in pages:
            ink = ink_fraction(p.image)
            if ink <= opt.blank_sensitivity:
                log(f"{name}: {p.label} removed as blank (ink {ink:.4f})")
            else:
                kept.append(p)
        if kept:
            pages = kept
    pages = [p for p in pages if not p.label.endswith("(placeholder)")]

    if opt.reverse_order:
        pages.reverse()
    log(f"{name}: {len(pages)} output page(s)")
    return pages


SECTION_KINDS = {"booklet", "duplex", "simplex", "single", "manual_duplex"}


def parse_sections(text: str, page_count: int) -> list[tuple[str, list[int]]]:
    """'1-13 booklet; 14-15 booklet; 16-17 single' -> [(kind, [0-based indexes])]. Raises ValueError."""
    out, used = [], set()
    for part in re.split(r"[;\n]+", text.strip()):
        part = part.strip()
        if not part:
            continue
        m = re.fullmatch(r"(\d+)(?:\s*-\s*(\d+|end))?\s+([a-z_]+)", part, re.I)
        if not m:
            raise ValueError(f"can't read section '{part}' (expected e.g. '1-13 booklet')")
        a = int(m.group(1))
        b = page_count if (m.group(2) or "").lower() == "end" else int(m.group(2) or a)
        kind = m.group(3).lower()
        if kind not in SECTION_KINDS:
            raise ValueError(f"unknown section type '{kind}' (use {', '.join(sorted(SECTION_KINDS))})")
        if not (1 <= a <= b <= page_count):
            raise ValueError(f"section '{part}' is outside pages 1-{page_count}")
        idx = list(range(a - 1, b))
        if used & set(idx):
            raise ValueError(f"section '{part}' overlaps another section")
        used |= set(idx)
        out.append(("simplex" if kind == "single" else kind, idx))
    leftover = [i for i in range(page_count) if i not in used]
    if leftover:
        out.append(("simplex", leftover))
    return out


def split_sections(pages: list[OutPage], opt: Options, log: Progress, name: str):
    if opt.sections.strip():
        for kind, idx in parse_sections(opt.sections, len(pages)):
            yield kind, [pages[i] for i in idx]
        return
    if opt.layout != "booklet":
        yield opt.layout, pages
        return
    # Whole scan is one booklet, except pages that clearly aren't spreads (inserts, cards).
    areas = sorted(p.image.width * p.image.height for p in pages)
    median = areas[len(areas) // 2]
    def is_spread(p):
        a = p.image.width * p.image.height
        return a > 0.7 * median and max(p.image.size) > 1.15 * min(p.image.size)
    spreads = [p for p in pages if is_spread(p)]
    extras = [p for p in pages if not is_spread(p)]
    if extras:
        log(f"{name}: treating {', '.join(p.label for p in extras)} as single pages (not booklet spreads)")
    yield "booklet", spreads
    if extras:
        yield "simplex", extras


def _orient(pages: list[OutPage], opt: Options) -> None:
    if opt.auto_rotate and pages:
        for p, (img, note, script, deg) in zip(pages, orient_all([p.image for p in pages])):
            p.transformed(rotation_matrix(p.image.width, p.image.height, deg), img)
            p.script = script
            p.notes.append(note)


def _layout_flat(pages: list[OutPage], kind: str, opt: Options, log: Progress, name: str) -> list[OutPage]:
    pages = list(pages)
    if kind == "duplex" and opt.duplex_swap:
        for i in range(0, len(pages) - 1, 2):
            pages[i], pages[i + 1] = pages[i + 1], pages[i]
    if kind == "manual_duplex" and len(pages) > 1:
        half = (len(pages) + 1) // 2
        fronts, backs = pages[:half], list(reversed(pages[half:]))
        pages = [x for pair in zip(fronts, backs + [None] * (len(fronts) - len(backs))) for x in pair if x]
    _orient(pages, opt)
    for p in pages:
        log(f"{name}: {p.label}: {'; '.join(p.notes)}")
    return pages


def _layout_booklet(spreads: list[OutPage], opt: Options, log: Progress, name: str) -> list[OutPage]:
    if not spreads:
        return []
    _orient(spreads, opt)
    spreads = list(spreads)
    if len(spreads) % 2:
        # Odd number of sheet sides: some scanners drop blank sides, most often the cover's inside.
        ref = spreads[0].image
        blank = OutPage(Image.new(ref.mode, ref.size, "white"), "blank (placeholder)", dpi=spreads[0].dpi)
        pos = len(spreads) - 1 if opt.booklet_inner_first else 1
        spreads.insert(pos, blank)
        log(f"{name}: odd number of spreads; assuming a blank cover inside was not scanned")
    order = booklet_order(len(spreads), opt.booklet_inner_first, opt.booklet_back_first)
    halves: list[tuple[int, OutPage]] = []
    first = spreads[0].label
    for sp, (lp, rp) in zip(spreads, order):
        if sp.image.height > sp.image.width:  # a spread is landscape when upright; OSD could not tell
            sp.transformed(rotation_matrix(sp.image.width, sp.image.height, 90), rotate_cw(sp.image, 90))
            sp.notes.append("forced landscape")
        left, right, cut = split_spread(sp.image)
        placeholder = sp.label.endswith("(placeholder)")
        log(f"{name}: {sp.label} -> booklet p{lp} | p{rp}" + ("" if placeholder else f"  ({'; '.join(sp.notes)})"))
        for num, half, side, dx in ((lp, left, "left", 0), (rp, right, "right", cut)):
            label = f"booklet p{num} (placeholder)" if placeholder else f"booklet p{num}"
            shift = np.array([[1, 0, -dx], [0, 1, 0], [0, 0, 1]], float)
            halves.append((num, OutPage(half, label, notes=[f"from {sp.label} {side}"], script=sp.script,
                                        dpi=sp.dpi, src=sp.src, geom=shift @ sp.geom)))
    log(f"{name}: booklet from {first}: {len(halves)} pages")
    return [p for _, p in sorted(halves, key=lambda t: t[0])]


def _encode(img: Image.Image, opt: Options) -> tuple[bytes, Image.Image]:
    if opt.color == "gray":
        img = img.convert("L")
    elif opt.color == "bw":
        g = np.asarray(img.convert("L"))
        bw = cv2.adaptiveThreshold(g, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY, 41, 15)
        img = Image.fromarray(bw).convert("1")
        buf = io.BytesIO(); img.save(buf, "PNG", optimize=True)
        return buf.getvalue(), img
    buf = io.BytesIO()
    img.convert("RGB" if img.mode != "L" else "L").save(buf, "JPEG", quality=opt.jpeg_quality, optimize=True)
    return buf.getvalue(), img


def _place_original(doc, page, p: OutPage, dpi: int, embedded: dict[int, int]) -> None:
    """Draws the untouched source image so the page shows exactly p.final_image(): crop, deskew, turns
    and booklet splits become the PDF image transform instead of re-encoded pixels."""
    src = p.src
    xref = embedded.get(id(src))
    if xref is None:
        xref = page.insert_image(page.rect, stream=src.data, keep_proportion=False)
        embedded[id(src)] = xref
    else:
        page.insert_image(page.rect, xref=xref, keep_proportion=False)
    name = next(i[7] for i in page.get_images(full=True) if i[0] == xref)
    k = 72.0 / dpi
    height = page.rect.height
    # PDF image space (unit square, y up) -> source px -> output px -> page points (y up)
    unit_to_src = np.array([[src.width, 0, 0], [0, -src.height, src.height], [0, 0, 1]], float)
    px_to_pt = np.array([[k, 0, 0], [0, -k, height], [0, 0, 1]], float)
    m = px_to_pt @ p.final_geom() @ unit_to_src
    a, b, c, d, e, f = m[0, 0], m[1, 0], m[0, 1], m[1, 1], m[0, 2], m[1, 2]
    content = f"q {a:.6f} {b:.6f} {c:.6f} {d:.6f} {e:.4f} {f:.4f} cm /{name} Do Q".encode()
    xrefs = page.get_contents()
    doc.update_stream(xrefs[0], content)
    for x in xrefs[1:]:
        doc.update_stream(x, b"")


def write_pdf(pages: list[OutPage], out_path: str | Path, opt: Options, title: str = "",
              log: Progress = print) -> Path:
    out_path = Path(out_path)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    doc = pymupdf.open()
    embedded: dict[int, int] = {}  # id(SourceImage) -> image xref, so booklet halves share one image
    lossless = 0
    for p in pages:
        img = p.final_image()
        dpi = p.dpi or opt.dpi or FALLBACK_DPI
        w, h = img.width * 72.0 / dpi, img.height * 72.0 / dpi
        page = doc.new_page(width=w, height=h)
        if opt.color == "color" and p.src is not None:
            _place_original(doc, page, p, dpi, embedded)
            lossless += 1
        else:
            data, _ = _encode(img, opt)
            page.insert_image(page.rect, stream=data)
    if opt.color == "color":
        log(f"{out_path.name}: {lossless}/{len(pages)} page(s) keep the original scan image unchanged")
    doc.set_metadata({"title": title or out_path.stem, "producer": "ScanProcessor"})

    tmp = out_path.with_name(out_path.stem + ".__tmp__.pdf")
    doc.save(tmp, garbage=3, deflate=True)
    doc.close()

    if opt.ocr:
        scripts = [p.script for p in pages]
        if opt.ocr_language == "auto" and not any(scripts):  # auto-rotate was off: sample a few pages
            scripts = [detect_osd(p.final_image())[2] for p in pages[:3]]
        language = resolve_ocr_language(opt.ocr_language, scripts)
        log(f"{out_path.name}: OCR ({language})...")
        try:
            run_ocr(tmp, out_path, language)
            tmp.unlink(missing_ok=True)
        except Exception as ex:
            log(f"{out_path.name}: OCR failed, saving without text layer: {ex}")
            os.replace(tmp, out_path)
    else:
        os.replace(tmp, out_path)
    return out_path


def run_ocr(src: Path, dst: Path, language: str) -> None:
    env = dict(os.environ)
    exe = find_tesseract()
    if exe:
        env["PATH"] = str(Path(exe).parent) + os.pathsep + env.get("PATH", "")
    python = Path(sys.executable)
    if python.name.lower() == "pythonw.exe":
        python = python.with_name("python.exe")
    cmd = [str(python), "-m", "ocrmypdf", "--output-type", "pdf", "--optimize", "0",
           "-l", language, "--skip-text", "--quiet", str(src), str(dst)]
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", env=env, creationflags=NO_WINDOW)
    if r.returncode != 0:
        raise RuntimeError((r.stderr or r.stdout).strip().splitlines()[-1] if (r.stderr or r.stdout) else f"exit {r.returncode}")


def unique_path(folder: Path, stem: str, suffix: str = ".pdf") -> Path:
    p = folder / f"{stem}{suffix}"
    n = 1
    while p.exists():
        p = folder / f"{stem}_{n}{suffix}"
        n += 1
    return p


def finish(src: Path, out_dir: Path, archive_dir: Path, pages: list[OutPage], opt: Options,
           out_stem: str | None = None, log: Progress = print) -> Path:
    """Writes the processed PDF to out_dir and moves the original into archive_dir."""
    dst = unique_path(out_dir, out_stem or src.stem)
    write_pdf(pages, dst, opt, title=dst.stem, log=log)
    archive_dir.mkdir(parents=True, exist_ok=True)
    shutil.move(str(src), str(unique_path(archive_dir, src.stem, src.suffix)))
    log(f"{src.name}: saved {dst} ; original archived")
    return dst


if __name__ == "__main__":  # quick CLI: python -m scanproc.engine in.pdf out.pdf [layout] [--no-ocr]
    a = sys.argv[1:]
    o = Options(layout=a[2] if len(a) > 2 and not a[2].startswith("--") else "duplex", ocr="--no-ocr" not in a)
    write_pdf(process(a[0], o), a[1], o)
