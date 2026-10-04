using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaToolbox.Wpf;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var mediaRoot = Path.Combine(Path.GetTempPath(), "MediaToolbox.Smoke", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(mediaRoot);
var outRoot = Path.Combine(mediaRoot, "smoke_outputs");
Directory.CreateDirectory(outRoot);
var passed = new List<string>();
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); passed.Add(name); Console.WriteLine("PASS: " + name); }
Environment.SetEnvironmentVariable("MEDIA_TOOLBOX_SETTINGS_PATH", Path.Combine(mediaRoot, "settings.json"));
var uiThread = new Thread(() =>
{
    var wpfApp = new App(); wpfApp.InitializeComponent();
    var window = new MainWindow();
    var setTool = typeof(MainWindow).GetMethod("SetTool", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new Exception("Tool navigation was not found.");
    foreach (var tool in new[] { "Extract Audio", "Trim Audio", "Trim Video", "Crop Frame", "Join Videos" })
    {
        setTool.Invoke(window, [tool]);
        var queueVisible = ((FrameworkElement)window.FindName("QueueWorkspace")).Visibility == Visibility.Visible;
        var previewVisible = ((FrameworkElement)window.FindName("PreviewWorkspace")).Visibility == Visibility.Visible;
        var expectedQueue = tool is "Extract Audio" or "Join Videos";
        Check(queueVisible == expectedQueue && previewVisible != expectedQueue, $"{tool} opens its dedicated workspace");
    }
    var themeClick = typeof(MainWindow).GetMethod("ThemeClick", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new Exception("Theme switch was not found.");
    themeClick.Invoke(window, [window, new RoutedEventArgs()]);
    Check(((SolidColorBrush)wpfApp.Resources["PageBrush"]).Color == (Color)ColorConverter.ConvertFromString("#F2F5F8"), "light theme applies across shared resources");
    themeClick.Invoke(window, [window, new RoutedEventArgs()]);
    Check(((SolidColorBrush)wpfApp.Resources["PageBrush"]).Color == (Color)ColorConverter.ConvertFromString("#11161D"), "dark theme can be restored");
    setTool.Invoke(window, ["Trim Video"]);
    window.Show(); window.UpdateLayout();
    var layoutImage = new RenderTargetBitmap(1536, 960, 96, 96, PixelFormats.Pbgra32);
    layoutImage.Render(window);
    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(layoutImage));
    using (var file = File.Create(Path.Combine(root, ".test_media", "wpf-layout.png"))) png.Save(file);
    window.Hide();
    window.Close(); wpfApp.Shutdown();
});
uiThread.SetApartmentState(ApartmentState.STA); uiThread.Start(); uiThread.Join();
Environment.SetEnvironmentVariable("MEDIA_TOOLBOX_SETTINGS_PATH", null);
var processor = new MediaProcessor(root);

async Task RunFfmpeg(string[] args)
{
    var psi = new ProcessStartInfo(Path.Combine(root, "ffmpeg.exe")) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
    foreach (var arg in args) psi.ArgumentList.Add(arg);
    using var p = Process.Start(psi) ?? throw new Exception("Could not start FFmpeg fixture generation.");
    var error = await p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
    if (p.ExitCode != 0) throw new Exception(error);
}

await RunFfmpeg(["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=25:duration=1", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-c:a", "aac", "-shortest", "-y", Path.Combine(mediaRoot, "a.mp4")]);
await RunFfmpeg(["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=200x120:rate=30:duration=1", "-c:v", "libx264", "-an", "-y", Path.Combine(mediaRoot, "b.mp4")]);

var multi = Path.Combine(mediaRoot, "multi-audio.mp4");
if (!File.Exists(multi))
    await RunFfmpeg(["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=4", "-f", "lavfi", "-i", "sine=frequency=440:duration=4", "-f", "lavfi", "-i", "sine=frequency=880:duration=4", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-metadata:s:a:0", "language=eng", "-metadata:s:a:0", "title=Main", "-metadata:s:a:1", "language=fra", "-metadata:s:a:1", "title=Commentary", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-y", multi]);
var rotatedPath = Path.Combine(mediaRoot, "rotated-90.mp4");
if (!File.Exists(rotatedPath)) await RunFfmpeg(["-hide_banner", "-loglevel", "error", "-display_rotation:v:0", "90", "-i", multi, "-c", "copy", "-y", rotatedPath]);

var a = await processor.ProbeAsync(Path.Combine(mediaRoot, "a.mp4"));
var b = await processor.ProbeAsync(Path.Combine(mediaRoot, "b.mp4"));
var m = await processor.ProbeAsync(multi);
Check(a.HasVideo && a.AudioTracks.Length == 1 && a.Duration > .9, "probe video and audio metadata");
Check(m.AudioTracks.Length == 2 && m.AudioTracks[1].Title == "Commentary" && m.AudioTracks[1].Language == "fra", "probe two audio tracks and language/title metadata");
Check(!b.AudioTracks.Any(), "probe clip without audio");
var rotated = await processor.ProbeAsync(rotatedPath);
Check(rotated.Rotation == 90 && rotated.OrientedWidth == 180 && rotated.OrientedHeight == 320, "rotation metadata changes displayed source geometry");
var vfrPath = Path.Combine(mediaRoot, "vfr.mp4");
if (!File.Exists(vfrPath)) await RunFfmpeg(["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=4", "-vf", "select='not(mod(n,3))'", "-fps_mode", "vfr", "-c:v", "libx264", "-an", "-y", vfrPath]);
var vfr = await processor.ProbeAsync(vfrPath);
Check(vfr.VariableFrameRate, "probe marks variable-rate video for conversion joins");

var perFile = ImmutableDictionary<string, int?>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase).Add(m.Path, null);
var allTracks = await processor.ProcessAsync(new(MediaOperation.ExtractAudio, [m], outRoot, "all_tracks", AudioFormat: AudioOutputFormat.Auto, TrackSelections: perFile));
Check(allTracks.Outputs.Length == 2 && allTracks.Outputs.All(x => x.Succeeded), "extract all tracks without re-encoding");
Check((await processor.ProbeAsync(allTracks.Outputs[0].Output!)).AudioTracks[0].Codec == "aac", "extracted audio retains AAC codec");
var mp3 = await processor.ProcessAsync(new(MediaOperation.ExtractAudio, [m], outRoot, "converted_mp3", AudioFormat: AudioOutputFormat.Mp3, TrackSelections: perFile));
Check(mp3.Outputs.Length == 2 && mp3.Outputs.All(x => x.Succeeded) && (await processor.ProbeAsync(mp3.Outputs[0].Output!)).AudioTracks[0].Codec == "mp3", "selected audio format re-encodes to MP3 with matching extension");

var secondTrack = ImmutableDictionary<string, int?>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase).Add(m.Path, 1);
var selected = await processor.ProcessAsync(new(MediaOperation.ExtractAudio, [m], outRoot, "selected_track", TrackSelections: secondTrack));
Check(selected.Outputs.Length == 1 && selected.Outputs[0].Succeeded && selected.Outputs[0].Output!.Contains("selected_track", StringComparison.Ordinal), "extract only selected second track");
var unique = await processor.ProcessAsync(new(MediaOperation.ExtractAudio, [m], outRoot, "selected_track", TrackSelections: secondTrack));
Check(unique.Outputs[0].Output != selected.Outputs[0].Output && File.Exists(unique.Outputs[0].Output!), "existing output receives a unique name");

var partialChoices = ImmutableDictionary<string, int?>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase).Add(m.Path, null).Add(b.Path, null);
var partial = await processor.ProcessAsync(new(MediaOperation.ExtractAudio, [m, b], outRoot, "", TrackSelections: partialChoices));
Check(partial.Outputs.Length == 3 && partial.Outputs.Count(x => x.Succeeded) == 2 && partial.FailedCount == 1, "batch extraction reports a missing-audio failure and continues");

