using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Path = System.IO.Path;

namespace MediaToolbox.Wpf;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<BitmapImage> thumbnails = [];
    public ObservableCollection<QueueEntry> Queue { get; } = [];
    private readonly ConcurrentDictionary<QueueEntry, CancellationTokenSource> queueThumbnailJobs = new();
    private readonly MediaProcessor processor = new();
    private readonly DispatcherTimer playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly string settingsPath;
    private CancellationTokenSource? previewCancellation;
    private CancellationTokenSource? jobCancellation;
    private MediaInfo? activeMedia;
    private double duration, selectionStart, selectionEnd;
    private bool updatingMedia, updatingRangeText, updatingCropText, draggingPosition, draggingTimeline, draggingCrop;
    private bool isLightTheme;
    private string draggedHandle = "";
    private string cropDragMode = "";
    private Point cropDragStart;
    private CropRectangle cropDragOrigin;
    private CropRectangle crop = new(0, 0, 2, 2);
    private double volumeBeforeMute = .7;
    private string currentTool = "Trim Video";
    private string savedOutputFolder = "";
    private bool jobRunning;
    private Point queueDragStart;
    private int lastQualityIndex;

    public MainWindow()
    {
        settingsPath = Environment.GetEnvironmentVariable("MEDIA_TOOLBOX_SETTINGS_PATH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Media Toolbox", "settings.json");
        InitializeComponent();
        DataContext = this;
        ThumbnailStrip.ItemsSource = thumbnails;
        playbackTimer.Tick += PlaybackTick;
        QueueList.SelectionChanged += QueueSelectionChanged;
        Closed += OnClosed;
        InputBox.AllowDrop = true;
        LoadSettings();
        SetTool("Trim Video");
        SyncTimeline();
    }

    private bool IsQueueTool => currentTool is "Extract Audio" or "Join Videos";
    private bool IsAudioTool => currentTool is "Extract Audio" or "Trim Audio";

    private async void ToolClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string name || jobRunning) return;
        SetTool(name);
        if (Queue.Count > 0 && Queue.FirstOrDefault()?.Media is { } first)
        {
            if (name == "Extract Audio" && first.AudioTracks.IsDefaultOrEmpty) SetError("This file has no audio track to extract.");
            else if (name == "Join Videos" && !first.HasVideo) SetError("Join Videos needs video clips. Add one or more video files.");
            else await LoadPreviewAsync(first);
        }
    }

    private void SetTool(string name)
    {
        if (currentTool != name)
        {
            var seed = activeMedia;
            foreach (var stale in Queue.ToArray()) CancelQueueThumbnail(stale);
            Queue.Clear();
            if (seed is not null && SupportsTool(seed, name))
            {
                Queue.Add(new QueueEntry(seed, name == "Extract Audio"));
                QueueList.SelectedIndex = 0;
            }
            else if (seed is not null)
            {
                activeMedia = null;
                Player.Stop(); Player.Source = null; thumbnails.Clear(); EmptyState.Visibility = Visibility.Visible;
                MediaFileName.Text = "No compatible media selected"; MediaMetadata.Text = "Choose a file for this tool"; SourceSizeText.Text = "—";
                updatingMedia = true; InputBox.Text = ""; updatingMedia = false; OutputNameBox.Text = "";
                SetDuration(0);
            }
        }
        currentTool = name;
        var batch = name is "Extract Audio" or "Join Videos";
        PreviewWorkspace.Visibility = batch ? Visibility.Collapsed : Visibility.Visible;
        QueueWorkspace.Visibility = batch ? Visibility.Visible : Visibility.Collapsed;
        TimelinePanel.Visibility = name is "Trim Audio" or "Trim Video" or "Crop Frame" ? Visibility.Visible : Visibility.Collapsed;
        CropCanvas.Visibility = name == "Crop Frame" ? Visibility.Visible : Visibility.Collapsed;
        PlayOverlayButton.Visibility = batch ? Visibility.Collapsed : Visibility.Visible;
        TrimSettingsPanel.Visibility = name is "Trim Audio" or "Trim Video" ? Visibility.Visible : Visibility.Collapsed;
        CropSettingsPanel.Visibility = name == "Crop Frame" ? Visibility.Visible : Visibility.Collapsed;
        ExtractSettingsPanel.Visibility = name == "Extract Audio" ? Visibility.Visible : Visibility.Collapsed;
        JoinSettingsPanel.Visibility = name == "Join Videos" ? Visibility.Visible : Visibility.Collapsed;
        ContainerRow.Visibility = name is "Extract Audio" or "Trim Audio" ? Visibility.Collapsed : Visibility.Visible;
        AudioContainerHint.Visibility = name is "Extract Audio" or "Trim Audio" ? Visibility.Visible : Visibility.Collapsed;
        ModeBox.Items.Clear();
        if (name == "Trim Audio") { ModeBox.Items.Add(new ComboBoxItem { Content = "Fast · stream copy" }); ModeBox.Items.Add(new ComboBoxItem { Content = "Precise · PCM WAV" }); ModeBox.SelectedIndex = Math.Clamp(lastQualityIndex, 0, 1); }
        else if (name == "Trim Video") { ModeBox.Items.Add(new ComboBoxItem { Content = "Fast · stream copy" }); ModeBox.Items.Add(new ComboBoxItem { Content = "Precise · H.264 / AAC" }); ModeBox.SelectedIndex = Math.Clamp(lastQualityIndex, 0, 1); }
        else ModeBox.SelectedIndex = -1;

        TitleText.Text = name switch { "Extract Audio" => "Extract Audio", "Trim Audio" => "Trim Audio", "Trim Video" => "Trim Video", "Crop Frame" => "Crop Frame", "Join Videos" => "Join Videos", _ => name };
        SubtitleText.Text = name switch
        {
            "Extract Audio" => "Choose one or more files and export the audio tracks you need.",
            "Trim Audio" => "Listen, set a range, and save a clean audio clip.",
            "Trim Video" => "Choose a range visually, then export a clean video clip.",
            "Crop Frame" => "Drag a crop over the displayed picture and match it to the source pixels.",
            "Join Videos" => "Arrange clips, check stream compatibility, and join them together.",
            _ => "This editor tool is not available yet."
        };
        InspectorTitle.Text = name switch { "Extract Audio" => "Track settings", "Trim Audio" => "Audio trim", "Trim Video" => "Video trim", "Crop Frame" => "Crop settings", "Join Videos" => "Join settings", _ => "Coming soon" };
        BrowseButton.Content = batch ? "Add files" : "Browse media";
        QueueTitle.Text = name == "Join Videos" ? "Clips in this join" : "Files to process";
        QueueHint.Text = name == "Join Videos" ? "Drag to reorder. The compatibility check updates as the list changes." : "Select All tracks or one track for each queued file.";
        ExportButton.Content = name switch { "Extract Audio" => "Extract audio", "Trim Audio" => "Export trimmed audio", "Trim Video" => "Export trimmed video", "Crop Frame" => "Export cropped video", "Join Videos" => "Join videos", _ => "Coming soon" };
        ExportButton.IsEnabled = !jobRunning;
        ExtractNav.Background = name == "Extract Audio" ? (Brush)Application.Current.Resources["ButtonBrush"] : Brushes.Transparent;
        TrimAudioNav.Background = name == "Trim Audio" ? (Brush)Application.Current.Resources["ButtonBrush"] : Brushes.Transparent;
        TrimVideoNav.Background = name == "Trim Video" ? (Brush)Application.Current.Resources["ButtonBrush"] : Brushes.Transparent;
        CropNav.Background = name == "Crop Frame" ? (Brush)Application.Current.Resources["ButtonBrush"] : Brushes.Transparent;
        JoinNav.Background = name == "Join Videos" ? (Brush)Application.Current.Resources["ButtonBrush"] : Brushes.Transparent;
        if (activeMedia is not null)
        {
            OutputNameBox.Text = DefaultOutputName(activeMedia, name);
            SetQualityNote();
            if (name == "Crop Frame") InitializeCrop();
        }
        UpdateQueueSummary();
        UpdateJoinCompatibility();
        SetReady("Ready");
    }

    private static bool SupportsTool(MediaInfo media, string tool) => tool switch
    {
        "Extract Audio" or "Trim Audio" => !media.AudioTracks.IsDefaultOrEmpty,
        "Trim Video" or "Crop Frame" or "Join Videos" => media.HasVideo,
        _ => false
    };

    private void ComingSoonClick(object sender, RoutedEventArgs e)
    {
        var name = (sender as FrameworkElement)?.Tag?.ToString() ?? "This tool";
        var info = new Window { Title = name, Owner = this, Width = 360, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = (Brush)Application.Current.Resources["PanelBrush"] };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = name, FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "This feature is marked for a future update. It won't start a different operation by mistake.", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["MutedBrush"], Margin = new Thickness(0, 9, 0, 16) });
        var close = new Button { Content = "Got it", HorizontalAlignment = HorizontalAlignment.Right }; close.Click += (_, _) => info.Close(); panel.Children.Add(close); info.Content = panel; info.ShowDialog();
    }

    private async void BrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = IsQueueTool ? "Add media files" : "Choose a media file", Filter = "Media files|*.mp4;*.mkv;*.mov;*.webm;*.m4v;*.avi;*.wmv;*.mp3;*.m4a;*.aac;*.wav;*.flac;*.ogg;*.opus|All files|*.*", CheckFileExists = true, Multiselect = IsQueueTool };
        if (dialog.ShowDialog(this) == true) await AddPathsAsync(dialog.FileNames);
    }

    private async void InputChanged(object sender, TextChangedEventArgs e)
    {
        var text = (sender as TextBox)?.Text ?? "";
        if (updatingMedia || !File.Exists(text)) return;
        await AddPathsAsync([text.Trim()]);
    }

    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var offered = paths.Where(File.Exists).Select(Path.GetFullPath).ToArray();
        var candidates = offered.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var skippedDuplicates = offered.Length - candidates.Length;
        if (candidates.Length == 0) { SetError("Choose a file that exists on this computer."); return; }
        if (!IsQueueTool) candidates = candidates.Take(1).ToArray();
        if (!IsQueueTool) { foreach (var stale in Queue.ToArray()) CancelQueueThumbnail(stale); Queue.Clear(); thumbnails.Clear(); }
        SetStatus("Reading file details…");
        foreach (var path in candidates)
        {
            if (Queue.Any(x => string.Equals(x.Media.Path, path, StringComparison.OrdinalIgnoreCase))) { skippedDuplicates++; continue; }
            try
            {
                var info = await processor.ProbeAsync(path);
                if (currentTool == "Join Videos" && !info.HasVideo) { SetError($"{Path.GetFileName(path)} does not contain a video stream, so it was not added to the join queue."); continue; }
                if (currentTool is "Trim Video" or "Crop Frame" && !info.HasVideo) { SetError("This file does not contain a video stream. Choose a video for this tool."); continue; }
                if (currentTool == "Trim Audio" && info.AudioTracks.IsDefaultOrEmpty) { SetError("This file has no audio track to trim."); continue; }
                var entry = new QueueEntry(info, currentTool == "Extract Audio");
                Queue.Add(entry);
                _ = LoadQueueThumbnailAsync(entry);
            }
            catch (Exception ex) { SetError($"Could not inspect {Path.GetFileName(path)}: {ex.Message}"); }
        }
        if (Queue.Count == 0) { UpdateQueueSummary(); return; }
        QueueList.SelectedIndex = 0;
        UpdateQueueSummary(); UpdateJoinCompatibility();
        await LoadPreviewAsync(Queue[0].Media);
        if (currentTool == "Extract Audio" && Queue.Count > 1) OutputNameBox.Text = "";
        if (IsQueueTool) SetReady(skippedDuplicates > 0 ? $"{Queue.Count} file(s) ready · {skippedDuplicates} duplicate(s) skipped" : $"{Queue.Count} file(s) ready");
    }

    private async Task LoadPreviewAsync(MediaInfo media)
    {
        var mediaChanged = activeMedia?.Path != media.Path;
        previewCancellation?.Cancel(); previewCancellation?.Dispose();
        previewCancellation = new CancellationTokenSource();
        var token = previewCancellation.Token;
        activeMedia = media;
        updatingMedia = true;
        InputBox.Text = media.Path;
        ProjectName.Text = Path.GetFileNameWithoutExtension(media.Path);
        MediaFileName.Text = media.FileName;
        PreviewName.Text = media.FileName;
        MediaMetadata.Text = media.Summary;
        SourceSizeText.Text = FormatBytes(new FileInfo(media.Path).Length);
        if (mediaChanged)
        {
            if (currentTool == "Extract Audio" && Queue.Count > 1) { if (OutputNameBox.Text == "edited") OutputNameBox.Text = ""; }
            else if (currentTool == "Join Videos" && Queue.Count > 1) { if (OutputNameBox.Text == "edited") OutputNameBox.Text = DefaultOutputName(Queue[0].Media, currentTool); }
            else OutputNameBox.Text = DefaultOutputName(media, currentTool);
        }
        if (string.IsNullOrWhiteSpace(savedOutputFolder) || !Directory.Exists(savedOutputFolder)) savedOutputFolder = Path.GetDirectoryName(media.Path) ?? "";
        OutputFolderBox.Text = savedOutputFolder;
        EmptyState.Visibility = Visibility.Collapsed;
        StatusText.Text = "Preparing preview…";
        thumbnails.Clear(); WaveformImage.Source = null; WaveformImage.Visibility = Visibility.Collapsed;
        Player.Stop(); Player.Source = null; PlayButton.Content = PlayOverlayButton.Content = "▶"; playbackTimer.Stop();
        SetDuration(media.Duration);
        if (currentTool == "Crop Frame") InitializeCrop();
        try
        {
            Player.Source = new Uri(media.Path);
            Player.Play(); Player.Pause();
        }
        catch { /* Some codecs need the default player; the edit itself still uses FFmpeg. */ }
        updatingMedia = false;
        if (media.HasVideo) await MakeVideoThumbnailsAsync(media, token);
        else await MakeWaveformAsync(media, token);
        if (activeMedia?.Path == media.Path && !token.IsCancellationRequested) SetReady("Media ready");
    }

    private async Task MakeVideoThumbnailsAsync(MediaInfo media, CancellationToken token)
    {
        try
        {
            var frames = await processor.GenerateVideoThumbnailsAsync(media, 12, token);
            var loaded = frames.Select(LoadBitmap).Where(x => x is not null).Cast<BitmapImage>().ToArray();
            await Dispatcher.InvokeAsync(() =>
            {
                if (activeMedia?.Path != media.Path || token.IsCancellationRequested) return;
                thumbnails.Clear(); foreach (var bitmap in loaded) thumbnails.Add(bitmap);
                foreach (var entry in Queue.Where(x => x.Media.Path == media.Path)) entry.Thumbnail = loaded.FirstOrDefault();
                SyncTimeline();
            });
        }
        catch (OperationCanceledException) { }
        catch { /* Still allow media playback if thumbnail extraction is unsupported. */ }
    }

    private async Task LoadQueueThumbnailAsync(QueueEntry entry)
    {
        if (!entry.Media.HasVideo) return;
        var cancellation = new CancellationTokenSource();
        if (!queueThumbnailJobs.TryAdd(entry, cancellation)) { cancellation.Dispose(); return; }
        try
        {
            var image = await processor.GenerateThumbnailAsync(entry.Media, cancellation.Token);
            if (image is not null)
            {
                var bitmap = LoadBitmap(image);
                await Dispatcher.InvokeAsync(() => { if (Queue.Contains(entry)) entry.Thumbnail = bitmap; });
            }
        }
        catch { }
        finally { queueThumbnailJobs.TryRemove(entry, out _); cancellation.Dispose(); }
    }

    private void CancelQueueThumbnail(QueueEntry entry)
    {
        if (queueThumbnailJobs.TryGetValue(entry, out var source)) source.Cancel();
    }

    private async Task MakeWaveformAsync(MediaInfo media, CancellationToken token)
    {
        try
        {
            var image = await processor.GenerateAudioWaveformAsync(media.Path, token);
            if (image is not null)
            {
                var bitmap = LoadBitmap(image);
                await Dispatcher.InvokeAsync(() => { if (activeMedia?.Path == media.Path && currentTool == "Trim Audio") { WaveformImage.Source = bitmap; WaveformImage.Visibility = Visibility.Visible; } });
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private static BitmapImage? LoadBitmap(byte[] bytes)
    {
        try { using var stream = new MemoryStream(bytes); var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.DecodePixelHeight = 110; bitmap.EndInit(); bitmap.Freeze(); return bitmap; }
        catch { return null; }
    }

    private void MediaOpened(object sender, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan)
        {
            var seconds = Player.NaturalDuration.TimeSpan.TotalSeconds;
            if (seconds > 0 && (duration <= 0 || Math.Abs(seconds - duration) < .15)) SetDuration(seconds);
        }
        Player.Pause();
    }

    private void MediaEnded(object sender, RoutedEventArgs e) { Player.Position = TimeSpan.Zero; Player.Pause(); PlayButton.Content = PlayOverlayButton.Content = "▶"; playbackTimer.Stop(); }
    private void PlayClick(object sender, RoutedEventArgs e)
    {
        if (activeMedia is null) { SetError("Choose a media file to preview first."); return; }
        try
        {
            if (PlayButton.Content?.ToString() == "▶")
            {
                if (Player.Position.TotalSeconds < selectionStart || Player.Position.TotalSeconds >= selectionEnd) Player.Position = TimeSpan.FromSeconds(selectionStart);
                Player.Play(); PlayButton.Content = PlayOverlayButton.Content = "Ⅱ"; playbackTimer.Start();
            }
            else { Player.Pause(); PlayButton.Content = PlayOverlayButton.Content = "▶"; playbackTimer.Stop(); }
        }
        catch { SetError("The built-in player could not decode this media. Use Open in player to preview it."); }
    }

    private void PlaybackTick(object? sender, EventArgs e)
    {
        var position = Player.Position.TotalSeconds;
        if (currentTool is "Trim Audio" or "Trim Video" && position >= selectionEnd && selectionEnd > selectionStart)
        { Player.Pause(); PlayButton.Content = PlayOverlayButton.Content = "▶"; playbackTimer.Stop(); }
        PositionSlider.Value = Math.Clamp(position, 0, Math.Max(.01, PositionSlider.Maximum));
        TimeText.Text = $"{FormatTime(position)} / {FormatTime(duration)}"; UpdatePlayhead(position);
    }

    private void PositionMouseDown(object sender, MouseButtonEventArgs e) => draggingPosition = true;
    private void PositionMouseUp(object sender, MouseButtonEventArgs e) { draggingPosition = false; Seek(PositionSlider.Value); }
    private void PositionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        TimeText.Text = $"{FormatTime(PositionSlider.Value)} / {FormatTime(duration)}";
        if (draggingPosition) Seek(PositionSlider.Value);
    }
    private void Seek(double seconds)
    {
        if (duration <= 0) return;
        try { Player.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, duration)); } catch { }
        UpdatePlayhead(seconds);
    }
    private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (IsInitialized) Player.Volume = e.NewValue; }
    private void VolumeDownClick(object sender, RoutedEventArgs e)
    {
        if (VolumeSlider.Value > 0) { volumeBeforeMute = VolumeSlider.Value; VolumeSlider.Value = 0; }
        else VolumeSlider.Value = volumeBeforeMute <= 0 ? .7 : volumeBeforeMute;
    }

    private void SetDuration(double seconds)
    {
        duration = Math.Max(0, seconds); selectionStart = 0; selectionEnd = duration;
        PositionSlider.Maximum = Math.Max(duration, .01); PositionSlider.Value = 0;
        UpdateRangeFields(); TimeText.Text = $"{FormatTime(0)} / {FormatTime(duration)}";
    }
    private void TimelineDown(object sender, MouseButtonEventArgs e)
    {
        if (duration <= 0 || RangeCanvas.ActualWidth <= 0) return;
        var ratio = Math.Clamp(e.GetPosition(RangeCanvas).X / RangeCanvas.ActualWidth, 0, 1);
        var left = selectionStart / duration; var right = selectionEnd / duration;
        var tolerance = Math.Max(.015, 15 / RangeCanvas.ActualWidth);
        if (Math.Abs(ratio - left) < tolerance) draggedHandle = "start";
        else if (Math.Abs(ratio - right) < tolerance) draggedHandle = "end";
        else if (ratio > left && ratio < right) draggedHandle = ratio - left < right - ratio ? "start" : "end";
        else { Seek(ratio * duration); return; }
        draggingTimeline = true; RangeCanvas.CaptureMouse(); SetHandleTime(ratio * duration);
    }
    private void TimelineMove(object sender, MouseEventArgs e)
    {
        if (!draggingTimeline || duration <= 0) return;
        SetHandleTime(Math.Clamp(e.GetPosition(RangeCanvas).X / Math.Max(1, RangeCanvas.ActualWidth) * duration, 0, duration));
    }
    private void TimelineUp(object sender, MouseButtonEventArgs e)
    {
        if (!draggingTimeline) return; draggingTimeline = false; RangeCanvas.ReleaseMouseCapture(); draggedHandle = "";
    }
    private void SetHandleTime(double value)
    {
        const double gap = .02;
        if (draggedHandle == "start") selectionStart = Math.Clamp(value, 0, Math.Max(0, selectionEnd - gap));
        else if (draggedHandle == "end") selectionEnd = Math.Clamp(value, Math.Min(duration, selectionStart + gap), duration);
        UpdateRangeFields();
    }
    private void RangeTextChanged(object sender, TextChangedEventArgs e)
    {
        if (updatingRangeText || duration <= 0 || !IsInitialized) return;
        if (!TryParseTime(StartBox.Text, out var start) || !TryParseTime(EndBox.Text, out var end) || start < 0 || end <= start || end > duration + .02) return;
        selectionStart = start; selectionEnd = Math.Min(duration, end); UpdateSelectionText(); SyncTimeline();
    }
    private void UpdateRangeFields()
    {
        if (StartBox is null) return;
        updatingRangeText = true; StartBox.Text = FormatTime(selectionStart); EndBox.Text = FormatTime(selectionEnd); updatingRangeText = false;
        UpdateSelectionText(); SyncTimeline();
    }
    private void UpdateSelectionText()
    {
        var selected = FormatTime(Math.Max(0, selectionEnd - selectionStart));
        DurationText.Text = selected; SelectionDuration.Text = "Selected " + selected;
        TimelineStart.Text = FormatTime(selectionStart); TimelineEnd.Text = FormatTime(selectionEnd);
    }
    private void TimelineSizeChanged(object sender, SizeChangedEventArgs e) => SyncTimeline();
    private void SyncTimeline()
    {
        if (RangeCanvas is null || duration <= 0 || RangeCanvas.ActualWidth <= 0) return;
        var width = RangeCanvas.ActualWidth; var height = RangeCanvas.ActualHeight;
        var left = Math.Clamp(selectionStart / duration * width, 0, width); var right = Math.Clamp(selectionEnd / duration * width, left, width);
        Canvas.SetLeft(LeftMask, 0); Canvas.SetTop(LeftMask, 0); LeftMask.Width = left; LeftMask.Height = height;
        Canvas.SetLeft(RightMask, right); Canvas.SetTop(RightMask, 0); RightMask.Width = width - right; RightMask.Height = height;
        Canvas.SetLeft(SelectionOutline, left); Canvas.SetTop(SelectionOutline, 0); SelectionOutline.Width = Math.Max(0, right - left); SelectionOutline.Height = height;
        Canvas.SetLeft(StartHandle, Math.Clamp(left - 5, 0, Math.Max(0, width - 11))); Canvas.SetTop(StartHandle, 2); StartHandle.Height = Math.Max(0, height - 4);
        Canvas.SetLeft(EndHandle, Math.Clamp(right - 5, 0, Math.Max(0, width - 11))); Canvas.SetTop(EndHandle, 2); EndHandle.Height = Math.Max(0, height - 4);
        Playhead.Height = height; Canvas.SetLeft(PlayheadDot, Math.Clamp((PositionSlider?.Value ?? 0) / duration * width - 4, 0, Math.Max(0, width - 8))); UpdatePlayhead(PositionSlider?.Value ?? 0);
    }
    private void UpdatePlayhead(double seconds)
    {
        if (RangeCanvas is null || duration <= 0 || RangeCanvas.ActualWidth <= 0) return;
        var x = Math.Clamp(seconds / duration * RangeCanvas.ActualWidth, 0, Math.Max(0, RangeCanvas.ActualWidth - 2));
        Canvas.SetLeft(Playhead, x); Canvas.SetTop(Playhead, 0); Canvas.SetLeft(PlayheadDot, Math.Clamp(x - 3.5, 0, Math.Max(0, RangeCanvas.ActualWidth - 9))); Canvas.SetTop(PlayheadDot, 0);
    }

    private void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeBox is not null && ModeBox.SelectedIndex >= 0) { lastQualityIndex = ModeBox.SelectedIndex; SaveSettings(); }
        SetQualityNote();
    }
    private void SetQualityNote()
    {
        if (QualityNoteText is null || ModeBox is null || ModeBox.SelectedIndex < 0) return;
        var precise = ModeBox.SelectedIndex == 1;
        QualityNoteText.Text = currentTool == "Trim Audio"
            ? precise ? "Precise mode saves uncompressed PCM WAV and follows the selected sample range." : "Fast mode copies the original audio stream. Some containers only cut at packet boundaries."
            : precise ? "Precise mode re-encodes to H.264 / AAC and follows the selected frame range." : "Fast mode copies the original streams. A cut may move to a nearby keyframe.";
    }

    private void CropTextChanged(object sender, TextChangedEventArgs e)
    {
        if (updatingCropText || CropXBox is null || CropYBox is null || CropWidthBox is null || CropHeightBox is null || CropPresetBox is null || !int.TryParse(CropXBox.Text, out var x) || !int.TryParse(CropYBox.Text, out var y) || !int.TryParse(CropWidthBox.Text, out var w) || !int.TryParse(CropHeightBox.Text, out var h)) return;
        if (CropPresetBox.SelectedIndex != 0) CropPresetBox.SelectedIndex = 0;
        crop = new CropRectangle(x, y, w, h); UpdateCropOverlay(); UpdateCropHint();
    }
    private void CropPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CropPresetBox is null || activeMedia is null || !IsInitialized) return;
        var ratio = CropPresetBox.SelectedIndex switch { 1 => 1d, 2 => 9d / 16, 3 => 16d / 9, 4 => 4d / 3, _ => 0d };
        if (ratio <= 0) return;
        var maxW = activeMedia.OrientedWidth & ~1; var maxH = activeMedia.OrientedHeight & ~1;
        var width = maxW; var height = (int)(width / ratio) & ~1;
        if (height > maxH) { height = maxH; width = (int)(height * ratio) & ~1; }
        crop = new CropRectangle(((maxW - width) / 2) & ~1, ((maxH - height) / 2) & ~1, width, height);
        SetCropFields(); UpdateCropOverlay(); UpdateCropHint();
    }
    private void InitializeCrop()
    {
        if (activeMedia is null || activeMedia.OrientedWidth < 2 || activeMedia.OrientedHeight < 2) return;
        var width = (int)(activeMedia.OrientedWidth * .84) & ~1; var height = (int)(activeMedia.OrientedHeight * .84) & ~1;
        crop = new CropRectangle(((activeMedia.OrientedWidth - width) / 2) & ~1, ((activeMedia.OrientedHeight - height) / 2) & ~1, width, height);
        if (CropPresetBox is not null && CropPresetBox.SelectedIndex != 0) CropPresetBox.SelectedIndex = 0;
        SetCropFields(); UpdateCropOverlay(); UpdateCropHint();
    }
    private void SetCropFields()
    {
        updatingCropText = true; CropXBox.Text = crop.X.ToString(CultureInfo.InvariantCulture); CropYBox.Text = crop.Y.ToString(CultureInfo.InvariantCulture); CropWidthBox.Text = crop.Width.ToString(CultureInfo.InvariantCulture); CropHeightBox.Text = crop.Height.ToString(CultureInfo.InvariantCulture); updatingCropText = false;
    }
    private void UpdateCropHint()
    {
        if (activeMedia is null) return;
        CropDimensionHint.Text = $"Output {crop.Width} × {crop.Height} px · source {activeMedia.OrientedWidth} × {activeMedia.OrientedHeight} px";
    }
    private Rect GetVideoContentRect()
    {
        if (activeMedia is null || CropCanvas.ActualWidth <= 0 || CropCanvas.ActualHeight <= 0) return Rect.Empty;
        var sourceAspect = (double)activeMedia.OrientedWidth / Math.Max(1, activeMedia.OrientedHeight);
        var canvasAspect = CropCanvas.ActualWidth / Math.Max(1, CropCanvas.ActualHeight);
        if (canvasAspect > sourceAspect)
        {
            var width = CropCanvas.ActualHeight * sourceAspect;
            return new Rect((CropCanvas.ActualWidth - width) / 2, 0, width, CropCanvas.ActualHeight);
        }
        var height = CropCanvas.ActualWidth / sourceAspect;
        return new Rect(0, (CropCanvas.ActualHeight - height) / 2, CropCanvas.ActualWidth, height);
    }
    private void UpdateCropOverlay()
    {
        if (CropSelection is null) return;
        var content = GetVideoContentRect(); if (content.IsEmpty || activeMedia is null || activeMedia.OrientedWidth <= 0 || activeMedia.OrientedHeight <= 0) return;
        var sx = content.Width / activeMedia.OrientedWidth; var sy = content.Height / activeMedia.OrientedHeight;
        Canvas.SetLeft(CropSelection, content.X + crop.X * sx); Canvas.SetTop(CropSelection, content.Y + crop.Y * sy);
        CropSelection.Width = Math.Max(0, crop.Width * sx); CropSelection.Height = Math.Max(0, crop.Height * sy);
    }
    private void CropCanvasSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCropOverlay();
    private void CropMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (activeMedia is null) return;
        var point = ToCropPixels(e.GetPosition(CropCanvas)); if (point is null) return;
        cropDragStart = point.Value; cropDragOrigin = crop;
        var content = GetVideoContentRect(); var sourceScale = content.Width / activeMedia.OrientedWidth;
        var px = e.GetPosition(CropCanvas).X; var py = e.GetPosition(CropCanvas).Y;
        var left = content.X + crop.X * sourceScale; var right = left + crop.Width * sourceScale;
        var top = content.Y + crop.Y * sourceScale; var bottom = top + crop.Height * sourceScale;
        var edge = Math.Max(10, sourceScale * 18);
        var nearL = Math.Abs(px - left) <= edge; var nearR = Math.Abs(px - right) <= edge;
        var nearT = Math.Abs(py - top) <= edge; var nearB = Math.Abs(py - bottom) <= edge;
        if (nearL && nearT) cropDragMode = "nw"; else if (nearR && nearT) cropDragMode = "ne"; else if (nearL && nearB) cropDragMode = "sw"; else if (nearR && nearB) cropDragMode = "se";
        else if (px >= left && px <= right && py >= top && py <= bottom) cropDragMode = "move";
        else { cropDragMode = "new"; crop = new CropRectangle((int)point.Value.X, (int)point.Value.Y, 2, 2); SetCropFields(); }
        draggingCrop = true; CropCanvas.CaptureMouse(); e.Handled = true;
    }
    private void CropMouseMove(object sender, MouseEventArgs e)
    {
        if (!draggingCrop || activeMedia is null) return;
        var point = ToCropPixels(e.GetPosition(CropCanvas)); if (point is null) return;
        var width = activeMedia.OrientedWidth; var height = activeMedia.OrientedHeight;
        var dx = (int)(point.Value.X - cropDragStart.X); var dy = (int)(point.Value.Y - cropDragStart.Y);
        var x1 = cropDragOrigin.X; var y1 = cropDragOrigin.Y; var x2 = cropDragOrigin.X + cropDragOrigin.Width; var y2 = cropDragOrigin.Y + cropDragOrigin.Height;
        if (cropDragMode == "new") { x1 = (int)Math.Min(cropDragStart.X, point.Value.X); y1 = (int)Math.Min(cropDragStart.Y, point.Value.Y); x2 = (int)Math.Max(cropDragStart.X, point.Value.X); y2 = (int)Math.Max(cropDragStart.Y, point.Value.Y); }
        else if (cropDragMode == "move") { x1 = Math.Clamp(cropDragOrigin.X + dx, 0, width - cropDragOrigin.Width); y1 = Math.Clamp(cropDragOrigin.Y + dy, 0, height - cropDragOrigin.Height); x2 = x1 + cropDragOrigin.Width; y2 = y1 + cropDragOrigin.Height; }
        else
        {
            if (cropDragMode.Contains('w')) x1 = Math.Clamp(cropDragOrigin.X + dx, 0, x2 - 2);
            if (cropDragMode.Contains('e')) x2 = Math.Clamp(cropDragOrigin.X + cropDragOrigin.Width + dx, x1 + 2, width);
            if (cropDragMode.Contains('n')) y1 = Math.Clamp(cropDragOrigin.Y + dy, 0, y2 - 2);
            if (cropDragMode.Contains('s')) y2 = Math.Clamp(cropDragOrigin.Y + cropDragOrigin.Height + dy, y1 + 2, height);
            if (CropPresetBox.SelectedIndex != 0)
            {
                var ratio = CropPresetBox.SelectedIndex switch { 1 => 1d, 2 => 9d / 16, 3 => 16d / 9, 4 => 4d / 3, _ => 0 };
                if (ratio > 0) { var h = (int)((x2 - x1) / ratio); if (cropDragMode.Contains('n')) y1 = y2 - h; else y2 = y1 + h; }
            }
        }
        var cx = Math.Clamp(x1 & ~1, 0, width - 2); var cy = Math.Clamp(y1 & ~1, 0, height - 2);
        var cw = Math.Max(2, (Math.Clamp(x2, cx + 2, width) - cx) & ~1);
        var ch = Math.Max(2, (Math.Clamp(y2, cy + 2, height) - cy) & ~1);
        crop = new CropRectangle(cx, cy, Math.Min(cw, width - cx), Math.Min(ch, height - cy));
        SetCropFields(); UpdateCropOverlay(); UpdateCropHint();
    }
    private void CropMouseUp(object sender, MouseButtonEventArgs e) { if (!draggingCrop) return; draggingCrop = false; CropCanvas.ReleaseMouseCapture(); cropDragMode = ""; }
    private Point? ToCropPixels(Point point)
    {
        if (activeMedia is null) return null;
        var content = GetVideoContentRect(); if (content.IsEmpty || !content.Contains(point)) return null;
        var x = (point.X - content.X) / content.Width * activeMedia.OrientedWidth;
        var y = (point.Y - content.Y) / content.Height * activeMedia.OrientedHeight;
        return new Point(Math.Clamp(x, 0, activeMedia.OrientedWidth), Math.Clamp(y, 0, activeMedia.OrientedHeight));
    }

    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        if (jobRunning) return;
        var inputs = IsQueueTool ? Queue.Select(x => x.Media).ToArray() : activeMedia is null ? [] : [activeMedia];
        if (inputs.Length == 0) { SetError(IsQueueTool ? "Add one or more files to the queue first." : "Choose a media file first."); return; }
        var selection = new MediaSelection(selectionStart, selectionEnd);
        if (currentTool is "Trim Audio" or "Trim Video")
        {
            if (!TryParseTime(StartBox.Text, out var typedStart) || !TryParseTime(EndBox.Text, out var typedEnd) || typedStart < 0 || typedEnd <= typedStart || typedEnd > duration)
            { SetError("Check the visible start and end times. The end must be after the start and within the media duration."); return; }
            selection = new MediaSelection(typedStart, typedEnd);
            selectionStart = typedStart; selectionEnd = typedEnd; UpdateSelectionText(); SyncTimeline();
        }
        var operation = currentTool switch { "Extract Audio" => MediaOperation.ExtractAudio, "Trim Audio" => MediaOperation.TrimAudio, "Trim Video" => MediaOperation.TrimVideo, "Crop Frame" => MediaOperation.CropVideo, "Join Videos" => MediaOperation.JoinVideos, _ => (MediaOperation?)null };
        if (operation is null) { SetError("This tool is not available yet."); return; }
        var trackSelections = Queue.ToImmutableDictionary(x => x.Media.Path, x => x.SelectedTrack?.Ordinal, StringComparer.OrdinalIgnoreCase);
        var name = OutputNameBox.Text;
        var spec = new MediaJobSpec(operation.Value, inputs.ToImmutableArray(), OutputFolderBox.Text.Trim(), name,
            (VideoContainer)Math.Clamp(ContainerBox.SelectedIndex, 0, 2), ModeBox.SelectedIndex == 1 ? QualityMode.Precise : QualityMode.Fast,
            (JoinMode)Math.Clamp(JoinModeBox.SelectedIndex, 0, 2), (AudioOutputFormat)Math.Clamp(AudioFormatBox.SelectedIndex, 0, 5),
            operation is MediaOperation.TrimAudio or MediaOperation.TrimVideo ? selection : null,
            operation == MediaOperation.CropVideo ? crop : null, trackSelections);
        var validation = MediaProcessor.Validate(spec);
        if (validation.Length > 0) { SetError(validation); return; }
        if (operation == MediaOperation.JoinVideos)
        {
            var compatibility = MediaProcessor.CheckJoinCompatibility(spec.Inputs);
            if (JoinModeBox.SelectedIndex == 1 && !compatibility.Compatible) { SetError("Fast Join requires matching streams. Choose Convert & Join for these clips."); return; }
            if (JoinModeBox.SelectedIndex == 0 && !compatibility.Compatible) JoinCompatibilityText.Text = compatibility.Reason + " Automatic mode will convert these clips.";
        }

        jobCancellation = new CancellationTokenSource(); jobRunning = true;
        ExportButton.IsEnabled = false; CancelButton.Visibility = Visibility.Visible; ProgressBar.Visibility = Visibility.Visible; ProgressBar.Value = 0; ProgressText.Text = "";
        SetStatus("Preparing export…");
        try
        {
            var result = await processor.ProcessAsync(spec, value => Dispatcher.BeginInvoke(() => UpdateProgress(value)), jobCancellation.Token);
            ProgressBar.Value = 100; ProgressText.Text = "100%";
            var successes = result.Outputs.Count(x => x.Succeeded); var failures = result.FailedCount;
            if (successes > 0) { SetReady(failures == 0 ? $"Completed · {successes} file(s) saved" : $"Completed with {failures} failure(s)"); ShowResult(result); }
            else SetError(result.Outputs.FirstOrDefault()?.Message ?? "No output files were created.");
        }
        catch (MediaJobCanceledException ex)
        {
            SetStatus("Cancelled · unfinished files removed");
            if (ex.CompletedOutputs.Any(x => x.Succeeded)) ShowResult(new MediaJobResult(ex.CompletedOutputs));
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled · unfinished files removed");
        }
        catch (Exception ex) { SetError(ex.Message); }
        finally
        {
            jobRunning = false; jobCancellation?.Dispose(); jobCancellation = null; ExportButton.IsEnabled = true; CancelButton.Visibility = Visibility.Collapsed; CancelButton.IsEnabled = true;
            await Task.Delay(650); ProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateProgress(MediaProgress value)
    {
        ProgressBar.Value = value.Percent;
        var remaining = value.Remaining is { } r ? $" · about {r.ToString(@"mm\:ss")} left" : "";
        var item = value.ItemCount > 1 ? $" · file {value.ItemIndex}/{value.ItemCount}" : "";
        ProgressText.Text = $"{value.Percent:0}% · {value.Elapsed.ToString(@"mm\:ss")} elapsed{remaining}{item}";
        StatusText.Text = value.Activity;
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        if (!jobRunning) return;
        CancelButton.IsEnabled = false; jobCancellation?.Cancel(); StatusText.Text = "Stopping FFmpeg and cleaning up…";
    }

    private void ShowResult(MediaJobResult result)
    {
        var window = new Window { Title = "Media job complete", Owner = this, Width = 560, Height = 360, MinWidth = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)Application.Current.Resources["PageBrush"] };
        var root = new DockPanel { Margin = new Thickness(22) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var firstOutput = result.Outputs.FirstOrDefault(x => x.Succeeded)?.Output;
        var play = new Button { Content = "▶ Play", IsEnabled = firstOutput is not null, Margin = new Thickness(0, 0, 6, 0) }; play.Click += (_, _) => { if (firstOutput is not null) OpenExternal(firstOutput); };
        var folder = new Button { Content = "Open folder", IsEnabled = firstOutput is not null, Margin = new Thickness(0, 0, 6, 0) }; folder.Click += (_, _) => { if (firstOutput is not null) OpenFolder(firstOutput); };
        var again = new Button { Content = "Process another", Background = (Brush)Application.Current.Resources["AccentBrush"], Foreground = Brushes.Black }; again.Click += (_, _) => { window.Close(); if (currentTool is "Extract Audio" or "Join Videos") BrowseClick(this, new RoutedEventArgs()); else BrowseClick(this, new RoutedEventArgs()); };
        actions.Children.Add(play); actions.Children.Add(folder); actions.Children.Add(again); DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var body = new StackPanel(); body.Children.Add(new TextBlock { Text = result.FailedCount == 0 ? "Your files are ready" : "Job finished with some issues", FontSize = 21, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = $"{result.Outputs.Count(x => x.Succeeded)} completed · {result.FailedCount} failed", Foreground = (Brush)Application.Current.Resources["MutedBrush"], Margin = new Thickness(0, 4, 0, 12) });
        var list = new ListBox { ItemsSource = result.Outputs, MaxHeight = 215 };
        list.ItemTemplate = new DataTemplate(typeof(MediaJobOutput));
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        var pathText = new FrameworkElementFactory(typeof(TextBlock)); pathText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Path = new PropertyPath(nameof(MediaJobOutput.Output)), TargetNullValue = "No output created" }); pathText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); pathText.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); panel.AppendChild(pathText);
        var detail = new FrameworkElementFactory(typeof(TextBlock)); detail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MediaJobOutput.Message))); detail.SetValue(TextBlock.ForegroundProperty, Application.Current.Resources["MutedBrush"]); detail.SetValue(TextBlock.FontSizeProperty, 10d); detail.SetValue(TextBlock.MarginProperty, new Thickness(0, 3, 0, 0)); panel.AppendChild(detail);
        list.ItemTemplate.VisualTree = panel; body.Children.Add(list); root.Children.Add(body); window.Content = root; window.ShowDialog();
    }

    private void OpenPlayerClick(object sender, RoutedEventArgs e)
    {
        var path = IsQueueTool ? (QueueList.SelectedItem as QueueEntry)?.Media.Path ?? Queue.FirstOrDefault()?.Media.Path : activeMedia?.Path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { SetError("Choose a media file first."); return; }
        OpenExternal(path);
    }
    private static void OpenExternal(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { } }
    private static void OpenFolder(string path) { try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { } }
    private void OutputFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where finished files are saved", Multiselect = false };
        if (Directory.Exists(OutputFolderBox.Text)) dialog.InitialDirectory = OutputFolderBox.Text;
        if (dialog.ShowDialog(this) == true) { OutputFolderBox.Text = dialog.FolderName; savedOutputFolder = dialog.FolderName; SaveSettings(); }
    }

    private void QueueSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsQueueTool && QueueList.SelectedItem is QueueEntry entry && activeMedia?.Path != entry.Media.Path) _ = LoadPreviewAsync(entry.Media);
    }
    private void RemoveQueueClick(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is not QueueEntry entry) return;
        var index = Queue.IndexOf(entry); Queue.Remove(entry);
        CancelQueueThumbnail(entry);
        if (Queue.Count > 0) QueueList.SelectedIndex = Math.Min(index, Queue.Count - 1);
        else { activeMedia = null; Player.Stop(); Player.Source = null; EmptyState.Visibility = Visibility.Visible; MediaFileName.Text = "No media selected"; MediaMetadata.Text = "Add a file to see details"; thumbnails.Clear(); SetDuration(0); }
        UpdateQueueSummary(); UpdateJoinCompatibility();
    }
    private void MoveUpClick(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDownClick(object sender, RoutedEventArgs e) => MoveSelected(1);
    private void MoveSelected(int delta)
    {
        var index = QueueList.SelectedIndex; var target = index + delta;
        if (index < 0 || target < 0 || target >= Queue.Count) return;
        Queue.Move(index, target); QueueList.SelectedIndex = target; UpdateQueueSummary(); UpdateJoinCompatibility();
    }
    private void QueueMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(QueueList); if (Math.Abs(now.X - queueDragStart.X) + Math.Abs(now.Y - queueDragStart.Y) < 8) return;
        if (QueueList.SelectedItem is QueueEntry selected) DragDrop.DoDragDrop(QueueList, selected, DragDropEffects.Move);
    }
    private void QueueDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(QueueEntry)) || e.Data.GetData(typeof(QueueEntry)) is not QueueEntry entry) return;
        var target = ItemsControl.ContainerFromElement(QueueList, e.OriginalSource as DependencyObject) as ListBoxItem;
        var to = target is null ? Queue.Count - 1 : QueueList.ItemContainerGenerator.IndexFromContainer(target);
        var from = Queue.IndexOf(entry); if (from < 0 || to < 0) return;
        Queue.Move(from, to); QueueList.SelectedItem = entry; UpdateQueueSummary(); UpdateJoinCompatibility();
    }
    private void QueueList_PreviewMouseDown(object sender, MouseButtonEventArgs e) => queueDragStart = e.GetPosition(QueueList);
    private void UpdateQueueSummary()
    {
        QueueCountText.Text = $"{Queue.Count} file{(Queue.Count == 1 ? "" : "s")}";
        var total = Queue.Sum(x => x.Media.Duration);
        QueueSummaryText.Text = currentTool == "Join Videos" && Queue.Count > 0 ? $"Total length · {FormatTime(total)}" : "";
    }
    private void UpdateJoinCompatibility()
    {
        if (JoinCompatibilityText is null) return;
        if (Queue.Count < 2)
        {
            JoinCompatibilityText.Text = "Add at least two clips. Their stream compatibility will appear here.";
            JoinConversionPanel.Visibility = JoinModeBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            var result = MediaProcessor.CheckJoinCompatibility(Queue.Select(x => x.Media).ToImmutableArray());
            JoinCompatibilityText.Text = result.Compatible ? "✓ " + result.Reason : "Convert required · " + result.Reason;
            JoinConversionPanel.Visibility = JoinModeBox.SelectedIndex == 2 || JoinModeBox.SelectedIndex == 0 && !result.Compatible ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    private void JoinModeChanged(object sender, SelectionChangedEventArgs e) => UpdateJoinCompatibility();

    private void WindowDragOver(object sender, DragEventArgs e) => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    private async void WindowDrop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await AddPathsAsync(files); }
    private async void InputDrop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await AddPathsAsync(files); }

    private void ThemeClick(object sender, RoutedEventArgs e)
    {
        isLightTheme = !isLightTheme;
        ApplyTheme(); SaveSettings();
    }
    private void ApplyTheme()
    {
        var colors = isLightTheme
            ? (page: "#F2F5F8", panel: "#FFFFFF", border: "#D8E0E8", text: "#18212B", muted: "#667585", accent: "#087E78", sidebar: "#E8EDF3", inspector: "#F8FAFC", canvas: "#E3E8ED", input: "#F5F7FA", button: "#EAF0F5")
            : (page: "#11161D", panel: "#19212B", border: "#2B3744", text: "#EFF4F7", muted: "#A0ADBA", accent: "#28C8B9", sidebar: "#131A23", inspector: "#171E27", canvas: "#0C1117", input: "#151D27", button: "#222E3B");
        SetBrush("PageBrush", colors.page); SetBrush("PanelBrush", colors.panel); SetBrush("PanelBorderBrush", colors.border); SetBrush("TextBrush", colors.text); SetBrush("MutedBrush", colors.muted); SetBrush("AccentBrush", colors.accent); SetBrush("SidebarBrush", colors.sidebar); SetBrush("InspectorBrush", colors.inspector); SetBrush("CanvasBrush", colors.canvas); SetBrush("InputBrush", colors.input); SetBrush("ButtonBrush", colors.button); Background = (Brush)Application.Current.Resources["PageBrush"];
    }
    private static void SetBrush(string key, string color) => Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(settingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (doc.RootElement.TryGetProperty("outputFolder", out var folder)) savedOutputFolder = folder.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("lightTheme", out var light)) { isLightTheme = light.GetBoolean(); if (isLightTheme) ApplyTheme(); }
            if (doc.RootElement.TryGetProperty("quality", out var q) && q.GetInt32() is 0 or 1) { lastQualityIndex = q.GetInt32(); if (ModeBox.Items.Count > 0) ModeBox.SelectedIndex = lastQualityIndex; }
        }
        catch { }
    }
    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(new { outputFolder = string.IsNullOrWhiteSpace(OutputFolderBox.Text) ? savedOutputFolder : OutputFolderBox.Text, lightTheme = isLightTheme, quality = Math.Clamp(lastQualityIndex, 0, 1) }));
        }
        catch { }
    }
    private void OnClosed(object? sender, EventArgs e)
    {
        playbackTimer.Stop(); previewCancellation?.Cancel(); jobCancellation?.Cancel();
        foreach (var source in queueThumbnailJobs.Values) source.Cancel();
        SaveSettings();
    }

    private void SetReady(string message) { StatusText.Text = message; StatusDot.Fill = (Brush)Application.Current.Resources["AccentBrush"]; }
    private void SetStatus(string message) { StatusText.Text = message; StatusDot.Fill = (Brush)Application.Current.Resources["AccentBrush"]; }
    private void SetError(string message) { StatusText.Text = message; StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x8A, 0x79)); }
    private static string DefaultOutputName(MediaInfo media, string tool) => Path.GetFileNameWithoutExtension(media.Path) + (tool switch { "Extract Audio" => "_audio", "Trim Audio" => "_trimmed_audio", "Trim Video" => "_trimmed", "Crop Frame" => "_cropped", "Join Videos" => "_joined", _ => "_output" });
    private static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span)) { seconds = span.TotalSeconds; return true; }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
    }
    private static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }
    private static string FormatBytes(long bytes) => bytes switch { >= 1_000_000_000 => $"{bytes / 1_000_000_000d:0.0} GB", >= 1_000_000 => $"{bytes / 1_000_000d:0.0} MB", _ => $"{bytes / 1000d:0} KB" };
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
}

