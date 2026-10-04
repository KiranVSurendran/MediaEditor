import json
import os
import re
import subprocess
import sys
import shutil
import tempfile
import threading
from pathlib import Path

if os.name == 'nt' and hasattr(os, 'add_dll_directory'):
    _bundle_root = Path(getattr(sys, '_MEIPASS', Path(sys.executable).parent))
    for _dll_dir in (_bundle_root / 'PySide6', _bundle_root / '_internal' / 'PySide6'):
        if _dll_dir.is_dir():
            os.add_dll_directory(str(_dll_dir))

from PySide6.QtCore import QObject, QProcess, QSettings, QSize, QUrl, Qt, QTimer, Signal
from PySide6.QtGui import QColor, QDragEnterEvent, QDropEvent, QIcon, QImage, QPainter, QPen
from PySide6.QtMultimedia import QAudioOutput, QMediaPlayer
from PySide6.QtMultimediaWidgets import QVideoWidget
from PySide6.QtWidgets import (
    QApplication, QComboBox, QDoubleSpinBox, QFileDialog, QFormLayout, QFrame,
    QHBoxLayout, QLabel, QLineEdit, QListWidget, QListWidgetItem, QMainWindow,
    QMessageBox, QPushButton, QProgressBar, QSizePolicy, QSlider, QSpinBox, QStackedWidget,
    QVBoxLayout, QWidget,
)

import Media_Toolbox as core
from media_models import MediaItem, inspect_media, validate_crop, validate_selection


class CropPreview(QVideoWidget):
    cropChanged = Signal(int, int, int, int)

    def __init__(self):
        super().__init__()
        self.setMinimumHeight(300)
        self.rect_source = [0, 0, 0, 0]
        self.source_size = (1, 1)
        self.drag_start = None

    def set_source_size(self, w, h):
        self.source_size = (max(1, w), max(1, h))
        self.update()

    def set_rect_source(self, rect):
        self.rect_source = list(rect)
        self.update()

    def _map(self, pos):
        w, h = self.source_size
        ox, oy, dw, dh = self._content_rect()
        return max(0, min(w, int((pos.x() - ox) * w / max(1, dw)))), max(0, min(h, int((pos.y() - oy) * h / max(1, dh))))

    def _content_rect(self):
        sw, sh = self.source_size; scale = min(self.width() / sw, self.height() / sh); dw, dh = sw * scale, sh * scale
        return (self.width() - dw) / 2, (self.height() - dh) / 2, dw, dh

    def mousePressEvent(self, event):
        if event.button() == Qt.MouseButton.LeftButton:
            self.drag_start = self._map(event.position())
        super().mousePressEvent(event)

    def mouseMoveEvent(self, event):
        if self.drag_start:
            x0, y0 = self.drag_start
            x1, y1 = self._map(event.position())
            x, y = min(x0, x1) // 2 * 2, min(y0, y1) // 2 * 2
            w, h = max(2, abs(x1 - x0) // 2 * 2), max(2, abs(y1 - y0) // 2 * 2)
            self.rect_source = [x, y, w, h]
            self.cropChanged.emit(*self.rect_source)
            self.update()
        super().mouseMoveEvent(event)

    def mouseReleaseEvent(self, event):
        self.drag_start = None
        super().mouseReleaseEvent(event)

    def paintEvent(self, event):
        super().paintEvent(event)
        if self.rect_source[2] <= 0:
            return
        p = QPainter(self)
        p.setPen(QPen(QColor('#55aaff'), 3))
        x, y, w, h = self.rect_source
        sw, sh = self.source_size; ox, oy, dw, dh = self._content_rect()
        p.drawRect(int(ox + x * dw / sw), int(oy + y * dh / sh), int(w * dw / sw), int(h * dh / sh))


class RangeSlider(QWidget):
    rangeChanged = Signal(float, float)

    def __init__(self):
        super().__init__(); self.setMinimumHeight(42); self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground); self.minimum = 0.0; self.maximum = 1.0; self.lower = 0.0; self.upper = 1.0; self.handle = None; self.thumbnail = QImage()

    def setRange(self, minimum, maximum):
        self.minimum, self.maximum = float(minimum), max(float(maximum), float(minimum) + .001); self.lower = self.minimum; self.upper = self.maximum; self.update()
    def setValues(self, lower, upper):
        self.lower, self.upper = max(self.minimum, float(lower)), min(self.maximum, float(upper)); self.update()
    def setThumbnail(self, data):
        image = QImage(); image.loadFromData(data, 'PNG'); self.thumbnail = image; self.update()
    def _x(self, value): return 10 + (self.width() - 20) * (value - self.minimum) / (self.maximum - self.minimum)
    def _value(self, x): return self.minimum + max(0, min(self.width() - 20, x - 10)) / max(1, self.width() - 20) * (self.maximum - self.minimum)
    def paintEvent(self, event):
        p = QPainter(self); y = self.height() // 2; left, right = 10, self.width() - 10
        if not self.thumbnail.isNull():
            p.drawImage(left, 3, self.thumbnail.scaled(right-left, self.height()-6, Qt.AspectRatioMode.IgnoreAspectRatio, Qt.TransformationMode.SmoothTransformation))
            p.fillRect(left, 3, max(0, int(self._x(self.lower))-left), self.height()-6, QColor(5, 12, 24, 165)); p.fillRect(int(self._x(self.upper)), 3, max(0, right-int(self._x(self.upper))), self.height()-6, QColor(5, 12, 24, 165))
        else:
            p.setPen(QPen(QColor('#52627a'), 5)); p.drawLine(left, y, right, y)
        p.setPen(QPen(QColor('#60a5fa'), 3)); p.drawLine(int(self._x(self.lower)), y, int(self._x(self.upper)), y); p.setBrush(QColor('#dbeafe')); p.setPen(QPen(QColor('#60a5fa'), 2)); p.drawEllipse(int(self._x(self.lower))-7, y-7, 14, 14); p.drawEllipse(int(self._x(self.upper))-7, y-7, 14, 14)
    def mousePressEvent(self, event):
        x = event.position().x(); self.handle = 'lower' if abs(x-self._x(self.lower)) <= abs(x-self._x(self.upper)) else 'upper'; self._move(x)
    def mouseMoveEvent(self, event):
        if self.handle: self._move(event.position().x())
    def mouseReleaseEvent(self, event): self.handle = None
    def _move(self, x):
        value = self._value(x)
        if self.handle == 'lower': self.lower = min(value, self.upper - .001)
        elif self.handle == 'upper': self.upper = max(value, self.lower + .001)
        self.update(); self.rangeChanged.emit(self.lower, self.upper)