var fastTrim = await processor.ProcessAsync(new(MediaOperation.TrimVideo, [a], outRoot, "fast_trim", Quality: QualityMode.Fast, Selection: new(.2, .8)));
var fastProbe = await processor.ProbeAsync(fastTrim.Outputs[0].Output!);
Check(fastTrim.Succeeded && fastProbe.VideoCodec == a.VideoCodec, "fast video trim preserves source codec");
var preciseTrim = await processor.ProcessAsync(new(MediaOperation.TrimVideo, [a], outRoot, "precise_trim", Quality: QualityMode.Precise, Selection: new(.2, .8)));
var preciseProbe = await processor.ProbeAsync(preciseTrim.Outputs[0].Output!);
Check(preciseTrim.Succeeded && Math.Abs(preciseProbe.Duration - .6) <= 1d / 25 + .00001, "precise video trim duration follows requested range within one frame");
var audioTrim = await processor.ProcessAsync(new(MediaOperation.TrimAudio, [a], outRoot, "audio_trim", Quality: QualityMode.Precise, Selection: new(.2, .8)));
var audioTrimProbe = await processor.ProbeAsync(audioTrim.Outputs[0].Output!);
Check(audioTrim.Succeeded && Path.GetExtension(audioTrim.Outputs[0].Output) == ".wav" && Math.Abs(audioTrimProbe.Duration - .6) <= 1d / 44100 + .000002, "precise audio trim exports sample-accurate PCM WAV");

