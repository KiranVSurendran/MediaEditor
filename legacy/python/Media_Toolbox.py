import os
import sys
import base64
import subprocess
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

APP_TITLE = "Media Toolbox — Lossless Audio + Video Tools"
CREATE_NO_WINDOW = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0

CODEC_EXT = {
    "aac": ".m4a", "mp3": ".mp3", "flac": ".flac", "opus": ".opus",
    "vorbis": ".ogg", "alac": ".m4a", "pcm_s16le": ".wav",
    "pcm_s24le": ".wav", "pcm_s32le": ".wav", "pcm_f32le": ".wav",
    "ac3": ".mka", "eac3": ".mka", "dts": ".mka", "truehd": ".mka",
}


def app_dir():
    return os.path.dirname(sys.executable) if getattr(sys, "frozen", False) else os.path.dirname(os.path.abspath(__file__))


def ffmpeg_path():
    bundled = os.path.join(app_dir(), "ffmpeg.exe")
    return bundled if os.path.exists(bundled) else "ffmpeg"


def ffprobe_path():
    bundled = os.path.join(app_dir(), "ffprobe.exe")
    return bundled if os.path.exists(bundled) else "ffprobe"


def run(cmd):
    return subprocess.run(cmd, capture_output=True, text=True, creationflags=CREATE_NO_WINDOW)


def probe(input_file, entries):
    cmd = [ffprobe_path(), "-v", "error", "-show_entries", entries,
           "-of", "default=nw=1:nk=1", input_file]
    p = run(cmd)
    if p.returncode != 0:
        raise RuntimeError(p.stderr.strip() or "Could not inspect the media file.")
    return p.stdout.strip()


def find_codec(input_file):
    value = probe(input_file, "stream=codec_name")
    return value.splitlines()[0] if value else "unknown"


def find_audio_codec(input_file):
    value = probe(input_file, "stream=codec_name")
    # ffprobe without select_streams is not safe here; use dedicated query.
    cmd = [ffprobe_path(), "-v", "error", "-select_streams", "a:0",
           "-show_entries", "stream=codec_name", "-of", "default=nw=1:nk=1", input_file]
    p = run(cmd)
    if p.returncode != 0 or not p.stdout.strip():
        raise RuntimeError(p.stderr.strip() or "No audio stream found in this file.")
    return p.stdout.strip().splitlines()[0]


def find_duration(input_file):
    cmd = [ffprobe_path(), "-v", "error", "-show_entries", "format=duration",
           "-of", "default=nw=1:nk=1", input_file]
    p = run(cmd)
    if p.returncode != 0 or not p.stdout.strip():
        raise RuntimeError("Could not determine media duration.")
    return float(p.stdout.strip())


def find_video_info(input_file):
    cmd = [ffprobe_path(), "-v", "error", "-select_streams", "v:0",
           "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", input_file]
    p = run(cmd)
    if p.returncode != 0 or not p.stdout.strip():
        raise RuntimeError("Could not read video dimensions.")
    return p.stdout.strip().splitlines()[0]


def output_extension(codec):
    return CODEC_EXT.get(codec.lower(), ".mka")


def safe_output_path(folder, base, ext):
    os.makedirs(folder, exist_ok=True)
    path = os.path.join(folder, base + ext)
    if not os.path.exists(path):
        return path
    i = 2
    while True:
        candidate = os.path.join(folder, f"{base} ({i}){ext}")
        if not os.path.exists(candidate):
            return candidate
        i += 1


def parse_time(value):
    value = value.strip()
    if not value:
        raise ValueError("Time cannot be empty.")
    try:
        parts = value.split(":")
        if len(parts) == 1:
            seconds = float(parts[0])
        elif len(parts) == 2:
            seconds = int(parts[0]) * 60 + float(parts[1])
        elif len(parts) == 3:
            seconds = int(parts[0]) * 3600 + int(parts[1]) * 60 + float(parts[2])
        else:
            raise ValueError
    except ValueError:
        raise ValueError("Use seconds, MM:SS, or HH:MM:SS.")
    if seconds < 0:
        raise ValueError("Time cannot be negative.")
    return seconds


