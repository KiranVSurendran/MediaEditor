using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MediaToolbox.Wpf;

/// <summary>FFprobe/FFmpeg boundary. It accepts immutable job settings and does not depend on WPF controls.</summary>
public sealed class MediaProcessor
{
    private readonly string ffmpeg;
    private readonly string ffprobe;

    public MediaProcessor(string? baseDirectory = null)
    {
        var root = baseDirectory ?? AppContext.BaseDirectory;
        ffmpeg = Locate(root, "ffmpeg.exe", "ffmpeg");
        ffprobe = Locate(root, "ffprobe.exe", "ffprobe");
    }

    public static string Validate(MediaJobSpec spec)
    {
        if (spec.Inputs.IsDefaultOrEmpty) return "Add at least one media file first.";
        if (spec.Inputs.Any(x => !File.Exists(x.Path))) return "One or more selected files can no longer be found. Remove them and add the files again.";
        if (spec.Operation == MediaOperation.JoinVideos && spec.Inputs.Length < 2) return "Add at least two videos to join.";
        if (spec.Operation is MediaOperation.TrimVideo or MediaOperation.CropVideo && spec.Inputs.Any(x => !x.HasVideo)) return "Every selected file needs a video stream for this operation.";
        if (spec.Operation == MediaOperation.TrimAudio && spec.Inputs.Any(x => x.AudioTracks.IsDefaultOrEmpty)) return "The selected file has no audio track to trim.";
        if (spec.Operation is MediaOperation.TrimAudio or MediaOperation.TrimVideo)
        {
            if (spec.Selection is not { } range || range.Start < 0 || range.End <= range.Start || spec.Inputs.Any(x => range.End > x.Duration + 0.02))
                return "Check the selection. The end must be after the start and within the media duration.";
        }
        if (spec.Operation == MediaOperation.CropVideo)
        {
            if (spec.Inputs.Length != 1 || spec.Crop is not { } crop) return "Choose one video and a crop area.";
            var item = spec.Inputs[0];
            if (crop.X < 0 || crop.Y < 0 || crop.Width < 2 || crop.Height < 2 || (crop.X | crop.Y | crop.Width | crop.Height) % 2 != 0 || crop.X + crop.Width > item.OrientedWidth || crop.Y + crop.Height > item.OrientedHeight)
                return "The crop must be inside the video with positive, even pixel coordinates and dimensions.";
        }
        var joinUsesFastCopy = spec.Operation == MediaOperation.JoinVideos && (spec.Join == JoinMode.Fast || spec.Join == JoinMode.Automatic && CheckJoinCompatibility(spec.Inputs).Compatible);
        var usesFastVideoCopy = spec.Operation == MediaOperation.TrimVideo && spec.Quality == QualityMode.Fast || joinUsesFastCopy;
        if (usesFastVideoCopy && spec.Container == VideoContainer.Mp4)
        {
            var mp4Video = new[] { "h264", "hevc", "av1", "mpeg4", "mpeg2video", "mjpeg" };
            var mp4Audio = new[] { "aac", "mp3", "alac", "ac3", "eac3" };
            if (spec.Inputs.Any(x => !mp4Video.Contains(x.VideoCodec, StringComparer.OrdinalIgnoreCase) || x.AudioTracks.Any(a => !mp4Audio.Contains(a.Codec, StringComparer.OrdinalIgnoreCase))))
                return "These original streams may not fit MP4 in Fast mode. Choose Auto or MKV, or use a precise/conversion mode.";
        }
        if (spec.OutputFolder.Trim().Length == 0) return "Choose a folder for the output.";
        return "";
    }

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The selected file could not be found.", path);
        var args = new[] { "-v", "error", "-show_streams", "-show_format", "-of", "json", path };
        var json = await RunCaptureAsync(ffprobe, args, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var streams = root.TryGetProperty("streams", out var streamArray) ? streamArray.EnumerateArray().ToArray() : [];
        var video = streams.FirstOrDefault(x => GetString(x, "codec_type") == "video" && GetInt(x, "disposition", "attached_pic") != 1);
        var hasVideo = video.ValueKind == JsonValueKind.Object;
        var width = hasVideo ? GetInt(video, "width") : 0;
        var height = hasVideo ? GetInt(video, "height") : 0;
        var rotation = hasVideo ? ReadRotation(video) : 0;
        var audio = ImmutableArray.CreateBuilder<AudioTrackInfo>();
        var ordinal = 0;
        foreach (var stream in streams.Where(x => GetString(x, "codec_type") == "audio"))
        {
            var tags = stream.TryGetProperty("tags", out var t) ? t : default;
            var disposition = stream.TryGetProperty("disposition", out var d) ? d : default;
            var title = GetString(tags, "title");
            if (string.IsNullOrWhiteSpace(title)) title = GetString(tags, "name");
            var language = GetString(tags, "language");
            if (string.IsNullOrEmpty(title) && disposition.ValueKind == JsonValueKind.Object && GetInt(disposition, "comment") == 1) title = "Commentary";
            audio.Add(new AudioTrackInfo(ordinal, GetInt(stream, "index"), GetString(stream, "codec_name"), language, title, GetInt(stream, "channels"), ParseInt(GetString(stream, "sample_rate"))));
            ordinal++;
        }
        var format = root.TryGetProperty("format", out var f) ? f : default;
        var duration = ParseDouble(GetString(format, "duration"));
        if (duration <= 0) duration = streams.Select(x => ParseDouble(GetString(x, "duration"))).DefaultIfEmpty(0).Max();
        var averageRate = hasVideo ? GetString(video, "avg_frame_rate") : "";
        var nominalRate = hasVideo ? GetString(video, "r_frame_rate") : "";
        var parsedAverage = ParseRateOrZero(averageRate);
        var parsedNominal = ParseRateOrZero(nominalRate);
        var variableFrameRate = parsedAverage > 0 && parsedNominal > 0 && Math.Abs(parsedAverage - parsedNominal) > .02;
        var frameRate = parsedAverage > 0 ? NormalizeRate(averageRate) : NormalizeRate(nominalRate);
        return new MediaInfo(Path.GetFullPath(path), duration, width, height, hasVideo ? GetString(video, "codec_name") : "", frameRate, rotation, audio.ToImmutable(), hasVideo, variableFrameRate);
    }