var cropSpec = new MediaJobSpec(MediaOperation.CropVideo, [a], outRoot, "crop_test", Crop: new CropRectangle(20, 20, 100, 80));
Check(MediaProcessor.Validate(cropSpec).Length == 0, "crop range validates even source-pixel geometry");
var cropOut = await processor.ProcessAsync(cropSpec);
var cropProbe = await processor.ProbeAsync(cropOut.Outputs[0].Output!);
Check(cropOut.Succeeded && cropProbe.Width == 100 && cropProbe.Height == 80, "crop export matches selected pixels");
var rotatedCrop = await processor.ProcessAsync(new(MediaOperation.CropVideo, [rotated], outRoot, "rotated_crop", Crop: new CropRectangle(20, 20, 100, 80)));
var rotatedCropProbe = await processor.ProbeAsync(rotatedCrop.Outputs[0].Output!);
Check(rotatedCrop.Succeeded && rotatedCropProbe.Width == 100 && rotatedCropProbe.Height == 80 && rotatedCropProbe.Rotation == 0, "rotated crop exports displayed orientation and clears rotation metadata");

var compatibleJoin = await processor.ProcessAsync(new(MediaOperation.JoinVideos, [a, a], outRoot, "joined_fast", Join: JoinMode.Fast));
Check(compatibleJoin.Succeeded && Math.Abs((await processor.ProbeAsync(compatibleJoin.Outputs[0].Output!)).Duration - 2) < .1, "compatible join uses fast stream-copy path");
var joinProgress = new List<double>();
var convertedJoin = await processor.ProcessAsync(new(MediaOperation.JoinVideos, [a, b], outRoot, "joined_convert", Join: JoinMode.Automatic), value => joinProgress.Add(value.Percent));
var convertedProbe = await processor.ProbeAsync(convertedJoin.Outputs[0].Output!);
Check(convertedJoin.Succeeded && convertedProbe.VideoCodec == "h264" && convertedProbe.AudioTracks.Length == 1 && Math.Abs(convertedProbe.Duration - 2) < .2, "incompatible join converts mixed dimensions/rates and fills missing audio");
Check(joinProgress.Zip(joinProgress.Skip(1)).All(pair => pair.Second + .01 >= pair.First), "join progress advances through conversion and final join");

var oddCrop = new MediaJobSpec(MediaOperation.CropVideo, [a], outRoot, "invalid_crop", Crop: new CropRectangle(1, 1, 101, 81));
Check(MediaProcessor.Validate(oddCrop).Contains("even pixel", StringComparison.OrdinalIgnoreCase), "invalid crop is rejected with an actionable explanation");
var incompatibleMp4 = new MediaJobSpec(MediaOperation.TrimVideo, [a with { VideoCodec = "vp9", AudioTracks = [new AudioTrackInfo(0, 1, "opus", "", "", 2, 48000)] }], outRoot, "invalid_mp4", Container: VideoContainer.Mp4, Quality: QualityMode.Fast, Selection: new(.1, .5));
Check(MediaProcessor.Validate(incompatibleMp4).Contains("Auto or MKV", StringComparison.OrdinalIgnoreCase), "incompatible explicit MP4 choice has actionable guidance");

var unicodeFolder = Path.Combine(outRoot, "音楽 output"); Directory.CreateDirectory(unicodeFolder);
var unicode = await processor.ProcessAsync(new(MediaOperation.TrimVideo, [a], unicodeFolder, "clip_é", Quality: QualityMode.Precise, Selection: new(.1, .7)));
Check(unicode.Succeeded && File.Exists(unicode.Outputs[0].Output!), "Unicode paths work for processing and output");

var longInputPath = Path.Combine(mediaRoot, "cancel-source.mp4");
if (!File.Exists(longInputPath)) await RunFfmpeg(["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=12", "-c:v", "libx264", "-preset", "ultrafast", "-an", "-y", longInputPath]);
var longInput = await processor.ProbeAsync(longInputPath);
var tempRoot = Path.Combine(Path.GetTempPath(), "MediaToolbox");
Directory.CreateDirectory(tempRoot);
var tempBefore = Directory.GetDirectories(tempRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
using (var cancel = new CancellationTokenSource())
{
    var cancelTask = processor.ProcessAsync(new(MediaOperation.TrimVideo, [longInput], outRoot, "cancelled_job", Quality: QualityMode.Precise, Selection: new(0, longInput.Duration)), cancellationToken: cancel.Token);
    await Task.Delay(120); cancel.Cancel();
    var wasCancelled = false;
    try { await cancelTask; } catch (OperationCanceledException) { wasCancelled = true; }
    var tempAfter = Directory.GetDirectories(tempRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Check(wasCancelled && !Directory.EnumerateFiles(outRoot, "cancelled_job*").Any() && tempAfter.SetEquals(tempBefore), "cancellation removes partial output and per-job temporary files");
}

Console.WriteLine($"All {passed.Count} smoke checks passed.");
Directory.Delete(mediaRoot, true);
