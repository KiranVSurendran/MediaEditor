# Third-party notices

## FFmpeg and FFprobe

Media Toolbox invokes FFmpeg and FFprobe as separate executables. They are not
included in this source repository because Windows builds are large and their
license depends on the configuration used to build them.

The `build_windows.bat` script copies `ffmpeg.exe` and `ffprobe.exe` from the
repository root into the published app folder. Supply binaries from a source
you trust and keep that distribution's license, notices, and any required
source offer with packages you redistribute. Review the licenses of FFmpeg and
the codecs enabled in the selected build. The binaries used during development
were built with GPL and version 3 options enabled, so those particular
binaries are GPL-3.0-or-later.

The Media Toolbox application source is MIT-licensed. That license does not
change the terms that apply to separately supplied FFmpeg/FFprobe binaries or
other third-party components.
