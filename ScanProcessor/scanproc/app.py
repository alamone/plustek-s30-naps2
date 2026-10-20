"""ScanProcessor GUI: pick PDFs from the incoming scan folder and post-process them."""
from __future__ import annotations

import json
import os
import sys
import time
import traceback
from dataclasses import replace
from pathlib import Path

import pymupdf
from PIL import Image
from PySide6.QtCore import QObject, QRunnable, QSize, Qt, QThreadPool, QTimer, Signal, QFileSystemWatcher
from PySide6.QtGui import QAction, QIcon, QImage, QPixmap
from PySide6.QtWidgets import (
    QAbstractItemView, QApplication, QCheckBox, QComboBox, QDialog, QDoubleSpinBox, QFileDialog,
    QFormLayout, QGroupBox, QHBoxLayout, QHeaderView, QLabel, QLineEdit, QListWidget, QListWidgetItem,
    QMainWindow, QMenu, QMessageBox, QPlainTextEdit, QProgressBar, QPushButton, QScrollArea, QSpinBox,
    QSplitter, QTableWidget, QTableWidgetItem, QTabWidget, QToolBar, QVBoxLayout, QWidget,
)

from . import engine
from .engine import LAYOUTS, OCR_LANGUAGES, PRESETS, Options, OutPage

APP = "ScanProcessor"
SETTINGS_PATH = Path(os.environ.get("APPDATA", Path.home())) / APP / "settings.json"
THUMB = 180


def load_settings() -> dict:
    defaults = {
        "incoming": str(Path.home() / "Documents" / "Scans"),
        "output": str(Path.home() / "Documents" / "Scans" / "Processed"),
        "archive_subfolder": "_originals",
        "preset": "Duplex document",
        "options": Options().to_dict(),
        "version": 2,
    }
    try:
        saved = json.loads(SETTINGS_PATH.read_text(encoding="utf-8"))
        if saved.get("version", 1) < 2:  # v1 saved the old fixed 300 DPI default; switch to Auto
            saved.get("options", {})["dpi"] = 0
            saved["version"] = 2
        defaults.update(saved)
    except (OSError, ValueError):
        pass
    return defaults


def save_settings(s: dict) -> None:
    try:
        SETTINGS_PATH.parent.mkdir(parents=True, exist_ok=True)
        SETTINGS_PATH.write_text(json.dumps(s, indent=2), encoding="utf-8")
    except OSError:
        pass


def pil_to_qimage(img: Image.Image, max_side: int) -> QImage:
    img = img.copy()
    img.thumbnail((max_side, max_side), Image.BILINEAR)
    img = img.convert("RGB")
    data = img.tobytes()
    return QImage(data, img.width, img.height, img.width * 3, QImage.Format.Format_RGB888).copy()


# ------------------------------------------------------------------ background jobs

class JobSignals(QObject):
    log = Signal(str)
    done = Signal(object)
    failed = Signal(str)


class Job(QRunnable):
    """Runs fn(log) on the thread pool; fn's return value is delivered via signals.done."""

    def __init__(self, fn):
        super().__init__()
        self.fn = fn
        self.signals = JobSignals()

    def run(self):
        try:
            self.signals.done.emit(self.fn(self.signals.log.emit))
        except Exception as ex:  # report, never crash the GUI
            self.signals.failed.emit(f"{ex}\n{traceback.format_exc(limit=3)}")


def render_thumbnails(pdf: Path) -> list[QImage]:
    out = []
    with pymupdf.open(pdf) as doc:
        for page in doc:
            z = THUMB * 2 / max(page.rect.width, page.rect.height)
            pix = page.get_pixmap(matrix=pymupdf.Matrix(z, z))
            out.append(QImage(pix.samples, pix.width, pix.height, pix.stride,
                              QImage.Format.Format_RGB888).copy())
    return out


# ------------------------------------------------------------------ widgets

