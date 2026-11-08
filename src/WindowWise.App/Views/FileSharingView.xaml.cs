using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WindowWise.Models;
using WindowWise.Services;

namespace WindowWise.Views;

public partial class FileSharingView : UserControl, IAsyncDisposable
{
    public event EventHandler? BackRequested;

    private const int HttpPort = 5001;
    private readonly HttpFileServer _server = new(HttpPort);
    private readonly HttpFileClient _client = new();
    private readonly ObservableCollection<LocalFileRow> _sharedFiles = new();
    private readonly ObservableCollection<DeviceRow> _devices = new();
    private readonly ObservableCollection<RemoteFileRow> _remoteFiles = new();
    private LanDiscoveryService? _discovery;
    private CancellationTokenSource? _announcementCts;
    private CancellationTokenSource? _listCts;
    private CancellationTokenSource? _downloadCts;
    private Task _announcementTask = Task.CompletedTask;
    private Task _scanTask = Task.CompletedTask;
    private Task _listTask = Task.CompletedTask;
    private Task _downloadTask = Task.CompletedTask;
    private Task _sharingTask = Task.CompletedTask;
    private Task _discoveryCleanupTask = Task.CompletedTask;
    private Task? _shutdownTask;
    private bool _receiveMode;
    private bool _isSharing;
    private bool _sharingBusy;
    private bool _scanning;
    private bool _loadingFiles;
    private bool _downloading;
    private bool _shuttingDown;
    private bool _updatingDevices;
    private string _destinationFolder;

    public FileSharingView()
    {
        InitializeComponent();
        SharedFileList.ItemsSource = _sharedFiles;
        DeviceList.ItemsSource = _devices;
        RemoteFileList.ItemsSource = _remoteFiles;
        LocalDeviceText.Text = Environment.MachineName;
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        _destinationFolder = Directory.Exists(downloads)
            ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        DestinationText.Text = _destinationFolder;
        DestinationText.ToolTip = _destinationFolder;
        UpdateControls();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void ShareMode_Click(object sender, RoutedEventArgs e)
    {
        if (_sharingBusy || _scanning || _downloading || _shuttingDown) return;
        _listCts?.Cancel();
        _receiveMode = false;
        SetStatus("Add files, then select Start sharing.");
        UpdateControls();
    }

    private async void ReceiveMode_Click(object sender, RoutedEventArgs e)
    {
        if (_sharingBusy || _scanning || _downloading || _shuttingDown) return;
        if (_isSharing)
            await (_sharingTask = ChangeSharingAsync(false));
        if (_shuttingDown) return;
        _receiveMode = true;
        SetStatus("Select Find devices, then choose a device on the left to receive files from.");
        UpdateControls();
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_isSharing || _sharingBusy || _shuttingDown) return;
        var dialog = new OpenFileDialog { Title = "Choose files to share", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            _server.SetSharedFiles(dialog.FileNames);
            SetStatus("Files added. Select Start sharing so other devices can receive them.");
        }
        catch (Exception ex) { SetStatus(GetErrorMessage(ex), true); }
        finally { UpdateSharedFiles(); }
    }

