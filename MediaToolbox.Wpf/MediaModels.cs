using System.Collections.Immutable;

namespace MediaToolbox.Wpf;

public sealed record AudioTrackInfo(int Ordinal, int StreamIndex, string Codec, string Language, string Title, int Channels, int SampleRate);

public sealed record MediaInfo(
    string Path,
    double Duration,
    int Width,
    int Height,
    string VideoCodec,
    string FrameRate,
    int Rotation,
    ImmutableArray<AudioTrackInfo> AudioTracks,
    bool HasVideo,
    bool VariableFrameRate = false,
    string? Thumbnail = null)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Dimensions => HasVideo ? $"{OrientedWidth} × {OrientedHeight}" : "Audio only";
    public int OrientedWidth => Math.Abs(Rotation) % 180 == 90 ? Height : Width;
    public int OrientedHeight => Math.Abs(Rotation) % 180 == 90 ? Width : Height;
    public string Summary => string.Join(" · ", new[] { Dimensions, VideoCodec.ToUpperInvariant(), HasVideo ? $"{FrameRate} fps{(VariableFrameRate ? " VFR" : "")}" : "", $"{AudioTracks.Length} audio track(s)", TimeSpan.FromSeconds(Math.Max(0, Duration)).ToString(@"hh\:mm\:ss") }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

public readonly record struct MediaSelection(double Start, double End)
{
    public double Duration => End - Start;
}

public readonly record struct CropRectangle(int X, int Y, int Width, int Height);

public enum MediaOperation { ExtractAudio, TrimAudio, TrimVideo, CropVideo, JoinVideos }
public enum QualityMode { Fast, Precise }
public enum JoinMode { Automatic, Fast, Convert }
public enum VideoContainer { Auto, Mp4, Mkv }
public enum AudioOutputFormat { Auto, Mp3, Wav, Flac, Aac, Opus }

/// <summary>All values required to execute a media job. This object is created by the UI and is never read from controls.</summary>
public sealed record MediaJobSpec(
    MediaOperation Operation,
    ImmutableArray<MediaInfo> Inputs,
    string OutputFolder,
    string OutputName,
    VideoContainer Container = VideoContainer.Auto,
    QualityMode Quality = QualityMode.Fast,
    JoinMode Join = JoinMode.Automatic,
    AudioOutputFormat AudioFormat = AudioOutputFormat.Auto,
    MediaSelection? Selection = null,
    CropRectangle? Crop = null,
    ImmutableDictionary<string, int?>? TrackSelections = null);

public sealed record MediaJobOutput(string Input, string? Output, bool Succeeded, string Message);
public sealed record MediaJobResult(ImmutableArray<MediaJobOutput> Outputs)
{
    public bool Succeeded => Outputs.Any(x => x.Succeeded);
    public int FailedCount => Outputs.Count(x => !x.Succeeded);
}

public sealed class MediaJobCanceledException(ImmutableArray<MediaJobOutput> completedOutputs) : OperationCanceledException("The media job was cancelled.")
{
    public ImmutableArray<MediaJobOutput> CompletedOutputs { get; } = completedOutputs;
}

public sealed record MediaProgress(double Percent, TimeSpan Elapsed, TimeSpan? Remaining, int ItemIndex, int ItemCount, string Activity);