class PageView(QDialog):
    def __init__(self, parent, image: QImage, title: str):
        super().__init__(parent)
        self.setWindowTitle(title)
        self.resize(900, 1000)
        lbl = QLabel()
        lbl.setPixmap(QPixmap.fromImage(image))
        sc = QScrollArea()
        sc.setWidget(lbl)
        sc.setAlignment(Qt.AlignmentFlag.AlignCenter)
        lay = QVBoxLayout(self)
        lay.addWidget(sc)


class ThumbList(QListWidget):
    def __init__(self):
        super().__init__()
        self.setViewMode(QListWidget.ViewMode.IconMode)
        self.setIconSize(QSize(THUMB, THUMB))
        self.setResizeMode(QListWidget.ResizeMode.Adjust)
        self.setMovement(QListWidget.Movement.Static)
        self.setSpacing(6)
        self.setSelectionMode(QAbstractItemView.SelectionMode.ExtendedSelection)
        self.setWordWrap(True)

    def set_images(self, images: list[QImage], labels: list[str]):
        self.clear()
        for img, label in zip(images, labels):
            it = QListWidgetItem(QIcon(QPixmap.fromImage(img)), label)
            it.setSizeHint(QSize(THUMB + 20, THUMB + 40))
            self.addItem(it)


class OptionsPanel(QWidget):
    changed = Signal()

    def __init__(self):
        super().__init__()
        f = QFormLayout(self)
        f.setFieldGrowthPolicy(QFormLayout.FieldGrowthPolicy.AllNonFixedFieldsGrow)

        self.preset = QComboBox()
        self.preset.addItems(list(PRESETS))
        f.addRow("Preset", self.preset)

        self.layout_ = QComboBox()
        for k, v in LAYOUTS.items():
            self.layout_.addItem(v, k)
        f.addRow("Layout", self.layout_)

        self.sections = QLineEdit()
        self.sections.setPlaceholderText("optional, e.g. 1-13 booklet; 14-15 booklet; 16-17 single")
        self.sections.setToolTip("Split one scan into parts (page numbers are scanned pages).\n"
                                 "Types: booklet, duplex, single, manual_duplex. Pages not listed stay single.\n"
                                 "Leave empty to use the Layout for the whole file.")
        f.addRow("Sections", self.sections)

        self.duplex_swap = QCheckBox("Each sheet fed back side first (swap page pairs)")
        f.addRow("", self.duplex_swap)
        self.inner_first = QCheckBox("Booklet: innermost sheet scanned first")
        self.back_first = QCheckBox("Booklet: back side of each sheet scanned first")
        f.addRow("", self.inner_first)
        f.addRow("", self.back_first)

        self.crop = QCheckBox("Crop scanner border && deskew")
        self.rotate = QCheckBox("Auto-rotate pages (Tesseract)")
        self.blank = QCheckBox("Remove blank pages")
        self.blank_sens = QDoubleSpinBox()
        self.blank_sens.setDecimals(4); self.blank_sens.setRange(0, 0.05); self.blank_sens.setSingleStep(0.001)
        self.blank_sens.setToolTip("Max fraction of dark pixels for a page to count as blank")
        self.reverse = QCheckBox("Reverse page order")
        for w in (self.crop, self.rotate):
            f.addRow("", w)
        row = QHBoxLayout(); row.addWidget(self.blank); row.addWidget(QLabel("sensitivity")); row.addWidget(self.blank_sens)
        f.addRow("", row)
        f.addRow("", self.reverse)

        self.color = QComboBox()
        self.color.addItem("Color (original scan image, lossless)", "color")
        self.color.addItem("Grayscale (re-encoded, smaller)", "gray")
        self.color.addItem("Black && white (smallest)", "bw")
        self.color.setToolTip(
            "Color keeps the scanner's JPEG byte-for-byte: crop, deskew, rotation and booklet splitting\n"
            "are stored as PDF instructions, so no quality is lost. Grayscale and black & white create\n"
            "new images (black & white is stored losslessly as PNG).")
        f.addRow("Output color", self.color)
        self.quality = QSpinBox(); self.quality.setRange(30, 95)
        self.quality.setToolTip("Only used when pages are re-encoded (grayscale output)")
        f.addRow("JPEG quality", self.quality)
        self.dpi = QSpinBox(); self.dpi.setRange(0, 1200); self.dpi.setSingleStep(50)
        self.dpi.setSpecialValueText("Auto")
        self.dpi.setToolTip(
            "Sets how large pages are in the output PDF; it never rescales the image (pixel count is unchanged).\n"
            "Auto: use the resolution stored in the PDF, or 300 DPI when it is implausible\n"
            "(NAPS2 + the S30 WIA driver store ~8 DPI, i.e. 444-inch-wide pages).\n"
            "A number forces that resolution for every page.")
        f.addRow("Page size DPI", self.dpi)

        self.ocr = QCheckBox("OCR (searchable text)")
        self.ocr_lang = QComboBox()
        for k, v in OCR_LANGUAGES.items():
            self.ocr_lang.addItem(v, k)
        row2 = QHBoxLayout(); row2.addWidget(self.ocr); row2.addWidget(self.ocr_lang, 1)
        f.addRow("", row2)

        self._loading = False
        self.preset.currentTextChanged.connect(self._apply_preset)
        for w in (self.layout_, self.color, self.ocr_lang):
            w.currentIndexChanged.connect(self._on_change)
        for w in (self.duplex_swap, self.inner_first, self.back_first, self.crop, self.rotate, self.blank,
                  self.reverse, self.ocr):
            w.toggled.connect(self._on_change)
        for w in (self.blank_sens, self.quality, self.dpi):
            w.valueChanged.connect(self._on_change)
        self.sections.textChanged.connect(self._on_change)

    def _apply_preset(self, name: str):
        if self._loading or name not in PRESETS:
            return
        cur = self.options()
        p = PRESETS[name]
        # keep machine/quality settings, take layout-related ones from the preset
        self.set_options(replace(p, dpi=cur.dpi, jpeg_quality=cur.jpeg_quality, color=cur.color,
                                 ocr=cur.ocr, ocr_language=cur.ocr_language), keep_preset=True)

    def _on_change(self, *_):
        if self._loading:
            return
        self._update_enabled()
        self.changed.emit()

    def _update_enabled(self):
        lay = self.layout_.currentData()
        booklet = lay == "booklet" or "booklet" in self.sections.text().lower()
        self.inner_first.setEnabled(booklet)
        self.back_first.setEnabled(booklet)
        self.duplex_swap.setEnabled(lay == "duplex" or "duplex" in self.sections.text().lower())
        self.blank_sens.setEnabled(self.blank.isChecked())
        self.ocr_lang.setEnabled(self.ocr.isChecked())
        self.quality.setEnabled(self.color.currentData() == "gray")

    def set_options(self, o: Options, keep_preset: bool = False):
        self._loading = True
        try:
            self.layout_.setCurrentIndex(max(0, self.layout_.findData(o.layout)))
            self.sections.setText(o.sections)
            self.duplex_swap.setChecked(o.duplex_swap)
            self.inner_first.setChecked(o.booklet_inner_first)
            self.back_first.setChecked(o.booklet_back_first)
            self.crop.setChecked(o.crop_deskew)
            self.rotate.setChecked(o.auto_rotate)
            self.blank.setChecked(o.remove_blank)
            self.blank_sens.setValue(o.blank_sensitivity)
            self.reverse.setChecked(o.reverse_order)
            self.color.setCurrentIndex(max(0, self.color.findData(o.color)))
            self.quality.setValue(o.jpeg_quality)
            self.dpi.setValue(o.dpi)
            self.ocr.setChecked(o.ocr)
            self.ocr_lang.setCurrentIndex(max(0, self.ocr_lang.findData(o.ocr_language)))
        finally:
            self._loading = False
        self._update_enabled()
        self.changed.emit()

    def options(self) -> Options:
        return Options(
            layout=self.layout_.currentData(), dpi=self.dpi.value(), crop_deskew=self.crop.isChecked(),
            auto_rotate=self.rotate.isChecked(), remove_blank=self.blank.isChecked(),
            blank_sensitivity=self.blank_sens.value(), color=self.color.currentData(),
            jpeg_quality=self.quality.value(), ocr=self.ocr.isChecked(),
            ocr_language=self.ocr_lang.currentData(), booklet_inner_first=self.inner_first.isChecked(),
            booklet_back_first=self.back_first.isChecked(), duplex_swap=self.duplex_swap.isChecked(),
            sections=self.sections.text().strip(), reverse_order=self.reverse.isChecked(),
        )


