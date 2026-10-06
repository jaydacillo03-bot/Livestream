using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using Microsoft.Win32;

namespace LivestreamStudio;

public partial class MainWindow : System.Windows.Window
{
    private static readonly string ProjectFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MosaicStudio",
        "studio.json");

    private readonly ObservableCollection<Destination> _destinations =
    [
        new("youtube", "YouTube", "Y", "#F04F51", "rtmps://a.rtmps.youtube.com/live2"),
        new("twitch", "Twitch", "T", "#9964F5", "rtmps://live.twitch.tv:443/app")
    ];

    private bool _isInitializing = true;
    private bool _isViewerMode;
    private bool _isPlaying;
    private bool _syncingFeedLists;
    private bool _isRefreshingCameras;
    private bool _isClosing;
    private bool _broadcastIsStarting;
    private bool _isBroadcastStopRequested;
    private FeedScene? _selectedFeed;
    private CancellationTokenSource? _cameraPreviewCancellation;
    private Task? _cameraPreviewTask;
    private Process? _broadcastProcess;
    private List<Destination> _broadcastDestinations = [];
    private string? _lastBroadcastError;

    public ObservableCollection<FeedScene> Feeds { get; } = [];
    public ObservableCollection<CameraDevice> Cameras { get; } = [];
    public ObservableCollection<string> AudioDevices { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        DestinationList.ItemsSource = _destinations;

        Feeds.Add(new FeedScene("main-camera", "Main camera", "Camera input · not connected", "CAMERA", "A", "#8165EB"));
        Feeds.Add(new FeedScene("screen-share", "Screen share", "Display capture · not connected", "DISPLAY", "S", "#328D9E"));
        Feeds.Add(new FeedScene("break-screen", "Break screen", "Graphic scene · not connected", "SCENE", "B", "#B47A45"));

        try
        {
            LoadStudio();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show(
                this,
                $"Your saved studio could not be loaded. The default feeds have been opened instead.\n\n{exception.Message}",
                "Couldn't load saved studio",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            StatusText.Text = "Saved studio could not be loaded. Default feeds are open.";
            ViewerStatusText.Text = StatusText.Text;
        }

        StudioFeedList.SelectedItem = _selectedFeed;
        ViewerFeedList.SelectedItem = _selectedFeed;
        _isInitializing = false;
        UpdateFeedCount();
        UpdateDestinationCount();
        SaveStudio(showConfirmation: false);
        UpdateWorkspaceAppearance();
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshCamerasAsync();
        await SyncPreviewAsync(_selectedFeed);
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        e.Cancel = true;
        _isClosing = true;
        await StopBroadcastAsync();
        await StopCameraPreviewAsync();
        Close();
    }

    private void LoadStudio()
    {
        if (!File.Exists(ProjectFile))
        {
            _selectedFeed = Feeds[0];
            return;
        }

        var json = File.ReadAllText(ProjectFile);
        var project = JsonSerializer.Deserialize<StudioProject>(json)
            ?? throw new JsonException("The saved studio file is empty or invalid.");

        if (project.Feeds is { Count: > 0 })
        {
            Feeds.Clear();
            foreach (var feed in project.Feeds)
            {
                Feeds.Add(feed);
            }
        }

        if (project.Destinations is not null)
        {
            _destinations.Clear();
            foreach (var destination in project.Destinations)
            {
                _destinations.Add(destination);
            }
        }
        else if (project.SelectedDestinationNames is not null)
        {
            foreach (var destination in _destinations)
            {
                destination.IsSelected = destination.IsConfigured
                    && project.SelectedDestinationNames.Contains(destination.Name, StringComparer.OrdinalIgnoreCase);
            }
        }

        _selectedFeed = Feeds.FirstOrDefault(feed => feed.Id == project.SelectedFeedId) ?? Feeds[0];
    }

    private void SaveStudio(bool showConfirmation = true)
    {
        var project = new StudioProject
        {
            Feeds = Feeds.ToList(),
            SelectedFeedId = _selectedFeed?.Id,
            SelectedDestinationNames = _destinations
                .Where(destination => destination.IsSelected)
                .Select(destination => destination.Name)
                .ToList(),
            Destinations = _destinations.ToList()
        };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProjectFile)!);
            var json = JsonSerializer.Serialize(project, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ProjectFile, json);

            var message = $"Studio saved on this PC. Video feeds reference their original files.";
            StatusText.Text = message;
            ViewerStatusText.Text = message;

            if (showConfirmation)
            {
                MessageBox.Show(this, $"Your studio was saved to:\n{ProjectFile}", "Studio saved",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var message = $"Couldn't save studio: {exception.Message}";
            StatusText.Text = message;
            ViewerStatusText.Text = message;
            MessageBox.Show(this, message, "Couldn't save studio",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSaveStudioClick(object sender, RoutedEventArgs e) => SaveStudio();

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SaveStudio();
            e.Handled = true;
        }
    }

    private void OnStudioModeClick(object sender, RoutedEventArgs e) => SetViewerMode(false);

    private void OnViewerModeClick(object sender, RoutedEventArgs e) => SetViewerMode(true);

    private void SetViewerMode(bool isViewerMode)
    {
        _isViewerMode = isViewerMode;
        StudioWorkspace.Visibility = isViewerMode ? Visibility.Collapsed : Visibility.Visible;
        ViewerWorkspace.Visibility = isViewerMode ? Visibility.Visible : Visibility.Collapsed;
        StudioSidebar.Visibility = isViewerMode ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(StudioWorkspace, isViewerMode ? 2 : 1);
        Grid.SetColumnSpan(ViewerWorkspace, isViewerMode ? 2 : 1);
        Grid.SetColumnSpan(StudioSidebar, 1);
        UpdateWorkspaceAppearance();

        if (_selectedFeed?.CameraDevicePath is null && _isPlaying)
        {
            PausePlayer(StudioMedia);
            PausePlayer(ViewerMedia);
            GetActiveMedia().Play();
        }
    }

    private void UpdateWorkspaceAppearance()
    {
        StudioModeButton.Background = new SolidColorBrush(
            _isViewerMode ? Colors.Transparent : Color.FromRgb(42, 34, 64));
        ViewerModeButton.Background = new SolidColorBrush(
            _isViewerMode ? Color.FromRgb(42, 34, 64) : Colors.Transparent);
        StudioModeButton.Foreground = new SolidColorBrush(
            _isViewerMode ? Color.FromRgb(214, 216, 225) : Color.FromRgb(199, 184, 255));
        ViewerModeButton.Foreground = new SolidColorBrush(
            _isViewerMode ? Color.FromRgb(199, 184, 255) : Color.FromRgb(214, 216, 225));
    }

    private void OnImportVideoClick(object sender, RoutedEventArgs e)
    {
        if (_broadcastIsStarting || _broadcastProcess is not null)
        {
            SetStudioStatus("Stop the broadcast before changing video feeds.");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Choose a video feed",
            Filter = "Video files|*.mp4;*.m4v;*.wmv;*.avi;*.mov|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var file = new FileInfo(dialog.FileName);
        var feed = new FeedScene(
            Guid.NewGuid().ToString("N"),
            Path.GetFileNameWithoutExtension(file.Name),
            $"Local video · {file.Extension.TrimStart('.').ToUpperInvariant()}",
            "VIDEO FEED",
            "▶",
            GetFeedAccent(Feeds.Count),
            file.FullName);

        Feeds.Add(feed);
        UpdateFeedCount();
        StudioFeedList.SelectedItem = feed;
        ViewerFeedList.SelectedItem = feed;
        StatusText.Text = "Video feed added. It plays locally and is not broadcast.";
        ViewerStatusText.Text = "Video feed added. It plays locally and is not broadcast.";
        SaveStudio(showConfirmation: false);
    }

    private async void OnRefreshCamerasClick(object sender, RoutedEventArgs e)
    => await RefreshCamerasAsync();

    private async Task RefreshCamerasAsync()
    {
        if (_isRefreshingCameras)
        {
            return;
        }

        _isRefreshingCameras = true;
        var previousDevicePath = (CameraSourceList.SelectedItem as CameraDevice)?.DevicePath;
        var previousAudioDevice = AudioSourceList.SelectedItem as string;
        try
        {
            CameraSourceList.IsEnabled = false;
            RefreshCamerasButton.IsEnabled = false;
            var cameraDevicesTask = Task.Run(CameraDeviceEnumerator.GetDevices);
            var audioDevicesTask = Task.Run(CameraDeviceEnumerator.GetAudioDeviceNames);
            await Task.WhenAll(cameraDevicesTask, audioDevicesTask);

            Cameras.Clear();
            foreach (var device in cameraDevicesTask.Result)
            {
                Cameras.Add(device);
            }

            AudioDevices.Clear();
            foreach (var device in audioDevicesTask.Result)
            {
                AudioDevices.Add(device);
            }

            CameraSourceList.SelectedItem = Cameras.FirstOrDefault(
                device => device.DevicePath == previousDevicePath)
                ?? Cameras.FirstOrDefault();
            AudioSourceList.SelectedItem = AudioDevices.FirstOrDefault(device => device == previousAudioDevice)
                ?? AudioDevices.FirstOrDefault();
            IncludeMicrophoneCheckBox.IsEnabled = AudioDevices.Count > 0
                && _selectedFeed?.CameraDevicePath is not null;

            var message = Cameras.Count == 0
                ? "No cameras found. Connect a camera and select Refresh."
                : $"Found {Cameras.Count} camera{(Cameras.Count == 1 ? "" : "s")} and {AudioDevices.Count} microphone{(AudioDevices.Count == 1 ? "" : "s")}.";
            StatusText.Text = message;
            ViewerStatusText.Text = message;
        }
        catch (Exception exception)
        {
            var message = $"Couldn't find cameras: {exception.Message}";
            StatusText.Text = message;
            ViewerStatusText.Text = message;
            MessageBox.Show(this, message, "Camera discovery failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CameraSourceList.IsEnabled = !_broadcastIsStarting && _broadcastProcess is null;
            RefreshCamerasButton.IsEnabled = !_broadcastIsStarting && _broadcastProcess is null;
            _isRefreshingCameras = false;
        }
    }

    private void OnCameraSourceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isRefreshingCameras || CameraSourceList.SelectedItem is not CameraDevice camera)
        {
            return;
        }

        var existingFeed = Feeds.FirstOrDefault(feed => feed.CameraDevicePath == camera.DevicePath);
        if (existingFeed is not null)
        {
            StudioFeedList.SelectedItem = existingFeed;
        }
        else
        {
            var message = $"{camera.Name} selected. Choose Add camera to create a preview feed.";
            StatusText.Text = message;
            ViewerStatusText.Text = message;
        }
    }

    private async void OnAddCameraClick(object sender, RoutedEventArgs e)
    {
        if (_broadcastIsStarting || _broadcastProcess is not null)
        {
            return;
        }

        if (CameraSourceList.SelectedItem is not CameraDevice camera)
        {
            var message = "Connect a camera, choose Refresh, and select a camera before adding it.";
            StatusText.Text = message;
            ViewerStatusText.Text = message;
            return;
        }

        var feed = Feeds.FirstOrDefault(item => item.CameraDevicePath == camera.DevicePath);
        if (feed is null)
        {
            feed = new FeedScene(
                Guid.NewGuid().ToString("N"),
                camera.Name,
                "Camera input · local preview",
                "CAMERA",
                "CAM",
                GetFeedAccent(Feeds.Count),
                cameraDevicePath: camera.DevicePath);
            Feeds.Add(feed);
            UpdateFeedCount();
        }

        await SelectFeedAsync(feed);
        SaveStudio(showConfirmation: false);
    }

    private async void OnStudioFeedSelectionChanged(object sender, SelectionChangedEventArgs e)
    => await SelectFeedAsync(StudioFeedList.SelectedItem as FeedScene);

    private async void OnViewerFeedSelectionChanged(object sender, SelectionChangedEventArgs e)
    => await SelectFeedAsync(ViewerFeedList.SelectedItem as FeedScene);

    private async Task SelectFeedAsync(FeedScene? feed)
    {
        if (_isInitializing || _syncingFeedLists || feed is null)
        {
            return;
        }

        if (_broadcastIsStarting || _broadcastProcess is not null)
        {
            _syncingFeedLists = true;
            StudioFeedList.SelectedItem = _selectedFeed;
            ViewerFeedList.SelectedItem = _selectedFeed;
            _syncingFeedLists = false;
            SetStudioStatus("Stop the broadcast before switching feeds.");
            return;
        }

        _selectedFeed = feed;
        _syncingFeedLists = true;
        StudioFeedList.SelectedItem = feed;
        ViewerFeedList.SelectedItem = feed;
        _syncingFeedLists = false;

        var message = $"Selected {feed.Title}. This is a local preview, not a live broadcast.";
        StatusText.Text = message;
        ViewerStatusText.Text = message;
        await SyncPreviewAsync(feed);
    }

    private async Task SyncPreviewAsync(FeedScene? feed)
    {
        await StopCameraPreviewAsync();
        if (feed is null || !ReferenceEquals(feed, _selectedFeed))
        {
            return;
        }

        StudioSceneTitle.Text = feed.Title;
        StudioSceneStatus.Text = feed.Description;
        ViewerSceneTitle.Text = feed.Title;
        ViewerSceneStatus.Text = feed.Description;

        if (!string.IsNullOrWhiteSpace(feed.CameraDevicePath))
        {
            VolumeSlider.IsEnabled = false;
            VolumeSlider.ToolTip = "Camera audio is not captured in this version.";
            IncludeMicrophoneCheckBox.IsEnabled = AudioDevices.Count > 0 && !_broadcastIsStarting;
            IncludeMicrophoneCheckBox.ToolTip = "Include this microphone in the camera broadcast.";
            var camera = Cameras.FirstOrDefault(device => device.DevicePath == feed.CameraDevicePath);
            if (camera is null)
            {
                SetEmptyPreview("Camera not available", "Reconnect this camera and choose Refresh cameras.");
                var message = $"Couldn't find “{feed.Title}”. Reconnect the camera and choose Refresh.";
                StatusText.Text = message;
                ViewerStatusText.Text = message;
                return;
            }

            CameraSourceList.SelectedItem = camera;
            await StartCameraPreviewAsync(camera, feed);
            return;
        }

        var videoPath = feed.VideoPath;
        IncludeMicrophoneCheckBox.IsEnabled = false;
        IncludeMicrophoneCheckBox.ToolTip = "Audio from the selected video is streamed automatically.";
        VolumeSlider.IsEnabled = true;
        VolumeSlider.ToolTip = "Video preview volume";
        if (string.IsNullOrWhiteSpace(videoPath))
        {
            SetEmptyPreview("Feed not connected", feed.Description);
            return;
        }

        if (!File.Exists(videoPath))
        {
            SetEmptyPreview("Video file not found", "Reimport this feed's video to preview it.");
            var message = $"Couldn't find the local video for “{feed.Title}”. Reimport the file to restore this feed.";
            StatusText.Text = message;
            ViewerStatusText.Text = message;
            return;
        }

        StudioEmptyState.Visibility = Visibility.Collapsed;
        ViewerEmptyState.Visibility = Visibility.Collapsed;
        StudioMedia.Visibility = Visibility.Visible;
        ViewerMedia.Visibility = Visibility.Visible;
        _isPlaying = false;
        StudioPlaybackButton.Content = "▶";

        var source = new Uri(videoPath, UriKind.Absolute);
        PausePlayer(StudioMedia);
        PausePlayer(ViewerMedia);
        StudioCameraImage.Visibility = Visibility.Collapsed;
        ViewerCameraImage.Visibility = Visibility.Collapsed;
        SetMediaSource(StudioMedia, source);
        SetMediaSource(ViewerMedia, source);
        GetActiveMedia().Play();
        _isPlaying = true;
        StudioPlaybackButton.Content = "Ⅱ";
    }

    private async Task StartCameraPreviewAsync(CameraDevice camera, FeedScene feed)
    {
        await StopCameraPreviewAsync();
        if (!ReferenceEquals(feed, _selectedFeed))
        {
            return;
        }

        SetEmptyPreview("Connecting to camera", $"Opening {camera.Name} for a local preview.");
        var message = $"Connecting to “{camera.Name}” …";
        StatusText.Text = message;
        ViewerStatusText.Text = message;

        _cameraPreviewCancellation = new CancellationTokenSource();
        var cancellationToken = _cameraPreviewCancellation.Token;
        _cameraPreviewTask = Task.Run(
            () => CaptureCameraPreview(camera, feed, cancellationToken),
            CancellationToken.None);
        _isPlaying = true;
        StudioPlaybackButton.Content = "Ⅱ";
    }

    private void CaptureCameraPreview(CameraDevice camera, FeedScene feed, CancellationToken cancellationToken)
    {
        try
        {
            using var capture = new VideoCapture(camera.DirectShowIndex, VideoCaptureAPIs.DSHOW);
            if (!capture.IsOpened())
            {
                throw new InvalidOperationException(
                    $"Windows could not open “{camera.Name}”. Close other apps using the camera and check Windows Settings > Privacy & security > Camera.");
            }

            capture.Set(VideoCaptureProperties.FrameWidth, 1280);
            capture.Set(VideoCaptureProperties.FrameHeight, 720);

            using var frame = new Mat();
            using var convertedFrame = new Mat();
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!capture.Read(frame) || frame.Empty())
                {
                    throw new InvalidOperationException(
                        $"No video frames are coming from “{camera.Name}”. Check the camera connection and Windows camera permissions.");
                }

                Mat previewFrame;
                if (frame.Channels() == 3)
                {
                    previewFrame = frame;
                }
                else
                {
                    var colorConversion = frame.Channels() == 4
                        ? ColorConversionCodes.BGRA2BGR
                        : ColorConversionCodes.GRAY2BGR;
                    Cv2.CvtColor(frame, convertedFrame, colorConversion);
                    previewFrame = convertedFrame;
                }

                var stride = checked((int)previewFrame.Step());
                var bitmap = BitmapSource.Create(
                    previewFrame.Cols,
                    previewFrame.Rows,
                    96,
                    96,
                    PixelFormats.Bgr24,
                    null,
                    previewFrame.Data,
                    checked(stride * previewFrame.Rows),
                    stride);
                bitmap.Freeze();

                Dispatcher.InvokeAsync(() =>
                {
                    if (!cancellationToken.IsCancellationRequested
                        && ReferenceEquals(feed, _selectedFeed)
                        && !_isClosing)
                    {
                        StudioCameraImage.Source = bitmap;
                        ViewerCameraImage.Source = bitmap;
                        StudioCameraImage.Visibility = Visibility.Visible;
                        ViewerCameraImage.Visibility = Visibility.Visible;
                        StudioEmptyState.Visibility = Visibility.Collapsed;
                        ViewerEmptyState.Visibility = Visibility.Collapsed;
                        var message = $"Live preview from “{camera.Name}”. Nothing is being broadcast.";
                        StatusText.Text = message;
                        ViewerStatusText.Text = message;
                    }
                }, System.Windows.Threading.DispatcherPriority.Render).Task
                    .GetAwaiter()
                    .GetResult();

                Thread.Sleep(33);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(feed, _selectedFeed) || _isClosing)
                {
                    return;
                }

                SetEmptyPreview("Couldn't start camera preview", exception.Message);
                var message = $"Couldn't start “{camera.Name}”: {exception.Message}";
                StatusText.Text = message;
                ViewerStatusText.Text = message;
                MessageBox.Show(this, message, "Camera preview failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }).Task.GetAwaiter().GetResult();
        }
    }

    private async Task StopCameraPreviewAsync()
    {
        var cancellation = _cameraPreviewCancellation;
        var previewTask = _cameraPreviewTask;
        _cameraPreviewCancellation = null;
        _cameraPreviewTask = null;

        if (cancellation is null)
        {
            return;
        }

        await cancellation.CancelAsync();
        if (previewTask is not null)
        {
            await previewTask;
        }

        cancellation.Dispose();
    }

    private void SetEmptyPreview(string title, string message)
    {
        _isPlaying = false;
        StudioPlaybackButton.Content = "▶";
        PausePlayer(StudioMedia);
        PausePlayer(ViewerMedia);
        StudioMedia.Source = null;
        ViewerMedia.Source = null;
        StudioMedia.Visibility = Visibility.Collapsed;
        ViewerMedia.Visibility = Visibility.Collapsed;
        StudioCameraImage.Source = null;
        ViewerCameraImage.Source = null;
        StudioCameraImage.Visibility = Visibility.Collapsed;
        ViewerCameraImage.Visibility = Visibility.Collapsed;
        StudioEmptyState.Visibility = Visibility.Visible;
        ViewerEmptyState.Visibility = Visibility.Visible;
        StudioEmptyTitle.Text = title;
        ViewerEmptyTitle.Text = title;
        StudioEmptyMessage.Text = message;
        ViewerEmptyMessage.Text = message;
    }

    private static void PausePlayer(MediaElement player)
    {
        if (player.Source is not null)
        {
            player.Pause();
        }
    }

    private static void SetMediaSource(MediaElement player, Uri source)
    {
        if (player.Source != source)
        {
            player.Source = source;
        }
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaElement player)
        {
            return;
        }

        if (ReferenceEquals(player, GetActiveMedia()))
        {
            player.Play();
            _isPlaying = true;
            StudioPlaybackButton.Content = "Ⅱ";
        }
        else
        {
            player.Pause();
        }

        var message = $"Playing “{_selectedFeed?.Title}” in local preview. Not broadcasting.";
        StatusText.Text = message;
        ViewerStatusText.Text = message;
    }

    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is not MediaElement player
            || !ReferenceEquals(player, GetActiveMedia())
            || _selectedFeed?.CameraDevicePath is not null)
        {
            return;
        }

        var detail = e.ErrorException?.Message ?? "The video decoder could not open this file.";
        var message = $"Couldn't play “{_selectedFeed?.Title}”: {detail}";
        SetEmptyPreview("Video couldn't be played", detail);
        StatusText.Text = message;
        ViewerStatusText.Text = message;
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        if (sender is MediaElement player && ReferenceEquals(player, GetActiveMedia()))
        {
            player.Position = TimeSpan.Zero;
            player.Play();
        }
    }

    private MediaElement GetActiveMedia() => _isViewerMode ? ViewerMedia : StudioMedia;

    private async void OnPlaybackClick(object sender, RoutedEventArgs e)
    {
        if (_selectedFeed?.CameraDevicePath is not null)
        {
            if (_isPlaying)
            {
                await StopCameraPreviewAsync();
                SetEmptyPreview("Camera preview paused", "Choose Play to reconnect to this camera.");
                StatusText.Text = "Camera preview paused. Nothing is being broadcast.";
                ViewerStatusText.Text = StatusText.Text;
            }
            else if (CameraSourceList.SelectedItem is CameraDevice camera)
            {
                await StartCameraPreviewAsync(camera, _selectedFeed);
            }

            return;
        }

        var player = GetActiveMedia();
        if (player.Source is null)
        {
            var message = "Choose a connected camera and select Add camera, or import a video feed.";
            StatusText.Text = message;
            ViewerStatusText.Text = message;
            return;
        }

        if (_isPlaying)
        {
            player.Pause();
            _isPlaying = false;
            StudioPlaybackButton.Content = "▶";
            StatusText.Text = "Preview paused. Nothing is being broadcast.";
            ViewerStatusText.Text = StatusText.Text;
        }
        else
        {
            player.Play();
            _isPlaying = true;
            StudioPlaybackButton.Content = "Ⅱ";
            StatusText.Text = "Playing in local preview. Nothing is being broadcast.";
            ViewerStatusText.Text = StatusText.Text;
        }
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (StudioMedia is not null)
        {
            StudioMedia.Volume = e.NewValue;
        }

        if (ViewerMedia is not null)
        {
            ViewerMedia.Volume = e.NewValue;
        }
    }

    private void OnDestinationSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        UpdateDestinationCount();
    }

    private void OnConfigureDestinationClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Destination destination })
        {
            return;
        }

        var isNewDestination = !_destinations.Contains(destination);
        var dialog = new DestinationSetupWindow(destination, isNewDestination) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (isNewDestination
            && _destinations.Any(item => string.Equals(
                item.Name, dialog.DestinationName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "A destination with this name already exists.", "Duplicate destination",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        destination.Name = isNewDestination ? dialog.DestinationName : destination.Name;
        destination.Initial = isNewDestination
            ? dialog.DestinationName[..1].ToUpperInvariant()
            : destination.Initial;
        destination.ServerUrl = dialog.ServerUrl;

        if (dialog.HasNewStreamKey)
        {
            try
            {
                destination.SetStreamKey(dialog.StreamKey);
            }
            catch (CryptographicException exception)
            {
                MessageBox.Show(this, $"Couldn't securely save the stream key.\n\n{exception.Message}",
                    "Secure storage failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        if (isNewDestination)
        {
            destination.IsSelected = true;
            _destinations.Add(destination);
        }

        DestinationList.Items.Refresh();
        UpdateDestinationCount();
        SaveStudio(showConfirmation: false);
        SetStudioStatus($"{destination.Name} is set up. Select its checkbox to include it in the next broadcast.");
    }

    private void OnAddDestinationClick(object sender, RoutedEventArgs e)
    {
        var destination = new Destination(
            Guid.NewGuid().ToString("N"),
            "New destination",
            "+",
            "#55637A",
            "rtmp://");
        OnConfigureDestinationClick(
            new Button { Tag = destination },
            new RoutedEventArgs(Button.ClickEvent));
    }

    private async void OnStartBroadcastClick(object sender, RoutedEventArgs e)
    {
        if (_broadcastProcess is not null)
        {
            await StopBroadcastAsync();
            return;
        }

        if (_broadcastIsStarting)
        {
            return;
        }

        var destinations = _destinations.Where(destination => destination.IsSelected).ToList();
        if (destinations.Count == 0)
        {
            SetStudioStatus("Set up a destination and select it before starting a broadcast.");
            MessageBox.Show(this, "Set up at least one streaming destination, then select its checkbox.",
                "No destinations selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var feed = _selectedFeed;
        if (feed is null)
        {
            SetStudioStatus("Select a camera feed or video before starting a broadcast.");
            return;
        }

        CameraDevice? camera = null;
        if (!string.IsNullOrWhiteSpace(feed.CameraDevicePath))
        {
            camera = Cameras.FirstOrDefault(device => device.DevicePath == feed.CameraDevicePath);
            if (camera is null)
            {
                SetStudioStatus("Reconnect the selected camera and choose Refresh before starting a broadcast.");
                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(feed.VideoPath) || !File.Exists(feed.VideoPath))
        {
            SetStudioStatus("Select a connected camera feed or an existing video before broadcasting.");
            return;
        }

        ProcessStartInfo startInfo;
        try
        {
            startInfo = BroadcastService.CreateStartInfo(
                feed,
                camera,
                AudioSourceList.SelectedItem as string,
                IncludeMicrophoneCheckBox.IsChecked == true,
                destinations);
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException
            or CryptographicException or FormatException)
        {
            SetStudioStatus(exception.Message);
            MessageBox.Show(this, exception.Message, "Broadcast could not start",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var broadcast = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        broadcast.ErrorDataReceived += OnBroadcastErrorDataReceived;
        broadcast.Exited += OnBroadcastProcessExited;
        _broadcastIsStarting = true;
        SetBroadcastingControls(true);

        try
        {
            if (camera is not null)
            {
                await StopCameraPreviewAsync();
            }

            if (_isClosing)
            {
                throw new InvalidOperationException("Mosaic Studio is closing.");
            }

            if (!broadcast.Start())
            {
                throw new InvalidOperationException("FFmpeg could not be started.");
            }

            _broadcastProcess = broadcast;
            _broadcastIsStarting = false;
            _broadcastDestinations = destinations;
            _isBroadcastStopRequested = false;
            _lastBroadcastError = null;
            broadcast.BeginErrorReadLine();
            broadcast.OutputDataReceived += OnBroadcastProgressDataReceived;
            broadcast.BeginOutputReadLine();
            SetBroadcastingControls(true);
            if (camera is not null)
            {
                SetEmptyPreview(
                    "Camera is live",
                    "The camera is streaming to the selected destinations. Its local preview resumes when you stop the broadcast.");
            }

            var message = $"Starting live broadcast to {destinations.Count} destination{(destinations.Count == 1 ? "" : "s")} …";
            SetStudioStatus(message);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
            or IOException)
        {
            _broadcastIsStarting = false;
            broadcast.Dispose();
            SetBroadcastingControls(false);
            SetStudioStatus($"Couldn't start the broadcast: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Broadcast could not start",
                MessageBoxButton.OK, MessageBoxImage.Error);
            if (camera is not null && !_isClosing)
            {
                await SyncPreviewAsync(feed);
            }
        }
    }

    private void OnBroadcastErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data) || sender is not Process process)
        {
            return;
        }

        var safeError = e.Data;
        foreach (var destination in _destinations)
        {
            if (!destination.IsConfigured)
            {
                continue;
            }

            try
            {
                safeError = safeError.Replace(destination.GetStreamKey(), "[redacted]", StringComparison.Ordinal);
            }
            catch (CryptographicException)
            {
                safeError = "The broadcast process reported an error. Check the destination setup and try again.";
            }
        }

        if (safeError.Length > 260)
        {
            safeError = safeError[..260];
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(_broadcastProcess, process))
            {
                _lastBroadcastError = safeError;
                SetStudioStatus(safeError);
            }
        });
    }

    private void OnBroadcastProgressDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data != "progress=continue" || sender is not Process process)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_broadcastProcess, process))
            {
                return;
            }

            foreach (var destination in _broadcastDestinations)
            {
                destination.IsLive = true;
            }

            DestinationList.Items.Refresh();
            SetStudioStatus($"LIVE — broadcasting to {string.Join(", ", _broadcastDestinations.Select(destination => destination.Name))}.");
        }));
    }

    private void OnBroadcastProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process process)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_broadcastProcess, process))
            {
                process.Dispose();
                return;
            }

            var wasStoppedByUser = _isBroadcastStopRequested;
            var exitCode = process.ExitCode;
            _broadcastProcess = null;
            _broadcastIsStarting = false;
            foreach (var destination in _broadcastDestinations)
            {
                destination.IsLive = false;
            }

            _broadcastDestinations = [];
            DestinationList.Items.Refresh();
            _isBroadcastStopRequested = false;
            SetBroadcastingControls(false);
            process.Dispose();

            var message = wasStoppedByUser
                ? "Broadcast stopped."
                : exitCode == 0
                    ? "Broadcast ended."
                    : $"Broadcast stopped (FFmpeg exit code {exitCode}). {_lastBroadcastError ?? "Check the stream URL, credentials, camera and network connection."}";
            SetStudioStatus(message);

            if (_selectedFeed?.CameraDevicePath is not null && !_isClosing)
            {
                _ = SyncPreviewAsync(_selectedFeed);
            }
        }));
    }

    private async Task StopBroadcastAsync()
    {
        var process = _broadcastProcess;
        if (process is null || process.HasExited)
        {
            return;
        }

        _isBroadcastStopRequested = true;
        StartBroadcastButton.IsEnabled = false;
        SetStudioStatus("Stopping broadcast …");

        try
        {
            await process.StandardInput.WriteLineAsync("q");
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private void SetBroadcastingControls(bool isBroadcasting)
    {
        StartBroadcastButton.Content = isBroadcasting
            ? _broadcastIsStarting ? "Starting broadcast…" : "Stop broadcast"
            : "Start broadcast";
        StartBroadcastButton.IsEnabled = !_broadcastIsStarting;
        StudioFeedList.IsEnabled = !isBroadcasting;
        ViewerFeedList.IsEnabled = !isBroadcasting;
        CameraSourceList.IsEnabled = !isBroadcasting;
        RefreshCamerasButton.IsEnabled = !isBroadcasting;
        AddCameraButton.IsEnabled = !isBroadcasting;
        ImportVideoButton.IsEnabled = !isBroadcasting;
        AudioSourceList.IsEnabled = !isBroadcasting && AudioDevices.Count > 0;
        IncludeMicrophoneCheckBox.IsEnabled = !isBroadcasting && AudioDevices.Count > 0
            && _selectedFeed?.CameraDevicePath is not null;
        StudioPlaybackButton.IsEnabled = !isBroadcasting;
        DestinationList.IsEnabled = !isBroadcasting;
    }

    private void SetStudioStatus(string message)
    {
        StatusText.Text = message;
        ViewerStatusText.Text = message;
    }

    private void UpdateFeedCount() => FeedCountText.Text = $"{Feeds.Count} FEEDS";

    private void UpdateDestinationCount()
    {
        var count = _destinations.Count(destination => destination.IsSelected);
        DestinationCountText.Text = $"{count} DESTINATIONS SELECTED";
    }

    private static string GetFeedAccent(int index) => (index % 3) switch
    {
        0 => "#8165EB",
        1 => "#328D9E",
        _ => "#B47A45"
    };
}

public sealed class FeedScene(
    string id,
    string title,
    string description,
    string typeLabel,
    string artworkLabel,
    string accentHex,
    string? videoPath = null,
    string? cameraDevicePath = null)
{
    public FeedScene()
        : this(Guid.NewGuid().ToString("N"), "", "", "", "", "#8165EB")
    {
    }

    public string Id { get; set; } = id;
    public string Title { get; set; } = title;
    public string Description { get; set; } = description;
    public string TypeLabel { get; set; } = typeLabel;
    public string ArtworkLabel { get; set; } = artworkLabel;
    public string AccentHex { get; set; } = accentHex;
    public string? VideoPath { get; set; } = videoPath;
    public string? CameraDevicePath { get; set; } = cameraDevicePath;

    [System.Text.Json.Serialization.JsonIgnore]
    public Brush Accent => new SolidColorBrush((Color)ColorConverter.ConvertFromString(AccentHex)!);
}

public sealed class Destination : INotifyPropertyChanged
{
    private string? _protectedStreamKey;
    private bool _isSelected;
    private bool _isLive;

    public Destination()
        : this(Guid.NewGuid().ToString("N"), "", "", "#55637A", "")
    {
    }

    public Destination(string name, string initial, string accentHex)
        : this(Guid.NewGuid().ToString("N"), name, initial, accentHex, "")
    {
    }

    public Destination(string id, string name, string initial, string accentHex, string serverUrl)
    {
        Id = id;
        Name = name;
        Initial = initial;
        AccentHex = accentHex;
        ServerUrl = serverUrl;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; set; }
    public string Name { get; set; }
    public string Initial { get; set; }
    public string AccentHex { get; set; }
    public string ServerUrl { get; set; }
    public string? ProtectedStreamKey
    {
        get => _protectedStreamKey;
        set
        {
            if (_protectedStreamKey == value)
            {
                return;
            }

            _protectedStreamKey = value;
            OnPropertyChanged(nameof(ProtectedStreamKey));
            OnPropertyChanged(nameof(IsConfigured));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ProtectedStreamKey);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLive
    {
        get => _isLive;
        set
        {
            if (_isLive == value)
            {
                return;
            }

            _isLive = value;
            OnPropertyChanged(nameof(IsLive));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBrush));
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusText => IsLive ? "LIVE" : IsConfigured ? "Configured" : "Set up required";

    [System.Text.Json.Serialization.JsonIgnore]
    public Brush StatusBrush => new SolidColorBrush(
        IsLive ? Color.FromRgb(126, 227, 173) : Color.FromRgb(148, 153, 169));

    [System.Text.Json.Serialization.JsonIgnore]
    public Brush Accent => new SolidColorBrush((Color)ColorConverter.ConvertFromString(AccentHex)!);

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void SetStreamKey(string streamKey)
    {
        var plaintext = System.Text.Encoding.UTF8.GetBytes(streamKey);
        try
        {
            var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            ProtectedStreamKey = Convert.ToBase64String(encrypted);
            CryptographicOperations.ZeroMemory(encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public string GetStreamKey()
    {
        if (string.IsNullOrWhiteSpace(ProtectedStreamKey))
        {
            throw new CryptographicException($"No stream key is configured for {Name}.");
        }

        var encrypted = Convert.FromBase64String(ProtectedStreamKey);
        try
        {
            var plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try
            {
                return System.Text.Encoding.UTF8.GetString(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }
}

public sealed class StudioProject
{
    public List<FeedScene> Feeds { get; set; } = [];
    public string? SelectedFeedId { get; set; }
    public List<string>? SelectedDestinationNames { get; set; }
    public List<Destination>? Destinations { get; set; }
}
