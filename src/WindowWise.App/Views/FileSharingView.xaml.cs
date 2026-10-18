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

    private void ShareMode_Click(object sender, RoutedEventArgs e)
    {
        if (_sharingBusy || _scanning || _downloading || _shuttingDown) return;
        _listCts?.Cancel();
        _receiveMode = false;
        SetStatus("파일을 추가하고 공유 시작을 누르세요.");
        UpdateControls();
    }

    private async void ReceiveMode_Click(object sender, RoutedEventArgs e)
    {
        if (_sharingBusy || _scanning || _downloading || _shuttingDown) return;
        if (_isSharing)
            await (_sharingTask = ChangeSharingAsync(false));
        if (_shuttingDown) return;
        _receiveMode = true;
        SetStatus("기기 찾기를 누른 뒤, 왼쪽에서 파일을 받을 기기를 선택하세요.");
        UpdateControls();
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_isSharing || _sharingBusy || _shuttingDown) return;
        var dialog = new OpenFileDialog { Title = "공유할 파일 선택", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            _server.SetSharedFiles(dialog.FileNames);
            SetStatus("파일을 추가했습니다. 공유 시작을 누르면 다른 기기에서 받을 수 있습니다.");
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
        SetStatus($"{selected.Length}개 파일을 공유 목록에서 제외했습니다. 원본 파일은 유지됩니다.");
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
                SetStatus("공유 중입니다. 다른 기기에서 파일 공유받기 → 기기 찾기를 누르세요.");
            }
            else
            {
                SetStatus("진행 중인 요청을 정리하고 공유를 중지하고 있습니다.");
                await StopSharingCoreAsync();
                SetStatus("공유를 중지했습니다. 파일 목록을 편집할 수 있습니다.");
            }
        }
        catch (Exception ex)
        {
            // UDP 포트 사용 중 등 시작 실패 시 HTTP 서버도 함께 정리한다.
            try { await StopSharingCoreAsync(); }
            catch (Exception cleanupError)
            {
                SetStatus($"{GetErrorMessage(ex)} 종료 처리: {GetErrorMessage(cleanupError)}", true);
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
                SetStatus($"기기 탐색 응답이 중단됐습니다. 공유를 중지한 후 다시 시작하세요. {GetErrorMessage(ex)}", true);
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
        SetStatus("주변 기기를 찾고 있습니다. 탐색은 약 15초 동안 진행됩니다.");
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
                ? "기기를 찾지 못했습니다. 같은 네트워크인지, 상대가 공유 중인지 확인한 뒤 다시 찾아보세요."
                : $"{_devices.Count}대의 기기를 찾았습니다. 받을 기기를 선택하세요.");
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
        RemoteTitleText.Text = selected?.Name ?? "기기를 선택하세요";
        RemoteEmptyText.Text = selected == null ? "왼쪽에서 받을 기기를 선택하세요." : "파일 목록을 불러오는 중입니다…";
        UpdateControls();
        try
        {
            if (selected == null) return;
            SetStatus($"{selected.Name}의 공유 파일 목록을 불러오고 있습니다.");
            var files = await _client.GetFilesAsync(selected.Device, cts.Token);
            if (cts.IsCancellationRequested || _shuttingDown || !ReferenceEquals(_listCts, cts)) return;
            foreach (var file in files.OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase))
                _remoteFiles.Add(new RemoteFileRow(file));
            RemoteEmptyText.Text = "이 기기에 공유된 파일이 없습니다.";
            SetStatus($"{files.Count}개 파일을 불러왔습니다. 받을 파일을 선택하세요. 여러 개는 Ctrl 또는 Shift로 선택할 수 있습니다.");
        }
        catch (OperationCanceledException)
        {
            if (!cts.IsCancellationRequested && !_shuttingDown)
                SetStatus("파일 목록 요청 시간이 초과됐습니다. 새로고침으로 다시 요청하세요.", true);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_listCts, cts) && !_shuttingDown)
            {
                RemoteEmptyText.Text = "목록을 불러오지 못했습니다. 새로고침으로 다시 시도하세요.";
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
        var dialog = new OpenFolderDialog { Title = "다운로드 저장 폴더 선택", InitialDirectory = _destinationFolder };
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
                SetStatus($"다운로드 중 · {completed + 1}/{files.Length} · {file.Name}");
                await _client.DownloadFileAsync(device, file.File.Id, destination, cts.Token);
                completed++;
            }
            SetStatus($"{completed}개 파일 다운로드 완료 · {_destinationFolder}");
        }
        catch (OperationCanceledException)
        {
            SetStatus($"다운로드를 중단했습니다. 완료된 {completed}개 파일은 저장 폴더에 남아 있습니다.");
        }
        catch (Exception ex)
        {
            SetStatus($"{completed}/{files.Length}개 완료. {GetErrorMessage(ex)}", true);
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
            throw new IOException("상대가 보낸 파일 이름이 올바르지 않습니다.");
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
        SetStatus("다운로드를 취소하고 임시 파일을 정리하고 있습니다.");
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
        ReceiveModeButton.ToolTip = _isSharing ? "받기로 전환하면 현재 공유가 중지됩니다." : null;
        SharePanel.Visibility = _receiveMode ? Visibility.Collapsed : Visibility.Visible;
        ReceivePanel.Visibility = _receiveMode ? Visibility.Visible : Visibility.Collapsed;
        AddFilesButton.IsEnabled = available && !_isSharing && !_sharingBusy;
        RemoveFilesButton.IsEnabled = AddFilesButton.IsEnabled && SharedFileList.SelectedItems.Count > 0;
        SharingButton.IsEnabled = available && !_sharingBusy && (_isSharing || _sharedFiles.Count > 0);
        SharingButton.Content = _sharingBusy ? "처리 중…" : _isSharing ? "공유 중지" : "공유 시작";
        SharingStateText.Text = _isSharing ? "● 공유 중" : "공유 중지됨";
        SharingStateText.Foreground = _isSharing ? Brushes.SeaGreen : (Brush)FindResource("MutedTextBrush");
        SharingHintText.Text = _isSharing ? "목록을 바꾸려면 공유를 먼저 중지하세요." : "준비가 되면 공유를 시작하세요.";
        ShareCountText.Text = $"{_sharedFiles.Count}개 파일 · {FormatSize(_sharedFiles.Sum(file => (double)file.File.Size))}";
        ShareEmptyState.Visibility = _sharedFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.IsEnabled = available && !_scanning && !_downloading;
        ScanButton.Content = _scanning ? "탐색 중…" : "기기 찾기";
        DeviceCountText.Text = $"발견된 기기 {_devices.Count}대";
        DeviceEmptyState.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeviceEmptyState.Text = _scanning ? "주변 기기를 찾고 있습니다…" : "기기를 찾아보세요.\n상대 기기에서 공유를\n시작하면 여기에 표시됩니다.";
        DeviceList.IsEnabled = available && !_downloading;
        RefreshFilesButton.IsEnabled = available && DeviceList.SelectedItem != null && !_loadingFiles && !_downloading;
        RemoteFileList.IsEnabled = available && !_loadingFiles && !_downloading;
        RemoteEmptyState.Visibility = _remoteFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RemoteCountText.Text = _loadingFiles ? "파일 목록을 불러오는 중…" : DeviceList.SelectedItem == null
            ? "선택한 기기의 공유 파일이 표시됩니다." : $"{_remoteFiles.Count}개 파일 · {RemoteFileList.SelectedItems.Count}개 선택";
        ChooseFolderButton.IsEnabled = available && !_downloading;
        DownloadButton.IsEnabled = available && !_loadingFiles && !_downloading && DeviceList.SelectedItem != null && RemoteFileList.SelectedItems.Count > 0;
        DownloadButton.Content = _downloading ? "다운로드 중…" : "선택한 파일 다운로드";
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
        SocketException => "기기 탐색을 시작하지 못했습니다. 다른 WindowWise가 실행 중인지 또는 네트워크 연결 상태를 확인하세요.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => "파일이 더 이상 공유되지 않습니다. 목록을 새로고침하세요.",
        HttpRequestException => "상대 기기에 연결하지 못했습니다. 공유 상태와 방화벽 설정을 확인한 뒤 다시 시도하세요.",
        UnauthorizedAccessException => "파일 또는 저장 폴더에 접근할 권한이 없습니다.",
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