def fmt_time(seconds):
    seconds = max(0, float(seconds))
    h = int(seconds // 3600)
    m = int((seconds % 3600) // 60)
    s = seconds % 60
    return f"{h:02d}:{m:02d}:{s:06.3f}"


class MediaApp(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title(APP_TITLE)
        self.geometry("900x720")
        self.minsize(820, 650)
        self.configure(bg="#f4f6f8")
        self.busy = False
        self._build_ui()

    def _build_ui(self):
        style = ttk.Style(self)
        try:
            style.theme_use("vista")
        except tk.TclError:
            pass
        style.configure("Title.TLabel", font=("Segoe UI", 19, "bold"))
        style.configure("Sub.TLabel", font=("Segoe UI", 10))
        style.configure("Big.TButton", font=("Segoe UI", 11, "bold"), padding=9)
        style.configure("Section.TLabelframe.Label", font=("Segoe UI", 11, "bold"))

        root = ttk.Frame(self, padding=20)
        root.pack(fill="both", expand=True)
        ttk.Label(root, text="Media Toolbox", style="Title.TLabel").pack(anchor="w")
        ttk.Label(root, text="Extract, trim, crop and join video/audio with FFmpeg.", style="Sub.TLabel").pack(anchor="w", pady=(2, 12))

        self.status_var = tk.StringVar(value="Ready")
        self.progress = ttk.Progressbar(root, mode="indeterminate")
        self.progress.pack(side="bottom", fill="x", pady=(10, 0))
        ttk.Label(root, textvariable=self.status_var).pack(side="bottom", anchor="w")

        notebook = ttk.Notebook(root)
        notebook.pack(fill="both", expand=True)
        self._build_audio_extract_tab(notebook)
        self._build_audio_trim_tab(notebook)
        self._build_video_trim_tab(notebook)
        self._build_crop_tab(notebook)
        self._build_join_tab(notebook)

    def _file_row(self, parent, variable, command, label="Input video", row=0, types=None):
        ttk.Label(parent, text=label).grid(row=row, column=0, sticky="w", pady=(0, 4))
        box = ttk.Frame(parent)
        box.grid(row=row + 1, column=0, sticky="ew", pady=(0, 14))
        parent.columnconfigure(0, weight=1)
        ttk.Entry(box, textvariable=variable).pack(side="left", fill="x", expand=True)
        ttk.Button(box, text="Browse…", command=command).pack(side="left", padx=(8, 0))

    def _folder_row(self, parent, variable, command, row):
        ttk.Label(parent, text="Output folder").grid(row=row, column=0, sticky="w", pady=(0, 4))
        box = ttk.Frame(parent)
        box.grid(row=row + 1, column=0, sticky="ew", pady=(0, 14))
        parent.columnconfigure(0, weight=1)
        ttk.Entry(box, textvariable=variable).pack(side="left", fill="x", expand=True)
        ttk.Button(box, text="Browse…", command=command).pack(side="left", padx=(8, 0))

    def _new_tab(self, notebook, title):
        tab = ttk.Frame(notebook, padding=20)
        notebook.add(tab, text=title)
        return tab

    def _build_audio_extract_tab(self, notebook):
        tab = self._new_tab(notebook, "🎵 Extract Audio")
        self.ae_input = tk.StringVar(); self.ae_output = tk.StringVar(); self.ae_info = tk.StringVar(value="Audio: —")
        self._file_row(tab, self.ae_input, lambda: self._choose_file(self.ae_input, self.ae_output, self._detect_audio), row=0)
        ttk.Label(tab, textvariable=self.ae_info).grid(row=2, column=0, sticky="w", pady=(0, 12))
        self._folder_row(tab, self.ae_output, lambda: self._choose_folder(self.ae_output), row=3)
        self._info_box(tab, "LOSSLESS\nThe original encoded audio stream is copied with -c:a copy. No MP3 conversion or re-encoding.")
        ttk.Button(tab, text="Extract Original Audio", style="Big.TButton", command=self.start_audio_extract).grid(row=7, column=0, sticky="ew", pady=(20, 0))

    def _build_audio_trim_tab(self, notebook):
        tab = self._new_tab(notebook, "✂ Audio Crop")
        self.at_input = tk.StringVar(); self.at_output = tk.StringVar(); self.at_start = tk.StringVar(value="00:00:00"); self.at_end = tk.StringVar(); self.at_info = tk.StringVar(value="Audio: —")
        self._file_row(tab, self.at_input, lambda: self._choose_file(self.at_input, self.at_output, self._detect_audio_trim), row=0)
        ttk.Label(tab, textvariable=self.at_info).grid(row=2, column=0, sticky="w", pady=(0, 12))
        times = ttk.Frame(tab); times.grid(row=3, column=0, sticky="ew", pady=(0, 14)); times.columnconfigure(1, weight=1); times.columnconfigure(3, weight=1)
        ttk.Label(times, text="Start (HH:MM:SS)").grid(row=0, column=0, sticky="w"); ttk.Entry(times, textvariable=self.at_start, width=18).grid(row=0, column=1, sticky="ew", padx=(8, 20))
        ttk.Label(times, text="End (HH:MM:SS)").grid(row=0, column=2, sticky="w"); ttk.Entry(times, textvariable=self.at_end, width=18).grid(row=0, column=3, sticky="ew", padx=(8, 0))
        self._folder_row(tab, self.at_output, lambda: self._choose_folder(self.at_output), row=4)
        self._info_box(tab, "LOSSLESS CROP\nAudio packets are stream-copied. The cut is not re-encoded, so there is no new quality loss. Exact sample-level cuts may depend on codec packet boundaries.")
        ttk.Button(tab, text="Crop Audio — No Re-encoding", style="Big.TButton", command=self.start_audio_trim).grid(row=7, column=0, sticky="ew", pady=(20, 0))

    def _build_video_trim_tab(self, notebook):
        tab = self._new_tab(notebook, "⏱ Video Portion")
        self.vt_input = tk.StringVar(); self.vt_output = tk.StringVar(); self.vt_start = tk.StringVar(value="00:00:00"); self.vt_end = tk.StringVar(); self.vt_info = tk.StringVar(value="Video: —")
        self._file_row(tab, self.vt_input, lambda: self._choose_file(self.vt_input, self.vt_output, self._detect_video), row=0)
        ttk.Label(tab, textvariable=self.vt_info).grid(row=2, column=0, sticky="w", pady=(0, 12))
        self.vt_preview = tk.Label(tab, text="Video preview will appear here", anchor="center", background="#101827", foreground="#9fb0c8", height=12)
        self.vt_preview.grid(row=3, column=0, sticky="ew", pady=(0, 8))
        self.vt_seek = tk.Scale(tab, from_=0, to=1, orient="horizontal", showvalue=False, command=self._preview_video_frame)
        self.vt_seek.grid(row=4, column=0, sticky="ew", pady=(0, 8))
        times = ttk.Frame(tab); times.grid(row=5, column=0, sticky="ew", pady=(0, 14)); times.columnconfigure(1, weight=1); times.columnconfigure(3, weight=1)
        ttk.Label(times, text="Start (HH:MM:SS)").grid(row=0, column=0, sticky="w"); ttk.Entry(times, textvariable=self.vt_start, width=18).grid(row=0, column=1, sticky="ew", padx=(8, 20))
        ttk.Label(times, text="End (HH:MM:SS)").grid(row=0, column=2, sticky="w"); ttk.Entry(times, textvariable=self.vt_end, width=18).grid(row=0, column=3, sticky="ew", padx=(8, 0))
        self._folder_row(tab, self.vt_output, lambda: self._choose_folder(self.vt_output), row=6)
        self._info_box(tab, "FAST / QUALITY-PRESERVING\nThe selected time range is stream-copied (-c copy). Because video is not re-encoded, cuts can align to nearby keyframes rather than an exact frame.")
        ttk.Button(tab, text="Extract Video Portion — No Re-encoding", style="Big.TButton", command=self.start_video_trim).grid(row=9, column=0, sticky="ew", pady=(20, 0))

    def _build_crop_tab(self, notebook):
        tab = self._new_tab(notebook, "▣ Crop Frame")
        self.cr_input = tk.StringVar(); self.cr_output = tk.StringVar(); self.cr_x = tk.StringVar(value="0"); self.cr_y = tk.StringVar(value="0"); self.cr_w = tk.StringVar(); self.cr_h = tk.StringVar(); self.cr_info = tk.StringVar(value="Video: —")
        self._file_row(tab, self.cr_input, lambda: self._choose_file(self.cr_input, self.cr_output, self._detect_crop), row=0)
        ttk.Label(tab, textvariable=self.cr_info).grid(row=2, column=0, sticky="w", pady=(0, 12))
        grid = ttk.Frame(tab); grid.grid(row=3, column=0, sticky="ew", pady=(0, 14))
        for i in range(4): grid.columnconfigure(i, weight=1)
        for i, (label, var) in enumerate([("X (left)", self.cr_x), ("Y (top)", self.cr_y), ("Width", self.cr_w), ("Height", self.cr_h)]):
            ttk.Label(grid, text=label).grid(row=0, column=i, sticky="w", padx=(0 if i == 0 else 8, 8))
            ttk.Entry(grid, textvariable=var).grid(row=1, column=i, sticky="ew", padx=(0 if i == 0 else 8, 8))
        self._folder_row(tab, self.cr_output, lambda: self._choose_folder(self.cr_output), row=4)
        self._info_box(tab, "FRAME CROP\nThis operation must re-encode the video because the pixels are being changed. Audio is copied when possible. Enter coordinates in source-video pixels.")
        ttk.Button(tab, text="Crop Selected Frame Area", style="Big.TButton", command=self.start_crop).grid(row=7, column=0, sticky="ew", pady=(20, 0))

    def _build_join_tab(self, notebook):
        tab = self._new_tab(notebook, "🔗 Join Videos")
        self.join_files = []
        self.join_list = tk.Listbox(tab, height=11, selectmode=tk.SINGLE)
        self.join_list.grid(row=0, column=0, sticky="nsew", pady=(0, 10))
        tab.rowconfigure(0, weight=1)
        tab.columnconfigure(0, weight=1)
        controls = ttk.Frame(tab); controls.grid(row=1, column=0, sticky="ew", pady=10)
        ttk.Button(controls, text="Add Videos", command=self.join_add).pack(side="left")
        ttk.Button(controls, text="Remove", command=self.join_remove).pack(side="left", padx=6)
        ttk.Button(controls, text="Move Up", command=lambda: self.join_move(-1)).pack(side="left", padx=6)
        ttk.Button(controls, text="Move Down", command=lambda: self.join_move(1)).pack(side="left")
        self.join_output = tk.StringVar()
        self._folder_row(tab, self.join_output, lambda: self._choose_folder(self.join_output), row=2)
        self._info_box(tab, "JOIN\nVideos are joined with stream copy when their streams are compatible. If they differ, FFmpeg may need re-encoding; the app reports any failure instead of silently pretending it was lossless.")
        ttk.Button(tab, text="Join Videos", style="Big.TButton", command=self.start_join).grid(row=6, column=0, sticky="ew", pady=(20, 0))

    def _info_box(self, parent, text):
        frame = tk.Frame(parent, bg="#e9eef5")
        # Parent tabs use grid, so this widget must also be managed by grid.
        next_row = parent.grid_size()[1]
        frame.grid(row=next_row, column=0, sticky="ew", pady=(0, 8))
        parent.columnconfigure(0, weight=1)
        tk.Label(frame, text=text, justify="left", anchor="w", bg="#e9eef5", fg="#263238", font=("Segoe UI", 10), padx=12, pady=10).pack(fill="x")

    def _choose_file(self, var, output_var, detector=None):
        path = filedialog.askopenfilename(title="Select media file", filetypes=[("Video/audio files", "*.mp4;*.m4v;*.mov;*.mkv;*.webm;*.mp3;*.m4a;*.wav;*.flac;*.opus"), ("All files", "*.*")])
        if not path: return
        var.set(path)
        if not output_var.get(): output_var.set(os.path.dirname(path))
        if detector:
            try: detector(path)
            except Exception as e: messagebox.showwarning("Media detection", str(e))

    def _choose_folder(self, var):
        path = filedialog.askdirectory(title="Select output folder")
        if path: var.set(path)

    def _detect_audio(self, path):
        codec = find_audio_codec(path); self.ae_info.set(f"Audio codec: {codec.upper()} → output: {output_extension(codec)}")

    def _detect_audio_trim(self, path):
        codec = find_audio_codec(path); duration = find_duration(path); self.at_end.set(fmt_time(duration)); self.at_info.set(f"Audio: {codec.upper()} • Duration: {fmt_time(duration)}")

    def _detect_video(self, path):
        duration = find_duration(path); size = find_video_info(path); self.vt_end.set(fmt_time(duration)); self.vt_info.set(f"Video: {size} • Duration: {fmt_time(duration)} • Drag the preview slider to inspect frames"); self.vt_seek.configure(to=max(duration, 0.01)); self.vt_seek.set(0); self._preview_video_frame(0)

    def _preview_video_frame(self, value):
        path = self.vt_input.get().strip().strip('"')
        if not os.path.isfile(path):
            return
        try:
            seconds = max(0, float(value))
            cmd = [ffmpeg_path(), "-hide_banner", "-loglevel", "error", "-ss", str(seconds), "-i", path, "-frames:v", "1", "-vf", "scale=720:405:force_original_aspect_ratio=decrease", "-f", "image2pipe", "-vcodec", "png", "pipe:1"]
            p = subprocess.run(cmd, capture_output=True, creationflags=CREATE_NO_WINDOW)
            if p.returncode == 0 and p.stdout:
                self.vt_preview_image = tk.PhotoImage(data=base64.b64encode(p.stdout).decode("ascii"))
                self.vt_preview.configure(image=self.vt_preview_image, text="")
        except Exception:
            self.vt_preview.configure(image="", text="Preview unavailable")

    def _detect_crop(self, path):
        size = find_video_info(path); w, h = map(int, size.lower().split("x")); self.cr_w.set(str(w)); self.cr_h.set(str(h)); self.cr_info.set(f"Video size: {w} × {h} pixels")

    def _validate_range(self, start_text, end_text, input_file):
        start = parse_time(start_text); end = parse_time(end_text); duration = find_duration(input_file)
        if end <= start: raise ValueError("End time must be greater than start time.")
        if start >= duration: raise ValueError("Start time is outside the media duration.")
        end = min(end, duration)
        return start, end

    def _set_busy(self, busy, message):
        self.busy = busy
        if busy: self.progress.start(10)
        else: self.progress.stop()
        self.status_var.set(message)

    def _run_background(self, func, success_title):
        if self.busy: return
        self._set_busy(True, "Working…")
        def worker():
            try:
                output = func()
                self.after(0, lambda: self._complete(success_title, output))
            except Exception as e:
                self.after(0, lambda error=str(e): self._failed(error))
        threading.Thread(target=worker, daemon=True).start()

    def _complete(self, title, output):
        self._set_busy(False, f"Done: {output}")
        messagebox.showinfo(title, f"Completed successfully.\n\n{output}")

    def _failed(self, error):
        self._set_busy(False, "Operation failed")
        messagebox.showerror("Operation failed", error)

    def _output_folder(self, input_file, folder):
        folder = folder.strip().strip('"') or os.path.dirname(input_file)
        os.makedirs(folder, exist_ok=True)
        return folder

    def start_audio_extract(self):
        inp = self.ae_input.get().strip().strip('"'); outdir = self._output_folder(inp, self.ae_output.get()) if os.path.isfile(inp) else ""
        if not os.path.isfile(inp): return messagebox.showerror("Input", "Please select a valid media file.")
        self._run_background(lambda: self._audio_extract(inp, outdir), "Audio extraction complete")

    def _audio_extract(self, inp, outdir):
        codec = find_audio_codec(inp); ext = output_extension(codec); base = os.path.splitext(os.path.basename(inp))[0]
        out = safe_output_path(outdir, base, ext)
        cmd = [ffmpeg_path(), "-hide_banner", "-loglevel", "error", "-i", inp, "-map", "0:a:0", "-vn", "-c:a", "copy", "-y", out]
        p = run(cmd)
        if p.returncode != 0: raise RuntimeError(p.stderr.strip() or "FFmpeg failed to extract audio.")
        return out

    def start_audio_trim(self):
        inp = self.at_input.get().strip().strip('"')
        if not os.path.isfile(inp): return messagebox.showerror("Input", "Please select a valid media file.")
        try: start, end = self._validate_range(self.at_start.get(), self.at_end.get(), inp)
        except Exception as e: return messagebox.showerror("Time range", str(e))
        outdir = self._output_folder(inp, self.at_output.get()); self._run_background(lambda: self._audio_trim(inp, outdir, start, end), "Audio crop complete")

    def _audio_trim(self, inp, outdir, start, end):
        codec = find_audio_codec(inp); ext = output_extension(codec); base = os.path.splitext(os.path.basename(inp))[0] + f"_audio_{start:.3f}-{end:.3f}"; out = safe_output_path(outdir, base, ext)
        cmd = [ffmpeg_path(), "-hide_banner", "-loglevel", "error", "-ss", fmt_time(start), "-i", inp, "-t", fmt_time(end-start), "-map", "0:a:0", "-vn", "-c:a", "copy", "-avoid_negative_ts", "make_zero", "-y", out]
        p = run(cmd)
        if p.returncode != 0: raise RuntimeError(p.stderr.strip() or "FFmpeg failed to crop audio.")
        return out

    def start_video_trim(self):
        inp = self.vt_input.get().strip().strip('"')
        if not os.path.isfile(inp): return messagebox.showerror("Input", "Please select a valid video.")
        try: start, end = self._validate_range(self.vt_start.get(), self.vt_end.get(), inp)
        except Exception as e: return messagebox.showerror("Time range", str(e))
        outdir = self._output_folder(inp, self.vt_output.get()); self._run_background(lambda: self._video_trim(inp, outdir, start, end), "Video portion complete")

    def _video_trim(self, inp, outdir, start, end):
        base = os.path.splitext(os.path.basename(inp))[0] + f"_clip_{start:.3f}-{end:.3f}"; out = safe_output_path(outdir, base, ".mp4")
        cmd = [ffmpeg_path(), "-hide_banner", "-loglevel", "error", "-ss", fmt_time(start), "-i", inp, "-t", fmt_time(end-start), "-map", "0:v:0", "-map", "0:a?", "-c", "copy", "-avoid_negative_ts", "make_zero", "-y", out]
        p = run(cmd)
        if p.returncode != 0: raise RuntimeError(p.stderr.strip() or "FFmpeg failed to extract the video portion.")
        return out

    def start_crop(self):
        inp = self.cr_input.get().strip().strip('"')
        if not os.path.isfile(inp): return messagebox.showerror("Input", "Please select a valid video.")
        try:
            x, y, w, h = map(int, [self.cr_x.get(), self.cr_y.get(), self.cr_w.get(), self.cr_h.get()])
            if min(x, y, w, h) < 0 or w <= 0 or h <= 0: raise ValueError
            size = find_video_info(inp); sw, sh = map(int, size.split("x"))
            if x + w > sw or y + h > sh: raise ValueError(f"Crop rectangle exceeds source frame {sw}×{sh}.")
            if w % 2 or h % 2: raise ValueError("Width and height should be even for common video codecs. Try even numbers.")
        except ValueError as e:
            return messagebox.showerror("Crop rectangle", str(e) if str(e) else "Enter valid integer crop values.")
        outdir = self._output_folder(inp, self.cr_output.get()); self._run_background(lambda: self._crop(inp, outdir, x, y, w, h), "Video crop complete")

    def _crop(self, inp, outdir, x, y, w, h):
        base = os.path.splitext(os.path.basename(inp))[0] + f"_crop_{x}_{y}_{w}x{h}"; out = safe_output_path(outdir, base, ".mp4")
        # Cropping changes pixels, so video must be encoded. Audio remains stream-copied.
        cmd = [ffmpeg_path(), "-hide_banner", "-loglevel", "error", "-i", inp, "-vf", f"crop={w}:{h}:{x}:{y}", "-map", "0:v:0", "-map", "0:a?", "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-c:a", "copy", "-movflags", "+faststart", "-y", out]
        p = run(cmd)
        if p.returncode != 0: raise RuntimeError(p.stderr.strip() or "FFmpeg failed to crop the video.")
        return out

    def join_add(self):
        paths = filedialog.askopenfilenames(title="Select videos", filetypes=[("Video files", "*.mp4;*.mkv;*.mov;*.m4v;*.webm"), ("All files", "*.*")])
        for p in paths:
            if p not in self.join_files:
                self.join_files.append(p); self.join_list.insert("end", p)
        if paths and not self.join_output.get(): self.join_output.set(os.path.dirname(paths[0]))

    def join_remove(self):
        sel = self.join_list.curselection()
        if not sel: return
        i = sel[0]; self.join_list.delete(i); self.join_files.pop(i)

    def join_move(self, delta):
        sel = self.join_list.curselection()
        if not sel: return
        i = sel[0]; j = i + delta
        if not (0 <= j < len(self.join_files)): return
        self.join_files[i], self.join_files[j] = self.join_files[j], self.join_files[i]
        self.join_list.delete(0, "end")
        for p in self.join_files: self.join_list.insert("end", p)
        self.join_list.selection_set(j)

    def start_join(self):
        if len(self.join_files) < 2: return messagebox.showerror("Join", "Add at least two videos.")
        outdir = self.join_output.get().strip().strip('"') or os.path.dirname(self.join_files[0]); os.makedirs(outdir, exist_ok=True)
        files = list(self.join_files)
        self._run_background(lambda: self._join(files, outdir), "Video join complete")

    def _join(self, files, outdir):
        # concat demuxer + stream copy: lossless only when streams/codecs/timebases are compatible.
        listfile = os.path.join(outdir, "_media_join_list.txt")
        try:
            with open(listfile, "w", encoding="utf-8") as f:
                for path in files:
                    safe = path.replace("'", "'\\''")
                    f.write(f"file '{safe}'\n")
            base = os.path.splitext(os.path.basename(files[0]))[0] + "_joined"; out = safe_output_path(outdir, base, ".mp4")
            cmd = [ffmpeg_path(), "-hide_banner", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", listfile, "-c", "copy", "-y", out]
            p = run(cmd)
            if p.returncode != 0:
                raise RuntimeError("Lossless join failed. The videos likely have incompatible streams/codecs.\n\n" + (p.stderr.strip() or "FFmpeg reported an incompatibility."))
            return out
        finally:
            try: os.remove(listfile)
            except OSError: pass


if __name__ == "__main__":
    try:
        from PySide6.QtWidgets import QApplication
        from modern_ui import ModernMediaApp
        qt_app = QApplication(sys.argv)
        app = ModernMediaApp()
        app.show()
        qt_app.exec()
    except (ImportError, OSError):
        # Some Windows systems cannot load the Qt multimedia DLLs. Keep the
        # portable app usable by falling back to the original Tk interface.
        MediaApp().mainloop()