class FFmpegJob(QObject):
    progress = Signal(int, str)
    finished = Signal(bool, str)

    def __init__(self, commands):
        super().__init__()
        self.commands = commands
        self.index = 0
        self.process = None
        self.cancelled = False

    def start(self):
        self._next()

    def cancel(self):
        self.cancelled = True
        if self.process:
            self.process.kill()

    def _next(self):
        if self.cancelled:
            self.finished.emit(False, 'Cancelled')
            return
        if self.index >= len(self.commands):
            self.finished.emit(True, 'Completed')
            return
        self.process = QProcess(self)
        self.process.setProgram(self.commands[self.index][0])
        self.process.setArguments(['-progress', 'pipe:1', '-nostats'] + self.commands[self.index][1:])
        self.process.setProcessChannelMode(QProcess.ProcessChannelMode.MergedChannels)
        self.process.readyReadStandardOutput.connect(self._read)
        self.process.finished.connect(self._done)
        self.process.start()

    def _read(self):
        text = bytes(self.process.readAllStandardOutput()).decode(errors='ignore')
        match = re.search(r'out_time_us=(\d+)', text)
        if match:
            # The selected media duration is reflected by the seek control; this
            # event keeps the UI moving while FFmpeg reports its output time.
            self.progress.emit(min(99, int(int(match.group(1)) / 1_000_000) % 100), 'Processing…')

    def _done(self, code, _status):
        if code != 0:
            message = bytes(self.process.readAllStandardOutput()).decode(errors='ignore').strip() or 'FFmpeg failed.'
            self.finished.emit(False, message[-1200:])
            return
        self.index += 1
        self.progress.emit(int(self.index / len(self.commands) * 100), f'Completed {self.index} of {len(self.commands)}')
        self._next()