    private void RemoveFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_isSharing || _sharingBusy || _shuttingDown) return;
        var selected = SharedFileList.SelectedItems.Cast<LocalFileRow>().ToArray();
        foreach (var file in selected)
            _server.DeleteShareFile(file.Path);
        UpdateSharedFiles();
        SetStatus($"Removed {selected.Length} file(s) from the share list. The original files are kept.");
    }

    private void UpdateSharedFiles()
    {
        _sharedFiles.Clear();
        foreach (var entry in _server.SharedFiles.OrderBy(entry => entry.Value.Name, StringComparer.CurrentCultureIgnoreCase))
            _sharedFiles.Add(new LocalFileRow(entry.Key, entry.Value));
        UpdateControls();
    }

    private async void ToggleSharing_Click(object sender, RoutedEventArgs e)
    {
        if (_sharingBusy || _shuttingDown) return;
        await (_sharingTask = ChangeSharingAsync(!_isSharing));
    }

    private async Task ChangeSharingAsync(bool start)
    {
        _sharingBusy = true;
        UpdateControls();
        try
        {
            if (start)
            {
                // HTTP를 먼저 시작한 다음에만 탐색 요청에 응답한다.
                await _server.StartAsync();
                _discovery = new LanDiscoveryService(HttpPort);
                _announcementCts = new CancellationTokenSource();
                _announcementTask = ListenForPeersAsync(_discovery, _announcementCts.Token);
                _isSharing = true;
                SetStatus("Sharing. On the other device, select Receive files, then Find devices.");
            }
            else
            {
                SetStatus("Finishing active requests and stopping sharing.");
                await StopSharingCoreAsync();
                SetStatus("Sharing stopped. You can edit the file list.");
            }
        }
        catch (Exception ex)
        {
            // UDP 포트 사용 중 등 시작 실패 시 HTTP 서버도 함께 정리한다.
            try { await StopSharingCoreAsync(); }
            catch (Exception cleanupError)
            {
                SetStatus($"{GetErrorMessage(ex)} Cleanup: {GetErrorMessage(cleanupError)}", true);
                return;
            }
            SetStatus(GetErrorMessage(ex), true);
        }
        finally
        {
            _sharingBusy = false;
            UpdateControls();
        }
    }

    private async Task ListenForPeersAsync(LanDiscoveryService discovery, CancellationToken token)
    {
        try
        {
            // 공유 중에는 15초 탐색과 별도로 계속 응답한다.
            await discovery.ReceiveLoopAsync(token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_shuttingDown)
                SetStatus($"Device discovery stopped responding. Stop sharing, then start again. {GetErrorMessage(ex)}", true);
        }
    }

    private async Task StopSharingCoreAsync()
    {
        _announcementCts?.Cancel();
        await _announcementTask;
        await ReleaseDiscoveryAsync();
        _announcementCts?.Dispose();
        _announcementCts = null;
        try { await _server.StopAsync(); }
        finally { _isSharing = false; }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning || _downloading || _shuttingDown) return;
        await (_scanTask = ScanDevicesAsync());
    }

    private async Task ScanDevicesAsync()
    {
        _scanning = true;
        _listCts?.Cancel();
        DeviceList.SelectedItem = null;
        _devices.Clear();
        _remoteFiles.Clear();
        SetStatus("Looking for nearby devices. This takes about 15 seconds.");
        UpdateControls();
        try
        {
            _discovery = new LanDiscoveryService(HttpPort);
            _discovery.DeviceChanged += DevicesChanged;
            await _discovery.FindDevicesAsync();
        }
        catch (OperationCanceledException)
        {
            // FindDevicesAsync의 15초 탐색 종료도 취소 예외로 전달된다.
        }
        catch (Exception ex)
        {
            SetStatus(GetErrorMessage(ex), true);
            return;
        }
        finally
        {
            await ReleaseDiscoveryAsync();
            _scanning = false;
            UpdateControls();
        }
        if (!_shuttingDown && !_loadingFiles && !_downloading)
            SetStatus(_devices.Count == 0
                ? "No devices found. Check that both devices are on the same network and the other device is sharing, then try again."
                : $"Found {_devices.Count} device(s). Choose one to receive from.");
    }

    private void DevicesChanged()
    {
        // 탐색을 UI 스레드에서 시작하므로 서비스의 await 이후에도 같은 컨텍스트를 사용한다.
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(DevicesChanged);
            return;
        }
        if (_discovery == null || _shuttingDown) return;
        var selectedId = (DeviceList.SelectedItem as DeviceRow)?.Device.DeviceId;
        _updatingDevices = true;
        try
        {
            foreach (var device in _discovery.DiscoveredDevice.Values)
            {
                int index = -1;
                for (int i = 0; i < _devices.Count; i++)
                    if (_devices[i].Device.DeviceId == device.DeviceId) { index = i; break; }
                if (index < 0)
                    _devices.Add(new DeviceRow(device));
                else if (_devices[index].Name != device.DeviceName ||
                         _devices[index].Device.HttpPort != device.HttpPort ||
                         !_devices[index].Device.DiscoveryEndpoint.Address.Equals(device.DiscoveryEndpoint.Address))
                    _devices[index] = new DeviceRow(device);
            }
            if (selectedId.HasValue)
                DeviceList.SelectedItem = _devices.FirstOrDefault(row => row.Device.DeviceId == selectedId.Value);
        }
        finally { _updatingDevices = false; }
        UpdateControls();
    }

    private Task ReleaseDiscoveryAsync()
    {
        if (_discovery != null)
        {
            var discovery = _discovery;
            _discovery = null;
            discovery.DeviceChanged -= DevicesChanged;
            _discoveryCleanupTask = DisposeDiscoveryAsync(discovery);
        }
        return _discoveryCleanupTask;
    }

    private static async Task DisposeDiscoveryAsync(LanDiscoveryService discovery)
    {
        try { await discovery.DisposeAsync(); }
        catch (OperationCanceledException) { }
        catch (SocketException)
        {
            // 서비스는 finally에서 소켓을 닫는다. 원래 통신 실패는 작업 쪽에서 표시한다.
        }
    }

    private async void DeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingDevices || _shuttingDown) return;
        await (_listTask = LoadRemoteFilesAsync());
    }

    private async void RefreshFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingFiles || _downloading || _shuttingDown) return;
        await (_listTask = LoadRemoteFilesAsync());
    }

    private async Task LoadRemoteFilesAsync()
    {
        _listCts?.Cancel();
        _remoteFiles.Clear();
        using var cts = new CancellationTokenSource();
        _listCts = cts;
        var selected = DeviceList.SelectedItem as DeviceRow;
        _loadingFiles = selected != null;
        RemoteTitleText.Text = selected?.Name ?? "Choose a device";
        RemoteEmptyText.Text = selected == null ? "Choose a device on the left." : "Loading the file list…";
        UpdateControls();
        try
        {
            if (selected == null) return;
            SetStatus($"Loading files shared by {selected.Name}.");
            var files = await _client.GetFilesAsync(selected.Device, cts.Token);
            if (cts.IsCancellationRequested || _shuttingDown || !ReferenceEquals(_listCts, cts)) return;
            foreach (var file in files.OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase))
                _remoteFiles.Add(new RemoteFileRow(file));
            RemoteEmptyText.Text = "This device is not sharing any files.";
            SetStatus($"Loaded {files.Count} file(s). Choose files to receive. Use Ctrl or Shift to select several.");
        }
        catch (OperationCanceledException)
        {
            if (!cts.IsCancellationRequested && !_shuttingDown)
                SetStatus("The file list request timed out. Select Refresh to try again.", true);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_listCts, cts) && !_shuttingDown)
            {
                RemoteEmptyText.Text = "Could not load the file list. Select Refresh to try again.";
                SetStatus(GetErrorMessage(ex), true);
            }
        }
        finally
        {
            if (ReferenceEquals(_listCts, cts))
            {
                _listCts = null;
                _loadingFiles = false;
                UpdateControls();
            }
        }
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading || _shuttingDown) return;
        var dialog = new OpenFolderDialog { Title = "Choose a download folder", InitialDirectory = _destinationFolder };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _destinationFolder = dialog.FolderName;
        DestinationText.Text = _destinationFolder;
        DestinationText.ToolTip = _destinationFolder;
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading || _loadingFiles || _shuttingDown) return;
        if (DeviceList.SelectedItem is not DeviceRow device) return;
        var files = RemoteFileList.SelectedItems.Cast<RemoteFileRow>().ToArray();
        if (files.Length == 0) return;
        await (_downloadTask = DownloadSelectedAsync(device.Device, files));
    }

    private async Task DownloadSelectedAsync(DiscoveredDevice device, RemoteFileRow[] files)
    {
        _downloading = true;
        using var cts = new CancellationTokenSource();
        _downloadCts = cts;
        int completed = 0;
        UpdateControls();
        try
        {
            foreach (var file in files)
            {
                cts.Token.ThrowIfCancellationRequested();
                string destination = GetAvailableDestination(_destinationFolder, file.Name);
                SetStatus($"Downloading · {completed + 1}/{files.Length} · {file.Name}");
                await _client.DownloadFileAsync(device, file.File.Id, destination, cts.Token);
                completed++;
            }
            SetStatus($"Downloaded {completed} file(s) · {_destinationFolder}");
        }
        catch (OperationCanceledException)
        {
            SetStatus($"Download stopped. {completed} completed file(s) remain in the save folder.");
        }
        catch (Exception ex)
        {
            SetStatus($"{completed}/{files.Length} completed. {GetErrorMessage(ex)}", true);
        }
        finally
        {
            _downloadCts = null;
            _downloading = false;
            UpdateControls();
        }
    }

    private static string GetAvailableDestination(string folder, string name)
    {
        // 상대가 보낸 파일명으로 저장 폴더 밖에 파일을 만들지 않도록 한다.
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) ||
            name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("The other device sent an invalid file name.");
        string path = Path.Combine(folder, name);
        int suffix = 1;
        while (File.Exists(path) || Directory.Exists(path))
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({suffix++}){Path.GetExtension(name)}");
        return path;
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        CancelDownloadButton.IsEnabled = false;
        SetStatus("Canceling the download and removing temporary files.");
    }

    private void FileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) UpdateControls();
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = error ? Brushes.Firebrick : (Brush)new BrushConverter().ConvertFromString("#536782")!;
    }

    private void UpdateControls()
    {
        bool available = !_shuttingDown;
        bool modeEnabled = available && !_sharingBusy && !_scanning && !_downloading;
        ShareModeButton.IsEnabled = modeEnabled;
        ReceiveModeButton.IsEnabled = modeEnabled;
        ShareModeButton.Background = !_receiveMode ? (Brush)new BrushConverter().ConvertFromString("#EEF4FF")! : Brushes.White;
        ReceiveModeButton.Background = _receiveMode ? (Brush)new BrushConverter().ConvertFromString("#EEF4FF")! : Brushes.White;
        ShareModeButton.BorderBrush = !_receiveMode ? (Brush)FindResource("PrimaryBrush") : (Brush)FindResource("BorderBrush");
        ReceiveModeButton.BorderBrush = _receiveMode ? (Brush)FindResource("PrimaryBrush") : (Brush)FindResource("BorderBrush");
        ReceiveModeButton.ToolTip = _isSharing ? "Switching to receive stops the current share." : null;
        SharePanel.Visibility = _receiveMode ? Visibility.Collapsed : Visibility.Visible;
        ReceivePanel.Visibility = _receiveMode ? Visibility.Visible : Visibility.Collapsed;
        AddFilesButton.IsEnabled = available && !_isSharing && !_sharingBusy;
        RemoveFilesButton.IsEnabled = AddFilesButton.IsEnabled && SharedFileList.SelectedItems.Count > 0;
        SharingButton.IsEnabled = available && !_sharingBusy && (_isSharing || _sharedFiles.Count > 0);
        SharingButton.Content = _sharingBusy ? "Working…" : _isSharing ? "Stop sharing" : "Start sharing";
        SharingStateText.Text = _isSharing ? "● Sharing" : "Sharing stopped";
        SharingStateText.Foreground = _isSharing ? Brushes.SeaGreen : (Brush)FindResource("MutedTextBrush");
        SharingHintText.Text = _isSharing ? "Stop sharing before changing the list." : "Start sharing when you are ready.";
        ShareCountText.Text = $"{_sharedFiles.Count} file(s) · {FormatSize(_sharedFiles.Sum(file => (double)file.File.Size))}";
        ShareEmptyState.Visibility = _sharedFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.IsEnabled = available && !_scanning && !_downloading;
        ScanButton.Content = _scanning ? "Searching…" : "Find devices";
        DeviceCountText.Text = $"{_devices.Count} device(s) found";
        DeviceEmptyState.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeviceEmptyState.Text = _scanning ? "Looking for nearby devices…" : "Find devices to get started.\nDevices that start sharing\nappear here.";
        DeviceList.IsEnabled = available && !_downloading;
        RefreshFilesButton.IsEnabled = available && DeviceList.SelectedItem != null && !_loadingFiles && !_downloading;
        RemoteFileList.IsEnabled = available && !_loadingFiles && !_downloading;
        RemoteEmptyState.Visibility = _remoteFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RemoteCountText.Text = _loadingFiles ? "Loading files…" : DeviceList.SelectedItem == null
            ? "Files shared by the selected device appear here." : $"{_remoteFiles.Count} file(s) · {RemoteFileList.SelectedItems.Count} selected";
        ChooseFolderButton.IsEnabled = available && !_downloading;
        DownloadButton.IsEnabled = available && !_loadingFiles && !_downloading && DeviceList.SelectedItem != null && RemoteFileList.SelectedItems.Count > 0;
        DownloadButton.Content = _downloading ? "Downloading…" : "Download selected files";
        CancelDownloadButton.Visibility = _downloading ? Visibility.Visible : Visibility.Collapsed;
        CancelDownloadButton.IsEnabled = available && _downloadCts?.IsCancellationRequested == false;
        ActivityProgress.Visibility = _scanning || _loadingFiles || _downloading || _sharingBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    public Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    public ValueTask DisposeAsync() => new(ShutdownAsync());

    private async Task ShutdownCoreAsync()
    {
        _shuttingDown = true;
        _listCts?.Cancel();
        _downloadCts?.Cancel();
        UpdateControls();
        await _sharingTask;
        try { await StopSharingCoreAsync(); }
        finally
        {
            await Task.WhenAll(_scanTask, _listTask, _downloadTask);
        }
    }

    private static string GetErrorMessage(Exception ex) => ex switch
    {
        SocketException => "Could not start device discovery. Check whether another WindowWise is running and check your network connection.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => "This file is no longer shared. Refresh the list.",
        HttpRequestException => "Could not connect to the other device. Check that it is sharing and that the firewall allows it, then try again.",
        UnauthorizedAccessException => "You do not have permission to access the file or save folder.",
        _ => ex.Message
    };

    private static string FormatSize(double size)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{size:0} B" : $"{size:0.#} {units[unit]}";
    }

    private sealed record LocalFileRow(string Path, SharedFileInfo File)
    {
        public string Name => File.Name;
        public string SizeText => FormatSize(File.Size);
    }

    private sealed record RemoteFileRow(SharedFileInfo File)
    {
        public string Name => File.Name;
        public string SizeText => FormatSize(File.Size);
    }

    private sealed record DeviceRow(DiscoveredDevice Device)
    {
        public string Name => Device.DeviceName;
        public string Address => $"{Device.DiscoveryEndpoint.Address}:{Device.HttpPort}";
    }
}