public sealed record TrackChoice(int? Ordinal, string Label);

public sealed class QueueEntry : INotifyPropertyChanged
{
    private BitmapImage? thumbnail;
    private TrackChoice? selectedTrack;
    public MediaInfo Media { get; }
    public ObservableCollection<TrackChoice> TrackOptions { get; } = [];
    public string FileName => Media.FileName;
    public string Details => Media.HasVideo ? Media.Summary : $"{string.Join(" · ", Media.AudioTracks.Select(t => t.Codec.ToUpperInvariant()).Distinct())} · {TimeSpan.FromSeconds(Media.Duration).ToString(@"hh\:mm\:ss")}";
    public Visibility TrackSelectorVisibility { get; }
    public Visibility AudioIconVisibility => Media.HasVideo ? Visibility.Collapsed : Visibility.Visible;
    public BitmapImage? Thumbnail { get => thumbnail; set { thumbnail = value; Changed(); } }
    public TrackChoice? SelectedTrack { get => selectedTrack; set { selectedTrack = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public QueueEntry(MediaInfo media, bool showTracks)
    {
        Media = media;
        TrackSelectorVisibility = showTracks ? Visibility.Visible : Visibility.Collapsed;
        if (showTracks)
        {
            TrackOptions.Add(new TrackChoice(null, "All tracks"));
            foreach (var track in media.AudioTracks)
            {
                var title = string.IsNullOrWhiteSpace(track.Title) ? "" : $" · {track.Title}";
                var language = string.IsNullOrWhiteSpace(track.Language) ? "" : $" · {track.Language}";
                TrackOptions.Add(new TrackChoice(track.Ordinal, $"Track {track.Ordinal + 1} · {track.Codec.ToUpperInvariant()}{language}{title}"));
            }
            SelectedTrack = TrackOptions.FirstOrDefault();
        }
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
