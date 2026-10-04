from dataclasses import dataclass, field
from typing import Optional

import Media_Toolbox as core


@dataclass(frozen=True)
class AudioTrack:
    index: int
    codec: str
    language: str = 'und'
    title: str = 'Audio'
    channels: Optional[int] = None

    @property
    def label(self):
        detail = f'{self.codec.upper()} • {self.language} • {self.title}'
        return f'{self.index + 1}: {detail}'


@dataclass(frozen=True)
class MediaItem:
    path: str
    duration: float
    width: int = 0
    height: int = 0
    video_codec: str = ''
    frame_rate: str = ''
    rotation: int = 0
    audio_tracks: tuple[AudioTrack, ...] = field(default_factory=tuple)

    @property
    def name(self):
        return self.path.rsplit('\\', 1)[-1].rsplit('/', 1)[-1]


@dataclass(frozen=True)
class Selection:
    start: float = 0.0
    end: Optional[float] = None
    crop: Optional[tuple[int, int, int, int]] = None


@dataclass(frozen=True)
class JobSpec:
    operation: str
    inputs: tuple[MediaItem, ...]
    selection: Selection = Selection()
    track_choices: tuple[Optional[int], ...] = field(default_factory=tuple)
    precise: bool = False
    output_folder: str = ''
    output_name: str = ''
    container: str = 'Auto'
    convert_join: bool = False


def inspect_media(path):
    import json
    p = core.run([core.ffprobe_path(), '-v', 'error', '-show_streams', '-show_format', '-of', 'json', path])
    if p.returncode:
        raise ValueError(p.stderr.strip() or 'This file could not be read.')
    data = json.loads(p.stdout)
    video = next((s for s in data.get('streams', []) if s.get('codec_type') == 'video'), None)
    audio = tuple(AudioTrack(i, s.get('codec_name', 'unknown'), s.get('tags', {}).get('language', 'und'), s.get('tags', {}).get('title', 'Audio'), s.get('channels')) for i, s in enumerate(s for s in data.get('streams', []) if s.get('codec_type') == 'audio'))
    return MediaItem(path, float(data.get('format', {}).get('duration', 0) or 0), int((video or {}).get('width', 0) or 0), int((video or {}).get('height', 0) or 0), (video or {}).get('codec_name', ''), (video or {}).get('r_frame_rate', ''), int((video or {}).get('tags', {}).get('rotate', 0) or 0), audio), data


def validate_selection(item, start, end):
    start = core.parse_time(str(start)) if not isinstance(start, (int, float)) else float(start)
    end = core.parse_time(str(end)) if not isinstance(end, (int, float)) else float(end)
    if start < 0 or end <= start or end > item.duration + 0.001:
        raise ValueError(f'Choose a start and end between 00:00:00 and {core.fmt_time(item.duration)}.')
    return Selection(start, min(end, item.duration))


def validate_crop(item, rect):
    if not item.width or not item.height:
        raise ValueError('Choose a video with a video stream for cropping.')
    x, y, w, h = [int(v) for v in rect]
    if x < 0 or y < 0 or w <= 0 or h <= 0 or w % 2 or h % 2 or x + w > item.width or y + h > item.height:
        raise ValueError(f'Crop must stay inside {item.width}×{item.height} with positive even width and height.')
    return Selection(crop=(x, y, w, h))