    public async Task<ImmutableArray<byte[]>> GenerateVideoThumbnailsAsync(MediaInfo media, int frameCount = 12, CancellationToken cancellationToken = default)
    {
        if (!media.HasVideo || media.Duration <= 0) return [];
        var temp = CreatePreviewDirectory();
        try
        {
            var frames = ImmutableArray.CreateBuilder<byte[]>();
            var count = Math.Clamp(frameCount, 1, 48);
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var at = media.Duration * (i + .4) / (count + .7);
                var output = Path.Combine(temp, $"frame-{i:00}.jpg");
                var ok = await RunPreviewToolAsync(["-hide_banner", "-loglevel", "error", "-ss", at.ToString("0.###", CultureInfo.InvariantCulture), "-i", media.Path, "-frames:v", "1", "-vf", "scale=240:136:force_original_aspect_ratio=increase,crop=240:136", "-q:v", "7", "-y", output], cancellationToken).ConfigureAwait(false);
                if (ok && File.Exists(output)) frames.Add(await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false));
            }
            return frames.ToImmutable();
        }
        finally { TryDeleteDirectory(temp); }
    }

    public async Task<byte[]?> GenerateThumbnailAsync(MediaInfo media, CancellationToken cancellationToken = default)
    {
        if (!media.HasVideo || media.Duration <= 0) return null;
        var temp = CreatePreviewDirectory(); var output = Path.Combine(temp, "cover.jpg");
        try
        {
            var at = Math.Min(.3, media.Duration / 3).ToString("0.###", CultureInfo.InvariantCulture);
            var ok = await RunPreviewToolAsync(["-hide_banner", "-loglevel", "error", "-ss", at, "-i", media.Path, "-frames:v", "1", "-vf", "scale=256:144:force_original_aspect_ratio=decrease", "-q:v", "8", "-y", output], cancellationToken).ConfigureAwait(false);
            return ok && File.Exists(output) ? await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false) : null;
        }
        finally { TryDeleteDirectory(temp); }
    }

    public async Task<byte[]?> GenerateAudioWaveformAsync(string path, CancellationToken cancellationToken = default)
    {
        var temp = CreatePreviewDirectory(); var output = Path.Combine(temp, "wave.png");
        try
        {
            var ok = await RunPreviewToolAsync(["-hide_banner", "-loglevel", "error", "-i", path, "-filter_complex", "aformat=channel_layouts=mono,showwavespic=s=1200x90:colors=0x28C8B9", "-frames:v", "1", "-y", output], cancellationToken).ConfigureAwait(false);
            return ok && File.Exists(output) ? await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false) : null;
        }
        finally { TryDeleteDirectory(temp); }
    }

    public async Task<MediaJobResult> ProcessAsync(MediaJobSpec spec, Action<MediaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var validation = Validate(spec);
        if (validation.Length > 0) throw new InvalidOperationException(validation);
        Directory.CreateDirectory(spec.OutputFolder);
        var outputs = ImmutableArray.CreateBuilder<MediaJobOutput>();
        var tempDirectory = Path.Combine(Path.GetTempPath(), "MediaToolbox", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            if (spec.Operation == MediaOperation.ExtractAudio)
            {
                var tasks = spec.Inputs.SelectMany((input, index) =>
                {
                    var selection = spec.TrackSelections?.GetValueOrDefault(input.Path);
                    var tracks = selection is null ? input.AudioTracks : input.AudioTracks.Where(t => t.Ordinal == selection.Value).ToImmutableArray();
                    return tracks.Select(track => (Input: input, FileIndex: index + 1, Track: track));
                }).ToArray();
                var totalWork = Math.Max(.001, tasks.Sum(x => x.Input.Duration));
                var completedWork = 0d;
                try
                {
                    for (var i = 0; i < spec.Inputs.Length; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var input = spec.Inputs[i];
                        var selected = spec.TrackSelections?.GetValueOrDefault(input.Path);
                        var tracks = selected is null ? input.AudioTracks : input.AudioTracks.Where(t => t.Ordinal == selected.Value).ToImmutableArray();
                        if (tracks.IsDefaultOrEmpty) outputs.Add(new(input.Path, null, false, "The chosen audio track is not present in this file."));
                    }
                    for (var index = 0; index < tasks.Length; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var task = tasks[index]; var input = task.Input; var track = task.Track;
                        var ext = AudioExtension(spec.AudioFormat, track.Codec);
                        var multiple = (spec.TrackSelections?.GetValueOrDefault(input.Path) is null ? input.AudioTracks.Length : 1) > 1;
                        var suffix = multiple ? $"_track{track.Ordinal + 1}" : "";
                        var perFileStem = Path.GetFileNameWithoutExtension(input.Path) + "_audio";
                        var stem = spec.Inputs.Length > 1
                            ? string.IsNullOrWhiteSpace(spec.OutputName) ? perFileStem : SafeStem(spec.OutputName, input.Path) + "_" + Path.GetFileNameWithoutExtension(input.Path)
                            : string.IsNullOrWhiteSpace(spec.OutputName) ? perFileStem : SafeStem(spec.OutputName, input.Path);
                        var audioOutput = UniquePath(spec.OutputFolder, stem + suffix, ext);
                        try
                        {
                            var args = BuildExtractArgs(input, track, spec.AudioFormat, audioOutput);
                            await RunFfmpegAsync(args, trackDuration: input.Duration, elapsedBefore: TimeSpan.FromSeconds(completedWork), totalDuration: totalWork, itemIndex: task.FileIndex, itemCount: spec.Inputs.Length, activity: "Extracting audio", progress, cancellationToken).ConfigureAwait(false);
                            outputs.Add(new(input.Path, audioOutput, true, $"Track {track.Ordinal + 1} extracted."));
                        }
                        catch (OperationCanceledException) { TryDelete(audioOutput); throw; }
                        catch (Exception ex) { TryDelete(audioOutput); outputs.Add(new(input.Path, null, false, Explain(ex.Message))); }
                        completedWork += input.Duration;
                    }
                }
                catch (OperationCanceledException) { throw new MediaJobCanceledException(outputs.ToImmutable()); }
                return new(outputs.ToImmutable());
            }

            if (spec.Operation == MediaOperation.JoinVideos)
                return await JoinAsync(spec, tempDirectory, progress, cancellationToken).ConfigureAwait(false);

            var media = spec.Inputs[0];
            var transcodingVideo = spec.Quality == QualityMode.Precise || spec.Operation == MediaOperation.CropVideo;
            var outputExtension = spec.Operation == MediaOperation.TrimAudio
                ? spec.Quality == QualityMode.Precise ? ".wav" : AudioExtension(spec.AudioFormat, media.AudioTracks[0].Codec)
                : VideoExtension(spec.Container, transcodingVideo ? "h264" : media.VideoCodec,
                    transcodingVideo ? ["aac"] : media.AudioTracks.Select(x => x.Codec).ToArray());
            var output = UniquePath(spec.OutputFolder, SafeStem(spec.OutputName, media.Path), outputExtension);
            try
            {
                var args = BuildSingleArgs(spec, media, output);
                await RunFfmpegAsync(args, spec.Operation is MediaOperation.TrimAudio or MediaOperation.TrimVideo ? spec.Selection!.Value.Duration : media.Duration,
                    TimeSpan.Zero, media.Duration, 1, 1, spec.Operation switch { MediaOperation.TrimAudio => "Trimming audio", MediaOperation.TrimVideo => "Trimming video", _ => "Cropping video" }, progress, cancellationToken).ConfigureAwait(false);
                outputs.Add(new(media.Path, output, true, "Completed."));
            }
            catch (OperationCanceledException) { TryDelete(output); throw; }
            catch { TryDelete(output); throw; }
            return new(outputs.ToImmutable());
        }
        finally { TryDeleteDirectory(tempDirectory); }
    }

    private async Task<MediaJobResult> JoinAsync(MediaJobSpec spec, string temp, Action<MediaProgress>? progress, CancellationToken token)
    {
        var compatibility = CheckJoinCompatibility(spec.Inputs);
        var convert = spec.Join == JoinMode.Convert || (spec.Join == JoinMode.Automatic && !compatibility.Compatible);
        if (spec.Join == JoinMode.Fast && !compatibility.Compatible) throw new InvalidOperationException("Fast Join needs matching video/audio codecs, dimensions, frame rates and audio layouts. Choose Convert & Join to make the clips compatible.");
        var first = spec.Inputs[0];
        var container = spec.Container;
        var extension = VideoExtension(container, convert ? "h264" : first.VideoCodec, convert ? ["aac"] : first.AudioTracks.Select(x => x.Codec).ToArray());
        var output = UniquePath(spec.OutputFolder, SafeStem(spec.OutputName, first.Path), extension);
        var joinedDuration = spec.Inputs.Sum(x => x.Duration);
        var totalWorkDuration = convert ? joinedDuration * 2 : joinedDuration;
        try
        {
            var concatInputs = new List<string>();
            if (convert)
            {
                var frameRate = ParseRate(first.FrameRate);
                var width = Math.Max(2, first.OrientedWidth & ~1); var height = Math.Max(2, first.OrientedHeight & ~1);
                for (var i = 0; i < spec.Inputs.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var item = spec.Inputs[i]; var normalized = Path.Combine(temp, $"clip-{i:000}.mkv");
                    var args = new List<string> { "-i", item.Path };
                    if (item.AudioTracks.IsDefaultOrEmpty) args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
                    args.AddRange(["-map", "0:v:0"]);
                    args.AddRange(item.AudioTracks.IsDefaultOrEmpty ? ["-map", "1:a:0"] : ["-map", "0:a:0"]);
                    args.AddRange(["-vf", $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,fps={frameRate.ToString("0.####", CultureInfo.InvariantCulture)},setsar=1",
                        "-t", item.Duration.ToString("0.######", CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-c:a", "aac", "-ar", "48000", "-ac", "2", "-y", normalized]);
                    await RunFfmpegAsync(args, item.Duration, TimeSpan.FromSeconds(spec.Inputs.Take(i).Sum(x => x.Duration)), totalWorkDuration, i + 1, spec.Inputs.Length, "Converting clips", progress, token).ConfigureAwait(false);
                    concatInputs.Add(normalized);
                }
            }
            else concatInputs.AddRange(spec.Inputs.Select(x => x.Path));

            var concatList = Path.Combine(temp, "concat-list.txt");
            await File.WriteAllTextAsync(concatList, string.Join("\n", concatInputs.Select(x => $"file '{EscapeConcatPath(x)}'")), new UTF8Encoding(false), token).ConfigureAwait(false);
            var joinArgs = new List<string> { "-f", "concat", "-safe", "0", "-i", concatList, "-map", "0:v?", "-map", "0:a?", "-c", "copy", "-y", output };
            await RunFfmpegAsync(joinArgs, joinedDuration, TimeSpan.FromSeconds(convert ? joinedDuration : 0), totalWorkDuration, 1, 1, "Joining videos", progress, token).ConfigureAwait(false);
            return new([new(spec.Inputs[0].Path, output, true, convert ? "Converted and joined." : "Joined without re-encoding.")]);
        }
        catch (OperationCanceledException) { TryDelete(output); throw; }
        catch { TryDelete(output); throw; }
    }

    public static (bool Compatible, string Reason) CheckJoinCompatibility(ImmutableArray<MediaInfo> items)
    {
        if (items.Length < 2) return (false, "Add at least two videos.");
        var a = items[0];
        foreach (var b in items.Skip(1))
        {
            if (!a.HasVideo || !b.HasVideo || a.VariableFrameRate || b.VariableFrameRate || !string.Equals(a.VideoCodec, b.VideoCodec, StringComparison.OrdinalIgnoreCase) || a.OrientedWidth != b.OrientedWidth || a.OrientedHeight != b.OrientedHeight || a.FrameRate != b.FrameRate)
                return (false, "Video formats differ.");
            if (a.AudioTracks.Length != b.AudioTracks.Length || a.AudioTracks.Zip(b.AudioTracks).Any(pair => !string.Equals(pair.First.Codec, pair.Second.Codec, StringComparison.OrdinalIgnoreCase) || pair.First.Channels != pair.Second.Channels || pair.First.SampleRate != pair.Second.SampleRate))
                return (false, "Audio formats differ or some clips have no audio.");
        }
        return (true, "Matching video and audio streams. Fast Join keeps the original encoding.");
    }

    private static List<string> BuildSingleArgs(MediaJobSpec spec, MediaInfo media, string output)
    {
        var args = new List<string>();
        var precise = spec.Quality == QualityMode.Precise;
        if (!precise && spec.Operation is MediaOperation.TrimAudio or MediaOperation.TrimVideo) args.AddRange(["-ss", F(spec.Selection!.Value.Start)]);
        args.AddRange(["-i", media.Path]);
        if (precise && spec.Operation is MediaOperation.TrimAudio or MediaOperation.TrimVideo) args.AddRange(["-ss", F(spec.Selection!.Value.Start)]);
        if (spec.Operation is MediaOperation.TrimAudio or MediaOperation.TrimVideo) args.AddRange(["-t", F(spec.Selection!.Value.Duration)]);
        args.AddRange(["-map", spec.Operation == MediaOperation.TrimAudio ? "0:a:0" : "0:v:0"]);
        if (spec.Operation is MediaOperation.TrimVideo or MediaOperation.CropVideo) args.AddRange(["-map", "0:a?"]);
        args.AddRange(["-sn", "-dn"]);
        if (spec.Operation == MediaOperation.TrimAudio)
        {
            if (precise) args.AddRange(["-c:a", "pcm_s16le"]); else args.AddRange(["-c:a", "copy"]);
        }
        else if (spec.Operation == MediaOperation.CropVideo)
        {
            var crop = spec.Crop!.Value;
            // FFmpeg applies display-matrix rotation before user filters. This keeps crop
            // coordinates in the same displayed orientation used by the WPF overlay.
            args.AddRange(["-vf", $"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}", "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-c:a", "aac", "-b:a", "192k"]);
        }
        else if (precise) args.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", "18", "-c:a", "aac", "-b:a", "192k"]);
        else args.AddRange(["-c", "copy"]);
        if (Path.GetExtension(output).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-y", output]);
        return args;
    }

    private static List<string> BuildExtractArgs(MediaInfo input, AudioTrackInfo track, AudioOutputFormat format, string output)
    {
        var args = new List<string> { "-i", input.Path, "-map", $"0:a:{track.Ordinal}", "-vn" };
        switch (format)
        {
            case AudioOutputFormat.Mp3: args.AddRange(["-c:a", "libmp3lame", "-q:a", "2"]); break;
            case AudioOutputFormat.Wav: args.AddRange(["-c:a", "pcm_s16le"]); break;
            case AudioOutputFormat.Flac: args.AddRange(["-c:a", "flac"]); break;
            case AudioOutputFormat.Aac: args.AddRange(["-c:a", "aac", "-b:a", "192k"]); break;
            case AudioOutputFormat.Opus: args.AddRange(["-c:a", "libopus", "-b:a", "160k"]); break;
            default: args.AddRange(["-c:a", "copy"]); break;
        }
        args.AddRange(["-y", output]);
        return args;
    }

    private async Task RunFfmpegAsync(IReadOnlyList<string> args, double trackDuration, TimeSpan elapsedBefore, double totalDuration, int itemIndex, int itemCount, string activity, Action<MediaProgress>? progress, CancellationToken token)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-nostats", "-progress", "pipe:1" }) psi.ArgumentList.Add(arg);
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("FFmpeg could not be started. Check the packaged FFmpeg files.");
        using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        var started = DateTime.UtcNow;
        var errorTask = process.StandardError.ReadToEndAsync(token);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                var parts = line.Split('=', 2);
                if (parts.Length != 2 || parts[0] is not ("out_time_us" or "out_time_ms") || !long.TryParse(parts[1], out var microseconds)) continue;
                var current = Math.Clamp(microseconds / 1_000_000d, 0, Math.Max(0, trackDuration));
                var complete = Math.Clamp(elapsedBefore.TotalSeconds + current, 0, Math.Max(0.001, totalDuration));
                var percent = totalDuration > 0 ? Math.Clamp(complete / totalDuration * 100, 0, 99) : 0;
                var elapsed = DateTime.UtcNow - started + elapsedBefore;
                TimeSpan? remain = complete > 0 && totalDuration > complete ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (totalDuration - complete) / complete) : null;
                progress?.Invoke(new(percent, elapsed, remain, itemIndex, itemCount, activity));
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException(Explain(error));
        }
        catch (OperationCanceledException) { try { if (!process.HasExited) process.Kill(true); } catch { } throw; }
    }

    private async Task<bool> RunPreviewToolAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg could not be started for preview generation.");
        using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await stdout.ConfigureAwait(false); await stderr.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) { try { if (!process.HasExited) process.Kill(true); } catch { } throw; }
    }

    private static string CreatePreviewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "MediaToolbox", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<string> RunCaptureAsync(string executable, IEnumerable<string> args, CancellationToken token)
    {
        var psi = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("FFprobe could not be started. Check that FFprobe is included with the app.");
        var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        var output = await stdout.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException(Explain(await stderr.ConfigureAwait(false)));
        return output;
    }

    private static int ReadRotation(JsonElement stream)
    {
        double value = 0;
        if (stream.TryGetProperty("tags", out var tags)) value = ParseDouble(GetString(tags, "rotate"));
        if (stream.TryGetProperty("side_data_list", out var sideData))
            foreach (var side in sideData.EnumerateArray()) if (side.TryGetProperty("rotation", out var rotation)) { value = rotation.GetDouble(); break; }
        var normalized = ((int)Math.Round(value) % 360 + 360) % 360;
        return normalized is 90 or 180 or 270 ? normalized : 0;
    }

    private static string AudioExtension(AudioOutputFormat format, string codec) => format switch
    {
        AudioOutputFormat.Mp3 => ".mp3", AudioOutputFormat.Wav => ".wav", AudioOutputFormat.Flac => ".flac", AudioOutputFormat.Aac => ".m4a", AudioOutputFormat.Opus => ".opus",
        _ => codec.ToLowerInvariant() switch { "mp3" => ".mp3", "flac" => ".flac", "opus" => ".opus", "vorbis" => ".ogg", "pcm_s16le" or "pcm_s24le" or "pcm_f32le" => ".wav", "aac" or "alac" => ".m4a", _ => ".mka" }
    };

    private static string VideoExtension(VideoContainer container, string videoCodec, IReadOnlyList<string> audioCodecs)
    {
        if (container == VideoContainer.Mkv) return ".mkv";
        if (container == VideoContainer.Mp4) return ".mp4";
        var mp4Video = new[] { "h264", "hevc", "av1", "mpeg4", "mpeg2video", "mjpeg" };
        var mp4Audio = new[] { "aac", "mp3", "alac", "ac3", "eac3" };
        return mp4Video.Contains(videoCodec, StringComparer.OrdinalIgnoreCase) && audioCodecs.All(codec => mp4Audio.Contains(codec, StringComparer.OrdinalIgnoreCase)) ? ".mp4" : ".mkv";
    }

    private static string SafeStem(string name, string fallback) { var stem = Path.GetFileNameWithoutExtension(name.Trim()); return string.IsNullOrWhiteSpace(stem) ? Path.GetFileNameWithoutExtension(fallback) + "_output" : stem; }
    private static string UniquePath(string folder, string stem, string extension) { var path = Path.Combine(folder, stem + extension); for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{stem} ({i}){extension}"); return path; }
    private static string EscapeConcatPath(string path) => Path.GetFullPath(path).Replace('\\', '/').Replace("'", "'\\''");
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
    private static string Locate(string root, string exe, string fallback) { var candidate = Path.Combine(root, exe); return File.Exists(candidate) ? candidate : fallback; }
    private static string F(double n) => n.ToString("0.######", CultureInfo.InvariantCulture);
    private static string GetString(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString() : "";
    private static int GetInt(JsonElement element, string key) => int.TryParse(GetString(element, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static int GetInt(JsonElement element, string key, string nestedKey) => element.TryGetProperty(key, out var nested) ? GetInt(nested, nestedKey) : 0;
    private static int ParseInt(string value) => int.TryParse(value, out var result) ? result : 0;
    private static double ParseDouble(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;
    private static string NormalizeRate(string value) { var rate = ParseRate(value); return rate > 0 ? rate.ToString("0.####", CultureInfo.InvariantCulture) : ""; }
    private static double ParseRateOrZero(string value) { var bits = value.Split('/'); if (bits.Length == 2 && double.TryParse(bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b != 0) return a / b; return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0 ? r : 0; }
    private static double ParseRate(string value) { var bits = value.Split('/'); if (bits.Length == 2 && double.TryParse(bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b != 0) return a / b; return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0 ? r : 30; }
    private static string Explain(string message)
    {
        var lines = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var useful = lines.LastOrDefault(x => !x.Contains("frame=", StringComparison.OrdinalIgnoreCase) && !x.Contains("time=", StringComparison.OrdinalIgnoreCase)) ?? "FFmpeg could not complete this operation.";
        if (useful.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase)) return "FFmpeg could not read this media file. Try opening it in a player first.";
        if (useful.Contains("codec", StringComparison.OrdinalIgnoreCase) && useful.Contains("not currently", StringComparison.OrdinalIgnoreCase)) return "The output format cannot store this codec. Choose MKV or select a precise conversion mode.";
        return useful.Length > 240 ? useful[^240..] : useful;
    }
}
