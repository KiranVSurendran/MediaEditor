# Media Toolbox

Media Toolbox is a Windows desktop app for everyday audio and video edits. It
uses a native WPF interface and FFmpeg for media processing. The application
source is open source under the MIT License; see [Third-party notices](THIRD_PARTY_NOTICES.md)
for FFmpeg and codec licensing details.

## Features

- **Extract Audio** — process multiple videos, select a track per file or all
  tracks, keep the original encoding, or convert to MP3, WAV, FLAC, AAC, or
  Opus. A failed file does not stop the rest of the batch.
- **Trim Audio** — preview and seek, select a range on the waveform or enter
  timestamps, then use fast stream copy or precise PCM WAV output.
- **Trim Video** — preview and seek with a filmstrip range selector. Fast mode
  preserves the original streams and may cut at a nearby keyframe; precise
  mode re-encodes to H.264/AAC at the requested boundaries.
- **Crop Frame** — drag a crop rectangle or enter even source-pixel
  coordinates. Free, square, portrait, widescreen, and classic presets are
  available, including rotation-aware cropping.
- **Join Videos** — reorder clips and check stream compatibility. Matching
  clips can be joined without re-encoding; mismatched clips can be converted
  to the first clip's size and frame rate, with H.264, stereo AAC, and silence
  added where a clip has no audio.

The dark theme is the default; a light theme is also available. Preferences are
stored per Windows user. Output names are made unique to protect existing
files. Cancellation removes incomplete output and keeps completed batch
results. Rotate, Resize, Filters, and Titles are visible as Coming Soon.

## Run the app

The published app is framework-dependent and requires the **.NET 8 Desktop
Runtime** on the target Windows computer. It does not need Python or `uv`.

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
   to build from source, or the .NET 8 Desktop Runtime to run a published copy.
2. Place compatible `ffmpeg.exe` and `ffprobe.exe` binaries in the repository
   root. They are intentionally excluded from Git; read
   [Third-party notices](THIRD_PARTY_NOTICES.md) before redistributing them.
3. Run `build_windows.bat`.
4. Launch `MediaToolbox.Wpf.exe` from the newest
   `dist\Media_Toolbox_WPF_*` folder.

Each build uses a new timestamped output folder, so it will not replace an app
that is already running. The build folder includes the app, FFmpeg tools, and
license/notice files.

To run the WPF project directly during development, use:

```powershell
dotnet run --project MediaToolbox.Wpf\MediaToolbox.Wpf.csproj
```

## Run the smoke tests

With the .NET 8 SDK and FFmpeg/FFprobe available in the repository root:

```powershell
dotnet run --project MediaToolbox.Wpf.SmokeTests\MediaToolbox.Smoke.csproj -c Release
```

The smoke runner generates temporary media and checks the five screens and
themes, multi-track extraction, trim accuracy, crop geometry and rotation,
compatible and converted joins, Unicode paths, progress, output naming, and
cancellation cleanup.

## Repository layout

- `MediaToolbox.Wpf/` — current application source.
- `MediaToolbox.Wpf.SmokeTests/` — dependency-free WPF and media-processing
  smoke tests.
- `build_windows.bat` — timestamped Windows publisher.
- `legacy/python/` — archived Python/Qt version retained for reference; it is
  not the supported build or fallback launcher.

## Contributing

Bug reports and focused improvements are welcome. Include the Windows version,
steps to reproduce, the tool involved, and any relevant FFmpeg error text.
Please avoid uploading private media; short synthetic samples are better for
reproducing media-processing issues.

Before submitting a change, build the WPF project and run the smoke tests
above. Keep processing logic in `MediaProcessor` and immutable job settings in
`MediaModels`; UI code should not assemble FFmpeg arguments directly.

## License

The original Media Toolbox application code is available under the MIT License.
Third-party executables and components retain their own licenses; see
[`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).