# ------------------------------------------------------------------ main window

class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.settings = load_settings()
        self.pool = QThreadPool.globalInstance()
        self.pool.setMaxThreadCount(max(2, self.pool.maxThreadCount()))
        self.busy = 0
        self._jobs: set[Job] = set()  # keep jobs (and their signal objects) alive until they report back
        self.current: Path | None = None
        self.preview: tuple[Path, Options, list[OutPage]] | None = None  # last processed result
        self.thumb_cache: dict[tuple[str, float], list[QImage]] = {}

        self.setWindowTitle(APP)
        self.resize(1500, 900)
        self._build_ui()
        self.opts.preset.setCurrentText(self.settings.get("preset", "Duplex document"))
        self.opts.set_options(Options.from_dict(self.settings.get("options", {})))

        self.watcher = QFileSystemWatcher()
        self.watcher.directoryChanged.connect(lambda _: self.refresh_timer.start(1500))
        self.refresh_timer = QTimer(singleShot=True, timeout=self.refresh)
        self.poll = QTimer(interval=10000, timeout=self.refresh)  # network shares don't always notify
        self.poll.start()
        self.set_incoming(self.settings["incoming"])

    # ---------------------------------------------------------------- UI
    def _build_ui(self):
        tb = QToolBar("Main"); tb.setMovable(False); self.addToolBar(tb)
        self.folder_lbl = QLabel()
        tb.addWidget(QLabel(" Incoming: ")); tb.addWidget(self.folder_lbl)
        tb.addAction(QAction("Change…", self, triggered=self.choose_incoming))
        tb.addAction(QAction("Refresh", self, triggered=self.refresh))
        tb.addSeparator()
        self.out_lbl = QLabel()
        tb.addWidget(QLabel(" Output: ")); tb.addWidget(self.out_lbl)
        tb.addAction(QAction("Change…", self, triggered=self.choose_output))
        tb.addSeparator()
        tb.addAction(QAction("Open output folder", self, triggered=lambda: os.startfile(self.settings["output"])))

        # files
        self.table = QTableWidget(0, 4)
        self.table.setHorizontalHeaderLabels(["File", "Pages", "Size", "Modified"])
        self.table.horizontalHeader().setSectionResizeMode(0, QHeaderView.ResizeMode.Stretch)
        for c in (1, 2, 3):
            self.table.horizontalHeader().setSectionResizeMode(c, QHeaderView.ResizeMode.ResizeToContents)
        self.table.verticalHeader().hide()
        self.table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.table.setSelectionMode(QAbstractItemView.SelectionMode.SingleSelection)
        self.table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.table.itemSelectionChanged.connect(self.on_select)
        self.table.itemChanged.connect(lambda *_: self._update_buttons())
        btns = QHBoxLayout()
        for text, state in (("Check all", True), ("Check none", False)):
            b = QPushButton(text); b.clicked.connect(lambda _=False, s=state: self.check_all(s)); btns.addWidget(b)
        btns.addStretch()
        left = QWidget(); ll = QVBoxLayout(left); ll.setContentsMargins(0, 0, 0, 0)
        ll.addWidget(QLabel("Tick the PDFs to process; click one to preview it."))
        ll.addWidget(self.table); ll.addLayout(btns)

        # previews
        self.tabs = QTabWidget()
        self.orig_view = ThumbList()
        self.result_view = ThumbList()
        self.result_view.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        self.result_view.customContextMenuRequested.connect(self.result_menu)
        self.orig_view.itemDoubleClicked.connect(lambda it: self.show_page(self.orig_view, it))
        self.result_view.itemDoubleClicked.connect(lambda it: self.show_page(self.result_view, it))
        self.tabs.addTab(self.orig_view, "Scanned pages")
        res = QWidget(); rl = QVBoxLayout(res); rl.setContentsMargins(0, 0, 0, 0)
        edit = QHBoxLayout()
        for text, fn in (("⟲ Rotate left", lambda: self.edit_result(rot=-90)),
                         ("⟳ Rotate right", lambda: self.edit_result(rot=90)),
                         ("Rotate 180°", lambda: self.edit_result(rot=180)),
                         ("Delete page", lambda: self.edit_result(delete=True)),
                         ("Move earlier", lambda: self.edit_result(move=-1)),
                         ("Move later", lambda: self.edit_result(move=1))):
            b = QPushButton(text); b.clicked.connect(fn); edit.addWidget(b)
        edit.addStretch()
        rl.addLayout(edit); rl.addWidget(self.result_view)
        self.tabs.addTab(res, "Result preview")

        # options + actions
        self.opts = OptionsPanel()
        self.opts.changed.connect(self._options_changed)
        self.out_name = QLineEdit()
        self.out_name.setPlaceholderText("same as original")
        self.btn_preview = QPushButton("Preview selected file")
        self.btn_save = QPushButton("Save preview")
        self.btn_batch = QPushButton("Process checked files")
        self.btn_preview.clicked.connect(self.run_preview)
        self.btn_save.clicked.connect(self.save_preview)
        self.btn_batch.clicked.connect(self.run_batch)
        right = QWidget(); rv = QVBoxLayout(right)
        g = QGroupBox("Processing"); gl = QVBoxLayout(g); gl.addWidget(self.opts); rv.addWidget(g)
        g2 = QGroupBox("Output"); fl = QFormLayout(g2); fl.addRow("File name", self.out_name); rv.addWidget(g2)
        rv.addWidget(self.btn_preview); rv.addWidget(self.btn_save); rv.addWidget(self.btn_batch)
        rv.addStretch()
        rscroll = QScrollArea(); rscroll.setWidget(right); rscroll.setWidgetResizable(True); rscroll.setMinimumWidth(520)

        split = QSplitter(); split.addWidget(left); split.addWidget(self.tabs); split.addWidget(rscroll)
        split.setStretchFactor(0, 2); split.setStretchFactor(1, 5); split.setStretchFactor(2, 0)
        split.setSizes([360, 620, 520])

        self.logbox = QPlainTextEdit(); self.logbox.setReadOnly(True); self.logbox.setMaximumBlockCount(2000)
        self.progress = QProgressBar(); self.progress.setRange(0, 0); self.progress.hide()
        vsplit = QSplitter(Qt.Orientation.Vertical); vsplit.addWidget(split); vsplit.addWidget(self.logbox)
        vsplit.setSizes([720, 160])
        central = QWidget(); cl = QVBoxLayout(central); cl.addWidget(vsplit); cl.addWidget(self.progress)
        self.setCentralWidget(central)
        self._update_buttons()

    def log(self, msg: str):
        self.logbox.appendPlainText(f"{time.strftime('%H:%M:%S')}  {msg}")

    # ---------------------------------------------------------------- folders
    def choose_incoming(self):
        d = QFileDialog.getExistingDirectory(self, "Incoming scans folder", self.settings["incoming"])
        if d:
            self.set_incoming(d)

    def choose_output(self):
        d = QFileDialog.getExistingDirectory(self, "Output folder", self.settings["output"])
        if d:
            self.settings["output"] = d
            self.out_lbl.setText(d)
            save_settings(self.settings)

    def set_incoming(self, d: str):
        if self.watcher.directories():
            self.watcher.removePaths(self.watcher.directories())
        self.settings["incoming"] = d
        save_settings(self.settings)
        self.folder_lbl.setText(d)
        self.out_lbl.setText(self.settings["output"])
        if os.path.isdir(d):
            self.watcher.addPath(d)
        self.refresh()

    def incoming_files(self) -> list[Path]:
        try:
            return sorted((p for p in Path(self.settings["incoming"]).glob("*.pdf") if p.is_file()),
                          key=lambda p: p.stat().st_mtime)
        except OSError:
            return []

    def refresh(self):
        files = self.incoming_files()
        checked = {self.table.item(r, 0).data(Qt.ItemDataRole.UserRole)
                   for r in range(self.table.rowCount())
                   if self.table.item(r, 0).checkState() == Qt.CheckState.Checked}
        names = [str(p) for p in files]
        try:
            stamp = [(str(p), p.stat().st_size, p.stat().st_mtime) for p in files]
        except OSError:
            stamp = []
        if stamp == getattr(self, "_stamp", None):
            return
        self._stamp = stamp
        sel = str(self.current) if self.current else None
        self.table.blockSignals(True)
        self.table.setRowCount(0)
        for p in files:
            r = self.table.rowCount(); self.table.insertRow(r)
            st = p.stat()
            it = QTableWidgetItem(p.name)
            it.setData(Qt.ItemDataRole.UserRole, str(p))
            it.setFlags(it.flags() | Qt.ItemFlag.ItemIsUserCheckable)
            it.setCheckState(Qt.CheckState.Checked if str(p) in checked else Qt.CheckState.Unchecked)
            self.table.setItem(r, 0, it)
            try:
                with pymupdf.open(p) as d:
                    pages = str(d.page_count)
            except Exception:
                pages = "?"
            self.table.setItem(r, 1, QTableWidgetItem(pages))
            self.table.setItem(r, 2, QTableWidgetItem(f"{st.st_size / 1e6:.1f} MB"))
            self.table.setItem(r, 3, QTableWidgetItem(time.strftime("%Y-%m-%d %H:%M", time.localtime(st.st_mtime))))
            if str(p) == sel:
                self.table.selectRow(r)
        self.table.blockSignals(False)
        if sel and sel not in names:
            self.current = None
            self.orig_view.clear()
        self._update_buttons()

    def checked_files(self) -> list[Path]:
        return [Path(self.table.item(r, 0).data(Qt.ItemDataRole.UserRole)) for r in range(self.table.rowCount())
                if self.table.item(r, 0).checkState() == Qt.CheckState.Checked]

    def check_all(self, state: bool):
        for r in range(self.table.rowCount()):
            self.table.item(r, 0).setCheckState(Qt.CheckState.Checked if state else Qt.CheckState.Unchecked)

    # ---------------------------------------------------------------- selection / thumbnails
    def on_select(self):
        rows = self.table.selectionModel().selectedRows()
        if not rows:
            return
        p = Path(self.table.item(rows[0].row(), 0).data(Qt.ItemDataRole.UserRole))
        if p == self.current:
            return
        self.current = p
        self.out_name.clear()
        self.tabs.setCurrentIndex(0)
        if self.preview and self.preview[0] != p:
            self.result_view.clear()
        key = (str(p), p.stat().st_mtime if p.exists() else 0)
        if key in self.thumb_cache:
            self._show_orig(p, self.thumb_cache[key])
            return
        self.orig_view.clear()
        job = Job(lambda log: render_thumbnails(p))
        job.signals.done.connect(lambda imgs, p=p, key=key: (self.thumb_cache.__setitem__(key, imgs), self._show_orig(p, imgs)))
        job.signals.failed.connect(lambda e: self.log(f"Can't read {p.name}: {e.splitlines()[0]}"))
        self._submit(job)
        self._update_buttons()

    def _show_orig(self, p: Path, imgs: list[QImage]):
        if p == self.current:
            self.orig_view.set_images(imgs, [f"scan p{i}" for i in range(1, len(imgs) + 1)])

    def show_page(self, view: ThumbList, item: QListWidgetItem):
        row = view.row(item)
        if view is self.result_view and self.preview:
            pg = self.preview[2][row]
            img = pil_to_qimage(pg.final_image(), 1600)
        elif self.current:
            with pymupdf.open(self.current) as d:
                page = d[row]
                z = 1600 / max(page.rect.width, page.rect.height)
                pix = page.get_pixmap(matrix=pymupdf.Matrix(z, z))
                img = QImage(pix.samples, pix.width, pix.height, pix.stride, QImage.Format.Format_RGB888).copy()
        else:
            return
        PageView(self, img, item.text()).show()

    # ---------------------------------------------------------------- processing
    def _options_changed(self):
        self.settings["preset"] = self.opts.preset.currentText()
        self.settings["options"] = self.opts.options().to_dict()
        save_settings(self.settings)
        self._update_buttons()

    def _update_buttons(self):
        idle = self.busy == 0
        n = len(self.checked_files()) if hasattr(self, "table") else 0
        self.btn_preview.setEnabled(idle and self.current is not None)
        fresh = self.preview is not None and self.preview[0] == self.current
        self.btn_save.setEnabled(idle and fresh)
        self.btn_save.setText("Save preview" + (f"  ({self.current.name})" if fresh and self.current else ""))
        self.btn_batch.setEnabled(idle and n > 0)
        self.btn_batch.setText(f"Process checked files ({n})")
        self.out_name.setEnabled(self.current is not None)

    def _start(self, fn, on_done):
        self.busy += 1
        self.progress.show()
        self._update_buttons()
        job = Job(fn)
        job.signals.log.connect(self.log)

        def finished(result=None, error=None):
            self.busy -= 1
            if self.busy == 0:
                self.progress.hide()
            if error:
                self.log("ERROR: " + error)
                QMessageBox.warning(self, APP, error.splitlines()[0])
            else:
                on_done(result)
            self._update_buttons()
            self.refresh()

        job.signals.done.connect(lambda r: finished(result=r))
        job.signals.failed.connect(lambda e: finished(error=e))
        self._submit(job)

    def _submit(self, job: "Job"):
        self._jobs.add(job)
        job.setAutoDelete(False)
        job.signals.done.connect(lambda *_: self._jobs.discard(job))
        job.signals.failed.connect(lambda *_: self._jobs.discard(job))
        self.pool.start(job)

    def _validate(self, opt: Options, pdf: Path) -> bool:
        if opt.sections:
            try:
                with pymupdf.open(pdf) as d:
                    engine.parse_sections(opt.sections, d.page_count)
            except ValueError as ex:
                QMessageBox.warning(self, APP, f"Sections: {ex}")
                return False
        return True

    def run_preview(self):
        if not self.current:
            return
        p, opt = self.current, self.opts.options()
        if not self._validate(opt, p):
            return
        self.log(f"Preview {p.name} [{LAYOUTS[opt.layout]}{'; sections ' + opt.sections if opt.sections else ''}]")

        def work(log):
            return p, opt, engine.process(p, opt, log)

        self._start(work, self._show_preview)

    def _show_preview(self, result):
        p, opt, pages = result
        self.preview = (p, opt, pages)
        self._refresh_result_view()
        self.tabs.setCurrentIndex(1)

    def _refresh_result_view(self, select: list[int] | None = None):
        if not self.preview:
            return
        pages = self.preview[2]
        self.result_view.set_images([pil_to_qimage(pg.final_image(), THUMB * 2) for pg in pages],
                                    [f"{i}: {pg.label}" for i, pg in enumerate(pages, 1)])
        for i in select or []:
            if 0 <= i < self.result_view.count():
                self.result_view.item(i).setSelected(True)

    def result_menu(self, pos):
        if not self.result_view.itemAt(pos):
            return
        m = QMenu(self)
        m.addAction("Rotate left", lambda: self.edit_result(rot=-90))
        m.addAction("Rotate right", lambda: self.edit_result(rot=90))
        m.addAction("Rotate 180°", lambda: self.edit_result(rot=180))
        m.addSeparator()
        m.addAction("Move earlier", lambda: self.edit_result(move=-1))
        m.addAction("Move later", lambda: self.edit_result(move=1))
        m.addSeparator()
        m.addAction("Delete page", lambda: self.edit_result(delete=True))
        m.exec(self.result_view.mapToGlobal(pos))

    def edit_result(self, rot: int = 0, delete: bool = False, move: int = 0):
        if not self.preview:
            return
        pages = self.preview[2]
        rows = sorted({self.result_view.row(i) for i in self.result_view.selectedItems()})
        if not rows:
            return
        if delete:
            for r in reversed(rows):
                del pages[r]
            self._refresh_result_view()
            return
        if move:
            order = rows if move < 0 else list(reversed(rows))
            new = []
            for r in order:
                t = r + move
                if 0 <= t < len(pages) and t not in new:
                    pages[r], pages[t] = pages[t], pages[r]
                    new.append(t)
                else:
                    new.append(r)
            self._refresh_result_view(new)
            return
        for r in rows:
            pages[r].rotation = (pages[r].rotation + rot) % 360
        self._refresh_result_view(rows)

    def _dirs(self, src: Path) -> tuple[Path, Path]:
        return Path(self.settings["output"]), src.parent / self.settings["archive_subfolder"]

    def save_preview(self):
        if not self.preview or self.preview[0] != self.current:
            return
        src, opt, pages = self.preview
        if not pages:
            QMessageBox.warning(self, APP, "The preview has no pages left.")
            return
        out_stem = self.out_name.text().strip() or None
        out_dir, archive = self._dirs(src)
        # Output settings (colour / quality / OCR) may be changed after previewing without re-running.
        cur = self.opts.options()
        opt = replace(opt, color=cur.color, jpeg_quality=cur.jpeg_quality, dpi=cur.dpi,
                      ocr=cur.ocr, ocr_language=cur.ocr_language)

        def work(log):
            return engine.finish(src, out_dir, archive, pages, opt, out_stem, log)

        def done(dst):
            self.preview = None
            self.result_view.clear()
            self.current = None
            self.orig_view.clear()
            self.log(f"Done: {dst}")

        self._start(work, done)

    def run_batch(self):
        files = self.checked_files()
        if not files:
            return
        opt = self.opts.options()
        for f in files:
            if not self._validate(opt, f):
                return
        if QMessageBox.question(
                self, APP, f"Process {len(files)} file(s) with '{LAYOUTS[opt.layout]}'"
                f"{' (sections: ' + opt.sections + ')' if opt.sections else ''}?\n\n"
                f"Results go to {self.settings['output']}; originals move to "
                f"{self.settings['archive_subfolder']}\\.") != QMessageBox.StandardButton.Yes:
            return
        preview = self.preview
        out_dir = Path(self.settings["output"])
        archive_name = self.settings["archive_subfolder"]

        def work(log):
            done = []
            for i, f in enumerate(files, 1):
                log(f"[{i}/{len(files)}] {f.name}")
                if not f.exists():
                    log(f"{f.name}: no longer exists, skipped")
                    continue
                if time.time() - f.stat().st_mtime < 5:
                    log(f"{f.name}: modified just now (still being written?), skipped")
                    continue
                # Reuse a reviewed preview (with manual edits) if its layout options match.
                if preview and preview[0] == f and replace(preview[1], color=opt.color, jpeg_quality=opt.jpeg_quality,
                                                            ocr=opt.ocr, ocr_language=opt.ocr_language) == opt:
                    pages = preview[2]
                    log(f"{f.name}: using the reviewed preview")
                else:
                    pages = engine.process(f, opt, log)
                done.append(engine.finish(f, out_dir, f.parent / archive_name, pages, opt, None, log))
            return done

        def finished(done):
            self.preview = None
            self.result_view.clear()
            self.log(f"Batch finished: {len(done)} file(s) written to {out_dir}")

        self._start(work, finished)

    def closeEvent(self, e):
        if self.busy and QMessageBox.question(self, APP, "Processing is still running. Quit anyway?") \
                != QMessageBox.StandardButton.Yes:
            e.ignore()
            return
        save_settings(self.settings)
        super().closeEvent(e)


def main():
    app = QApplication(sys.argv)
    app.setApplicationName(APP)
    w = MainWindow()
    w.show()
    if not engine.find_tesseract():
        w.log("Tesseract not found: auto-rotate and OCR are unavailable. Install it from "
              "https://github.com/UB-Mannheim/tesseract/wiki")
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