class MediaWindow(QMainWindow):
    TOOLS = ['Extract Audio', 'Trim Audio', 'Trim Video', 'Crop Frame', 'Join Videos']
    thumbnailReady = Signal(bytes)
    joinThumbnailReady = Signal(int, bytes)
    def __init__(self):
        super().__init__()
        self.setWindowTitle('Media Toolbox')
        self.resize(1180, 820)
        self.setAcceptDrops(True)
        self.settings = QSettings('MediaToolbox', 'MediaToolbox')
        self.paths = []
        self.metadata = {}
        self.items = {}
        self.track_choices = {}
        self.job = None
        self.job_tempfiles = []
        self._build()
        self._theme(bool(self.settings.value('dark', True, type=bool)))

    def _build(self):
        central = QWidget(); self.setCentralWidget(central)
        outer = QHBoxLayout(central); outer.setContentsMargins(24, 20, 24, 20); outer.setSpacing(22)
        self.nav = QListWidget(); self.nav.setObjectName('Sidebar'); self.nav.setFixedWidth(210); self.nav.addItems(['🎵  Extract Audio', '✂  Trim Audio', '▶  Trim Video', '▣  Crop Frame', '🔗  Join Videos']); self.nav.currentRowChanged.connect(self._tool_changed); self.nav.setCurrentRow(0); outer.addWidget(self.nav)
        content = QVBoxLayout(); outer.addLayout(content, 1)
        header = QHBoxLayout(); content.addLayout(header)
        self.title = QLabel('Extract Audio'); self.title.setObjectName('Title'); header.addWidget(self.title)
        header.addStretch(); self.theme_btn = QPushButton('☼ Theme'); self.theme_btn.clicked.connect(self._toggle_theme); header.addWidget(self.theme_btn)
        self.subtitle = QLabel('Extract original audio without re-encoding.'); self.subtitle.setObjectName('Muted'); content.addWidget(self.subtitle)
        preview_card = QFrame(); preview_card.setObjectName('PreviewCard'); preview_layout = QVBoxLayout(preview_card); preview_layout.setContentsMargins(10, 10, 10, 10); self.video = CropPreview(); self.video.setObjectName('Preview'); self.video.setMinimumHeight(280); self.video.setMaximumHeight(500); self.video.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Fixed); self.video.cropChanged.connect(self._crop_from_preview); preview_layout.addWidget(self.video); content.addWidget(preview_card, 0, Qt.AlignmentFlag.AlignTop)
        self.player = QMediaPlayer(self); self.audio = QAudioOutput(self); self.player.setAudioOutput(self.audio); self.player.setVideoOutput(self.video)
        transport = QHBoxLayout(); self.play = QPushButton('▶ Play'); self.play.clicked.connect(self._play); transport.addWidget(self.play)
        self.trim_bar = RangeSlider(); self.trim_bar.rangeChanged.connect(self._range_changed)
        self.thumbnailReady.connect(self.trim_bar.setThumbnail)
        self.joinThumbnailReady.connect(self._set_join_thumbnail)
        timeline = QWidget(); timeline_layout = QHBoxLayout(timeline); timeline_layout.setContentsMargins(0, 0, 0, 0); timeline_stack = QStackedWidget(); timeline_stack.setMinimumHeight(34); seek_page = QWidget(); seek_layout = QVBoxLayout(seek_page); seek_layout.setContentsMargins(0, 0, 0, 0); self.seek = QSlider(Qt.Orientation.Horizontal); self.seek.sliderMoved.connect(self.player.setPosition); seek_layout.addWidget(self.seek); timeline_stack.addWidget(seek_page); trim_page = QWidget(); trim_layout = QVBoxLayout(trim_page); trim_layout.setContentsMargins(0, 0, 0, 0); trim_layout.addWidget(self.trim_bar); timeline_stack.addWidget(trim_page); timeline_layout.addWidget(timeline_stack, 1); self.timeline_stack = timeline_stack; transport.addWidget(timeline, 1)
        self.time = QLabel('00:00:00 / 00:00:00'); transport.addWidget(self.time); content.addLayout(transport)
        self.player.positionChanged.connect(lambda p: self.seek.setValue(p)); self.player.durationChanged.connect(lambda d: self.seek.setRange(0, d)); self.player.positionChanged.connect(self._time)
        file_card = QFrame(); file_card.setObjectName('Card'); file_row = QHBoxLayout(file_card); file_row.setContentsMargins(10, 8, 10, 8); self.file_edit = QLineEdit(); self.file_edit.setPlaceholderText('Drop media files here or browse…'); file_row.addWidget(self.file_edit, 1); b = QPushButton('Browse files'); b.clicked.connect(self._browse); file_row.addWidget(b); content.addWidget(file_card)
        self.details = QLabel('No media selected'); self.details.setObjectName('Muted'); content.addWidget(self.details)
        self.stack = QStackedWidget(); content.addWidget(self.stack)
        self._make_extract(); self._make_trim(); self._make_trim(video=True); self._make_crop(); self._make_join()
        output_card = QFrame(); output = QHBoxLayout(output_card); output.setContentsMargins(10, 8, 10, 8); self.folder = QLineEdit(str(self.settings.value('folder', ''))); self.folder.setPlaceholderText('Output folder (defaults to input folder)'); output.addWidget(self.folder, 1); fb = QPushButton('Choose folder'); fb.clicked.connect(self._folder); output.addWidget(fb); self.name = QLineEdit(); self.name.setPlaceholderText('Optional filename'); output.addWidget(self.name); self.container = QComboBox(); self.container.addItems(['Auto', 'MP4', 'MKV']); output.addWidget(self.container); content.addWidget(output_card)
        actions = QHBoxLayout(); self.run_btn = QPushButton('Extract Audio'); self.run_btn.setObjectName('Primary'); self.run_btn.clicked.connect(self._run); actions.addWidget(self.run_btn, 1); self.cancel_btn = QPushButton('Cancel'); self.cancel_btn.setEnabled(False); self.cancel_btn.clicked.connect(self._cancel); actions.addWidget(self.cancel_btn); content.addLayout(actions)
        self.progress = QProgressBar(); content.addWidget(self.progress); self.status = QLabel('Ready'); content.addWidget(self.status)

    def _page(self):
        w = QWidget(); w.setLayout(QVBoxLayout()); self.stack.addWidget(w); return w.layout()
    def _make_extract(self):
        l = self._page(); self.extract_list = QListWidget(); self.extract_list.setMaximumHeight(120); self.extract_list.currentRowChanged.connect(self._extract_select); l.addWidget(QLabel('Batch queue')); l.addWidget(self.extract_list); self.track = QComboBox(); self.track.currentIndexChanged.connect(self._remember_track); l.addWidget(QLabel('Audio track for selected file')); l.addWidget(self.track); self.batch = QLabel('Each queued file keeps its own track choice. Select a file in the queue before changing this option.'); l.addWidget(self.batch); l.addStretch()
    def _make_trim(self, video=False):
        l = self._page(); form = QFormLayout(); self.start = QLineEdit('00:00:00'); self.end = QLineEdit('00:00:00'); self.start.textChanged.connect(self._sync_range_from_fields); self.end.textChanged.connect(self._sync_range_from_fields); self.mode = QComboBox(); self.mode.addItems(['Fast • stream copy', 'Precise • re-encode']); form.addRow('Start (HH:MM:SS)', self.start); form.addRow('End (HH:MM:SS)', self.end); form.addRow('Mode', self.mode); l.addLayout(form); l.addStretch()
        if video: self.video_mode = self.mode
        else: self.audio_mode = self.mode
    def _make_crop(self):
        l = self._page(); form = QFormLayout(); self.crop = [QSpinBox() for _ in range(4)]
        for s, n in zip(self.crop, ['X', 'Y', 'Width', 'Height']): s.setRange(0, 100000); form.addRow(n, s); s.valueChanged.connect(self._crop_fields)
        self.aspect = QComboBox(); self.aspect.addItems(['Free', '1:1', '9:16', '16:9', '4:3']); form.addRow('Preset', self.aspect); self.aspect.currentTextChanged.connect(self._crop_preset); l.addLayout(form); l.addWidget(QLabel('Drag on the preview to select the crop area.')); l.addStretch()
    def _make_join(self):
        l = self._page(); self.join_list = QListWidget(); self.join_list.setIconSize(QSize(120, 68)); self.join_list.setMinimumHeight(150); self.join_list.setSelectionMode(QListWidget.SelectionMode.SingleSelection); l.addWidget(QLabel('Arrange clips in order')); l.addWidget(self.join_list); row = QHBoxLayout(); add = QPushButton('Add Videos…'); add.clicked.connect(self._browse_join); remove = QPushButton('Remove'); remove.clicked.connect(self._remove_join); up = QPushButton('Move up'); up.clicked.connect(lambda: self._move(-1)); down = QPushButton('Move down'); down.clicked.connect(lambda: self._move(1)); row.addWidget(add); row.addWidget(remove); row.addWidget(up); row.addWidget(down); l.addLayout(row); self.join_mode = QComboBox(); self.join_mode.addItems(['Fast Join • matching streams', 'Convert and Join • normalize clips']); l.addWidget(self.join_mode)

    def _tool_changed(self, row):
        if row < 0: return
        if not hasattr(self, 'stack'):
            return
        self.stack.setCurrentIndex(row); title = self.TOOLS[row]; self.title.setText(title); self.run_btn.setText(title); self.video.setEnabled(title == 'Crop Frame' or bool(self.metadata))
        subtitles = ['Extract original audio without re-encoding.', 'Choose a range; fast mode keeps the source packets.', 'Trim video quickly or re-encode for exact frame timing.', 'Drag a rectangle over the preview to crop video pixels.', 'Arrange clips and join compatible streams, or convert mismatches.']
        self.subtitle.setText(subtitles[row])
        self.container.setEnabled(title in ('Trim Video', 'Crop Frame', 'Join Videos'))
        trim_timeline = title in ('Trim Audio', 'Trim Video')
        self.timeline_stack.setCurrentIndex(1 if trim_timeline else 0)
        self._refresh_controls()
    def _refresh_controls(self):
        if not self.paths: return
        self.file_edit.setText(self.paths[0]); self._inspect(self.paths[0])
    def _browse(self):
        paths, _ = QFileDialog.getOpenFileNames(self, 'Choose media', '', 'Media files (*.mp4 *.mkv *.mov *.webm *.m4v *.mp3 *.m4a *.wav *.flac *.ogg *.opus);;All files (*)')
        if paths: self._set_paths((self.paths + list(paths)) if self.TOOLS[self.nav.currentRow()] in ('Extract Audio', 'Join Videos') else list(paths))

    def _browse_join(self):
        paths, _ = QFileDialog.getOpenFileNames(self, 'Add videos to join', '', 'Video files (*.mp4 *.mkv *.mov *.webm *.m4v);;All files (*)')
        if paths: self._set_paths(self.paths + list(paths))

    def _remove_join(self):
        row = self.join_list.currentRow()
        if 0 <= row < len(self.paths): self._set_paths(self.paths[:row] + self.paths[row + 1:])
    def _set_paths(self, paths):
        self.paths = list(dict.fromkeys([os.path.abspath(p) for p in paths if os.path.isfile(p)])); self.file_edit.setText(self.paths[0] if self.paths else ''); self._inspect(self.paths[0] if self.paths else '')
        for path in self.paths:
            try:
                self.items[path] = inspect_media(path)[0]
                self.track_choices.setdefault(path, None)
            except Exception:
                pass
        self.join_list.clear(); self.join_list.addItems([os.path.basename(p) for p in self.paths]); self._tool_changed(self.nav.currentRow())
        if hasattr(self, 'extract_list'):
            self.extract_list.clear(); self.extract_list.addItems([os.path.basename(p) for p in self.paths]);
            if self.paths: self.extract_list.setCurrentRow(0)
        for index, path in enumerate(self.paths):
            self._load_join_thumbnail(index, path)

    def _extract_select(self, row):
        if 0 <= row < len(self.paths) and not self.job:
            self._inspect(self.paths[row])

    def _load_join_thumbnail(self, index, path):
        def worker():
            try:
                result = subprocess.run([core.ffmpeg_path(), '-v', 'error', '-i', path, '-frames:v', '1', '-vf', 'scale=120:68:force_original_aspect_ratio=decrease,pad=120:68:(ow-iw)/2:(oh-ih)/2', '-f', 'image2pipe', '-vcodec', 'png', 'pipe:1'], capture_output=True, creationflags=core.CREATE_NO_WINDOW)
                if result.returncode == 0 and result.stdout: self.joinThumbnailReady.emit(index, result.stdout)
            except OSError:
                pass
        threading.Thread(target=worker, daemon=True).start()

    def _set_join_thumbnail(self, index, data):
        if index >= self.join_list.count(): return
        image = QImage(); image.loadFromData(data, 'PNG')
        if not image.isNull(): self.join_list.item(index).setIcon(QIcon(image))
    def _inspect(self, path):
        if not path: return
        try:
            item, self.metadata = inspect_media(path); self.items[path] = item
            fmt = self.metadata.get('format', {}); duration = float(fmt.get('duration', 0)); video = next((s for s in self.metadata.get('streams', []) if s.get('codec_type') == 'video'), None); audio = [s for s in self.metadata.get('streams', []) if s.get('codec_type') == 'audio']
            text = f'{os.path.basename(path)}  •  {core.fmt_time(duration)}';
            if video: text += f'  •  {video.get("width")}×{video.get("height")}  •  {video.get("codec_name", "").upper()}'; self.video.set_source_size(int(video.get('width', 1)), int(video.get('height', 1))); self.crop[2].setValue(int(video.get('width', 2)) // 2 * 2); self.crop[3].setValue(int(video.get('height', 2)) // 2 * 2)
            text += f'  •  {len(audio)} audio track(s)'; self.details.setText(text); self.track.blockSignals(True); self.track.clear(); self.track.addItem('All audio tracks'); [self.track.addItem(t.label) for t in item.audio_tracks]; self.track.setCurrentIndex(0 if self.track_choices.get(path) is None else self.track_choices[path] + 1); self.track.blockSignals(False)
            self.start.setText('00:00:00'); self.end.setText(core.fmt_time(duration)); self.player.setSource(QUrl.fromLocalFile(path)); self._crop_fields()
            self.trim_bar.setRange(0, duration); self.trim_bar.setValues(0, duration)
            self._load_thumbnail_strip(path, duration)
        except Exception as e: self.details.setText(f'Could not inspect file: {e}')
    def _load_thumbnail_strip(self, path, duration):
        if not any(s.get('codec_type') == 'video' for s in self.metadata.get('streams', [])):
            self.trim_bar.setThumbnail(b''); return
        def worker():
            try:
                fps = max(0.5, min(10.0, 10.0 / max(duration, 1.0)))
                cmd = [core.ffmpeg_path(), '-v', 'error', '-i', path, '-vf', f'fps={fps},scale=120:68:force_original_aspect_ratio=decrease,pad=120:68:(ow-iw)/2:(oh-ih)/2,tile=10x1', '-frames:v', '1', '-f', 'image2pipe', '-vcodec', 'png', 'pipe:1']
                result = subprocess.run(cmd, capture_output=True, creationflags=core.CREATE_NO_WINDOW)
                if result.returncode == 0 and result.stdout: self.thumbnailReady.emit(result.stdout)
            except OSError:
                pass
        threading.Thread(target=worker, daemon=True).start()
    def _range_changed(self, start, end):
        self.start.setText(core.fmt_time(start)); self.end.setText(core.fmt_time(end))
    def _sync_range_from_fields(self):
        try:
            self.trim_bar.setValues(core.parse_time(self.start.text()), core.parse_time(self.end.text()))
        except ValueError:
            pass
    def _remember_track(self, index):
        path = self.file_edit.text().strip()
        if path and path in self.items:
            self.track_choices[path] = None if index <= 0 else index - 1
    def _crop_fields(self): self.video.set_rect_source([s.value() for s in self.crop])
    def _crop_from_preview(self, x, y, w, h):
        for spin, value in zip(self.crop, (x, y, w, h)):
            spin.blockSignals(True); spin.setValue(value); spin.blockSignals(False)
    def _crop_preset(self, value):
        if value == 'Free' or not self.metadata: return
        v = next((s for s in self.metadata.get('streams', []) if s.get('codec_type') == 'video'), None)
        if not v: return
        a, b = map(int, value.split(':')); sw, sh = int(v['width']), int(v['height']); w = min(sw, int(sh*a/b)) // 2 * 2; h = min(sh, int(w*b/a)) // 2 * 2; self.crop[0].setValue((sw-w)//4*2); self.crop[1].setValue((sh-h)//4*2); self.crop[2].setValue(w); self.crop[3].setValue(h)
    def _play(self):
        if self.player.playbackState() == QMediaPlayer.PlaybackState.PlayingState: self.player.pause(); self.play.setText('▶ Play')
        else: self.player.play(); self.play.setText('❚❚ Pause')
    def _time(self, p): self.time.setText(f'{core.fmt_time(p/1000)} / {core.fmt_time(self.player.duration()/1000)}')
    def _folder(self):
        path = QFileDialog.getExistingDirectory(self, 'Output folder');
        if path: self.folder.setText(path)
    def _toggle_theme(self): self._theme(self.property('dark') is not True)
    def _theme(self, dark):
        self.setProperty('dark', dark); self.settings.setValue('dark', dark); self.setStyleSheet('QMainWindow,QWidget{background:#0b1220;color:#eef4ff} QFrame#Card,QFrame#PreviewCard{background:#111d2f;border:1px solid #263a56;border-radius:10px} QListWidget#Sidebar{background:#101c2d;border:1px solid #233752;border-radius:10px;padding:10px;color:#b9c8dc} QListWidget#Sidebar::item{padding:14px 12px;border-radius:7px;margin:3px 0} QListWidget#Sidebar::item:selected{background:#2563eb;color:white} QLineEdit,QComboBox,QListWidget{background:#17263b;color:#eef4ff;border:1px solid #314966;padding:8px;border-radius:6px} QPushButton{background:#203650;color:#eef4ff;padding:9px 14px;border-radius:6px} QPushButton:hover{background:#2b4667} QPushButton#Primary{background:#2563eb;color:white;font-weight:600;padding:11px} QProgressBar{background:#17263b;border:0;border-radius:4px;height:7px;text-align:center} QProgressBar::chunk{background:#38bdf8;border-radius:4px} QLabel#Title{font-size:26px;font-weight:700} QLabel#Muted{color:#9badc6} QVideoWidget{background:#050a12;border-radius:7px}' if dark else 'QMainWindow,QWidget{background:#f4f7fb;color:#172033} QFrame#Card,QFrame#PreviewCard{background:#ffffff;border:1px solid #d7e0ec;border-radius:10px} QListWidget#Sidebar{background:#ffffff;border:1px solid #d7e0ec;border-radius:10px;padding:10px;color:#52627a} QListWidget#Sidebar::item{padding:14px 12px;border-radius:7px;margin:3px 0} QListWidget#Sidebar::item:selected{background:#2563eb;color:white} QLineEdit,QComboBox,QListWidget{background:white;color:#172033;border:1px solid #cbd5e1;padding:8px;border-radius:6px} QPushButton{background:#e5eaf2;color:#172033;padding:9px 14px;border-radius:6px} QPushButton:hover{background:#d7e1ef} QPushButton#Primary{background:#2563eb;color:white;font-weight:600;padding:11px} QProgressBar{background:#e5eaf2;border:0;border-radius:4px;height:7px;text-align:center} QProgressBar::chunk{background:#2563eb;border-radius:4px} QLabel#Title{font-size:26px;font-weight:700} QLabel#Muted{color:#52627a} QVideoWidget{background:#dbe4ef;border-radius:7px}')
    def _run(self):
        if not self.paths: QMessageBox.warning(self, 'Input required', 'Choose at least one media file.'); return
        try: commands = self._commands()
        except ValueError as e: QMessageBox.warning(self, 'Check settings', str(e)); return
        self.job = FFmpegJob(commands); self.job.progress.connect(lambda p, s: (self.progress.setValue(p), self.status.setText(s))); self.job.finished.connect(self._finished); self.run_btn.setEnabled(False); self.cancel_btn.setEnabled(True); self.progress.setValue(0); self.job.start()
    def _base_out(self, path, suffix, ext):
        folder = self.folder.text().strip() or os.path.dirname(path); os.makedirs(folder, exist_ok=True); name = self.name.text().strip() or Path(path).stem + suffix; return core.safe_output_path(folder, name, ext)
    def _commands(self):
        tool = self.TOOLS[self.nav.currentRow()]; commands = []
        if tool == 'Join Videos' and len(self.paths) < 2: raise ValueError('Add at least two videos to join.')
        if tool == 'Join Videos':
            signatures = []
            for item in self.paths:
                probe = json.loads(core.run([core.ffprobe_path(), '-v', 'error', '-show_streams', '-of', 'json', item]).stdout)
                signatures.append([(s.get('codec_type'), s.get('codec_name'), s.get('width'), s.get('height'), s.get('r_frame_rate'), s.get('sample_rate'), s.get('channels')) for s in probe.get('streams', []) if s.get('codec_type') in ('video', 'audio')])
            mismatch = any(sig != signatures[0] for sig in signatures[1:])
            if mismatch and self.join_mode.currentIndex() == 0:
                raise ValueError('These videos have different codecs, dimensions, frame rates, or audio tracks. Choose Convert and Join to normalize them.')
        if tool == 'Extract Audio':
            for path in self.paths:
                item = self.items.get(path) or inspect_media(path)[0]
                tracks = item.audio_tracks
                if not tracks: raise ValueError(f'{os.path.basename(path)} has no audio track.')
                choice = self.track_choices.get(path)
                if choice is not None and choice >= len(tracks):
                    raise ValueError(f'{os.path.basename(path)} does not have the selected audio track. Choose All audio tracks or select an available track.')
                selected = list(enumerate(tracks)) if choice is None else [(choice, tracks[choice])]
                for n, (track_index, stream) in enumerate(selected):
                    codec = stream.codec or 'aac'; suffix = '_audio' if len(selected) == 1 else f'_audio_{n + 1}'; out = self._base_out(path, suffix, core.output_extension(codec)); commands.append([core.ffmpeg_path(), '-hide_banner', '-loglevel', 'error', '-i', path, '-map', f'0:a:{track_index}', '-vn', '-c:a', 'copy', '-y', out])
            return commands
        path = self.paths[0]; out = self._base_out(path, {'Trim Audio': '_audio_trim', 'Trim Video': '_clip', 'Crop Frame': '_crop', 'Join Videos': '_joined'}[tool], '.mkv' if self.container.currentText() == 'MKV' else '.mp4')
        if tool == 'Trim Audio':
            selection = validate_selection(self.items.get(path) or inspect_media(path)[0], self.start.text(), self.end.text()); start, end = selection.start, selection.end
            precise = self.audio_mode.currentIndex() == 1
            if precise: out = self._base_out(path, '_audio_trim', '.wav')
            commands.append([core.ffmpeg_path(), '-hide_banner', '-loglevel', 'error', '-ss', str(start), '-i', path, '-t', str(end-start), '-map', '0:a:0', '-vn', '-c:a', 'pcm_s16le' if precise else 'copy', '-y', out])
        elif tool == 'Trim Video':
            selection = validate_selection(self.items.get(path) or inspect_media(path)[0], self.start.text(), self.end.text()); start, end = selection.start, selection.end
            args = ['-ss', str(start), '-i', path, '-t', str(end-start), '-map', '0:v:0', '-map', '0:a?', '-c', 'copy'] if self.video_mode.currentIndex() == 0 else ['-ss', str(start), '-i', path, '-t', str(end-start), '-map', '0:v:0', '-map', '0:a?', '-c:v', 'libx264', '-crf', '18', '-c:a', 'aac']
            commands.append([core.ffmpeg_path(), '-hide_banner', '-loglevel', 'error', *args, '-y', out])
        elif tool == 'Crop Frame':
            x, y, w, h = [s.value() for s in self.crop]; validate_crop(self.items.get(path) or inspect_media(path)[0], (x, y, w, h)); commands.append([core.ffmpeg_path(), '-hide_banner', '-loglevel', 'error', '-i', path, '-vf', f'crop={w}:{h}:{x}:{y}', '-map', '0:v:0', '-map', '0:a?', '-c:v', 'libx264', '-crf', '18', '-c:a', 'copy', '-y', out])
        else:
            temp_root = self.folder.text().strip() or os.path.dirname(path) or os.getcwd()
            os.makedirs(temp_root, exist_ok=True)
            self.job_tempfiles = []
            join_paths = list(self.paths)
            if self.join_mode.currentIndex() == 1:
                first = self.items.get(self.paths[0]) or inspect_media(self.paths[0])[0]
                rate = first.frame_rate or '30'
                for index, source in enumerate(self.paths):
                    item = self.items.get(source) or inspect_media(source)[0]
                    normalized = os.path.join(temp_root, f'.media_toolbox_join_{os.getpid()}_{index:03d}.mp4')
                    self.job_tempfiles.append(normalized)
                    audio_map = '0:a:0' if item.audio_tracks else '1:a:0'
                    commands.append([core.ffmpeg_path(), '-hide_banner', '-loglevel', 'error', '-i', source, '-f', 'lavfi', '-i', 'anullsrc=r=48000:cl=stereo', '-map', '0:v:0', '-map', audio_map, '-vf', f'scale={first.width}:{first.height}:force_original_aspect_ratio=decrease,pad={first.width}:{first.height}:(ow-iw)/2:(oh-ih)/2', '-r', rate, '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-ar', '48000', '-ac', '2', '-shortest', '-y', normalized])
                    join_paths[index] = normalized
            list_path = os.path.join(temp_root, f'.media_toolbox_join_{os.getpid()}.txt'); self.job_tempfiles.append(list_path); Path(list_path).write_text(''.join(f"file '{p.replace(chr(39), chr(39)+chr(92)+chr(39)+chr(39))}'\n" for p in join_paths), encoding='utf-8'); commands.append([core.ffmpeg_path(), '-hide_banner', '-loglevel', 'error', '-f', 'concat', '-safe', '0', '-i', list_path, '-c', 'copy', '-y', out])
        return commands
    def _cancel(self):
        if self.job:
            self.job.cancel()
        self._cleanup_tempfiles()
    def _finished(self, ok, message):
        self.run_btn.setEnabled(True); self.cancel_btn.setEnabled(False); self.job = None; self.progress.setValue(100 if ok else 0); self.status.setText(message); 
        self._cleanup_tempfiles()
        if ok: QMessageBox.information(self, 'Media Toolbox', 'Processing completed successfully.\n\nOpen the output folder to view the result.')
        elif message != 'Cancelled': QMessageBox.critical(self, 'Processing failed', message)
    def _move(self, delta):
        i = self.join_list.currentRow(); j = i + delta
        if 0 <= j < self.join_list.count(): self.paths[i], self.paths[j] = self.paths[j], self.paths[i]; self._set_paths(self.paths); self.join_list.setCurrentRow(j)
    def _cleanup_tempfiles(self):
        for path in self.job_tempfiles:
            try: os.remove(path)
            except OSError: pass
        self.job_tempfiles = []
    def dragEnterEvent(self, event: QDragEnterEvent):
        if event.mimeData().hasUrls(): event.acceptProposedAction()
    def dropEvent(self, event: QDropEvent): self._set_paths([u.toLocalFile() for u in event.mimeData().urls()]); event.acceptProposedAction()


# Compatibility name used by the existing entry point and older packaged builds.
ModernMediaApp = MediaWindow


def main():
    app = QApplication(sys.argv); app.setStyle('Fusion'); window = MediaWindow(); window.show(); sys.exit(app.exec())


if __name__ == '__main__': main()
