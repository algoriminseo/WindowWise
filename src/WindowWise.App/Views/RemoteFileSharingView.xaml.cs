using System.Collections.ObjectModel;
using System.IO;
using System.Net.WebSockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SIPSorcery.Net;
using WindowWise.Services;
using WindowWise.Localization;

namespace WindowWise.Views;

public partial class RemoteFileSharingView : UserControl, IAsyncDisposable
{
    private readonly ObservableCollection<string> _history = new();
    private readonly List<(DateTime Time, string Key, object[] Arguments)> _historyMessages = new();
    private RemoteSharingText _text = null!;
    private string _statusKey = "Ready";
    private object[] _statusArguments = [];
    private bool _statusError;
    private RemoteTransferProgress? _lastProgress;
    private Window? _testHost;
    private Exception? _connectionError;
    private readonly List<(Window Window, RemoteFileSharingView View)> _testWindows = new();
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private RemoteSignalingClient? _signaling;
    private WebRtcDataChannelService? _transport;
    private WebRtcFileTransferService? _files;
    private CancellationTokenSource? _connectionCancellation;
    private CancellationTokenSource? _sendCancellation;
    private Task _connectionTask = Task.CompletedTask;
    private Task _sendTask = Task.CompletedTask;
    private Task? _shutdownTask;
    private RemoteFileOffer? _offer;
    private Guid? _receivingId;
    private string? _selectedPath;
    private bool _connecting;
    private bool _sending;
    private bool _disconnecting;
    private bool _shuttingDown;

    public event EventHandler? BackRequested;

    // Initialize the remote sharing screen with an independent device ID.
    public RemoteFileSharingView()
    {
        InitializeComponent();
        _text = (RemoteSharingText)Resources["RemoteText"];
        DeviceBox.Text = $"{Environment.MachineName}-{Guid.NewGuid():N}"[..Math.Min(Environment.MachineName.Length + 7, 80)];
        HistoryList.ItemsSource = _history;
        UpdateControls();
    }

    // Switch this screen's catalog while preserving connection, progress, and history data.
    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_text is null || StatusText is null) return;
        _text.Language = LanguageBox.SelectedIndex == 1 ? "en" : "ko";
        if (_testHost is not null) _testHost.Title = _text["TestTitle"];
        RenderStatus();
        RenderHistory();
        if (_lastProgress is not null) RenderProgress(_lastProgress);
        if (_offer is not null) IncomingText.Text = $"{_offer.Name} · {FormatSize(_offer.Size)}";
        if (_selectedPath is not null && File.Exists(_selectedPath))
            SelectedDetail.Text = $"{FormatSize(new FileInfo(_selectedPath).Length)} · {_selectedPath}";
        UpdateControls();
    }

    // Return to the sharing selector while keeping the current session alive.
    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    // Start a connection operation and keep its task available for shutdown.
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_connecting || _transport is not null || _shuttingDown) return;
        await (_connectionTask = ConnectAsync());
    }

    // Connect signaling, configure direct ICE, and initiate or await a peer connection.
    private async Task ConnectAsync()
    {
        if (!Uri.TryCreate(ServerBox.Text.Trim(), UriKind.Absolute, out var server) || server.Scheme is not ("ws" or "wss"))
        { SetStatus("InvalidServer", true); return; }
        string device = DeviceBox.Text.Trim(), peer = PeerBox.Text.Trim(), room = RoomBox.Text.Trim();
        if (device.Length is 0 or > 128 || peer.Length is 0 or > 128 || room.Length is 0 or > 128 || device == peer)
        { SetStatus("InvalidIds", true); return; }
        string stun = StunBox.Text.Trim();
        if (stun.Length > 0 && !stun.StartsWith("stun:", StringComparison.OrdinalIgnoreCase))
        { SetStatus("InvalidStun", true); return; }
        // Leave discovery and relay servers unconfigured when the STUN field is empty.
        var config = new RTCConfiguration();
        if (stun.Length > 0) config.iceServers = [new RTCIceServer { urls = stun }];
        bool initiate = RoleBox.SelectedIndex == 0;
        _connecting = true;
        _connectionError = null;
        _connectionCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        UpdateControls();
        try
        {
            _signaling = new RemoteSignalingClient();
            var transport = _transport = new WebRtcDataChannelService(_signaling, peer, room, config);
            var files = _files = new WebRtcFileTransferService(transport);
            transport.Closed += (_, _) => Dispatch(transport, () =>
            {
                _offer = null;
                _receivingId = null;
                SetStatus("SessionClosed");
                UpdateControls();
            });
            transport.ErrorOccurred += (_, error) =>
            {
                _connectionError = error;
                Dispatch(transport, () => SetStatus("ConnectionFailed", true, error));
            };
            files.FileOffered += (_, offer) => Dispatch(transport, () =>
            {
                _offer = offer;
                IncomingText.Text = $"{offer.Name} · {FormatSize(offer.Size)}";
                SetStatus("OfferReceived");
                UpdateControls();
            });
            files.ProgressChanged += (_, progress) => Dispatch(transport, () => ShowProgress(progress));
            files.FileReceived += (_, received) => Dispatch(transport, () =>
            {
                AddHistory("ReceivedHistory", received.Name, FormatSize(received.Size), received.Path);
                SetStatus("Saved", false, received.Path);
            });
            files.ErrorOccurred += (_, error) => Dispatch(transport, () => SetStatus("FailedStatus", true, error));
            SetStatus("SignalingConnecting");
            await _signaling.ConnectAsync(server, device, _connectionCancellation.Token);
            SetStatus(initiate ? "PeerConnecting" : "PeerWaiting");
            if (initiate) await transport.StartAsync(_connectionCancellation.Token);
            await transport.WaitUntilOpenAsync(_connectionCancellation.Token);
            SetStatus("ConnectionReady");
        }
        catch (OperationCanceledException)
        {
            await CleanupAsync();
            if (!_disconnecting && !_shuttingDown)
            {
                if (_connectionError is not null) SetStatus("ConnectionFailed", true, _connectionError);
                else SetStatus("ConnectionTimeout", true);
            }
        }
        catch (Exception exception)
        {
            await CleanupAsync();
            SetStatus("ConnectionFailed", true, exception);
        }
        finally { _connecting = false; UpdateControls(); }
    }

    // Cancel pending operations and disconnect the current peer.
    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_disconnecting) return;
        _disconnecting = true;
        UpdateControls();
        try
        {
            _connectionCancellation?.Cancel();
            _sendCancellation?.Cancel();
            await Task.WhenAll(_connectionTask, _sendTask);
            await CleanupAsync();
            SetStatus("DisconnectedStatus");
        }
        catch (Exception exception) { SetStatus("FailedStatus", true, exception); }
        finally { _disconnecting = false; UpdateControls(); }
    }

    // Select a single file without reading its contents into memory.
    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = _text["ChooseDialog"], CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var file = new FileInfo(dialog.FileName);
            SelectedName.Text = file.Name;
            SelectedDetail.Text = $"{FormatSize(file.Length)} · {file.FullName}";
            SelectedDetail.ToolTip = file.FullName;
            _selectedPath = file.FullName;
            UpdateControls();
        }
        catch (Exception exception) { SetStatus("FailedStatus", true, exception); }
    }

    // Start a tracked send operation for the selected file.
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_files is null || _selectedPath is null || _sending) return;
        await (_sendTask = SendSelectedAsync(_files, _selectedPath));
    }

    // Keep send progress responsive and surface rejection, timeout, or cancellation.
    private async Task SendSelectedAsync(WebRtcFileTransferService files, string path)
    {
        _sending = true;
        _sendCancellation = new CancellationTokenSource();
        TransferProgress.Value = 0;
        UpdateControls();
        try
        {
            await files.SendFileAsync(path, _sendCancellation.Token);
            AddHistory("SentHistory", Path.GetFileName(path));
            SetStatus("Sent");
        }
        catch (OperationCanceledException) { SetStatus("CanceledStatus"); AddHistory("CanceledHistory", Path.GetFileName(path)); }
        catch (Exception exception) { SetStatus("FailedStatus", true, exception); AddHistory("FailedHistory", Path.GetFileName(path)); }
        finally { _sending = false; _sendCancellation.Dispose(); _sendCancellation = null; UpdateControls(); }
    }

    // Request a new destination name before approving the incoming file.
    private async void Accept_Click(object sender, RoutedEventArgs e)
    {
        var offer = _offer;
        var files = _files;
        if (offer is null || files is null) return;
        var dialog = new SaveFileDialog { Title = _text["SaveDialog"], FileName = offer.Name, OverwritePrompt = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (File.Exists(dialog.FileName)) { SetStatus("NoOverwrite", true); return; }
        AcceptButton.IsEnabled = RejectButton.IsEnabled = false;
        _receivingId = offer.Id;
        try
        {
            await files.AcceptAsync(offer.Id, dialog.FileName);
            if (_offer?.Id == offer.Id) _offer = null;
        }
        catch (Exception exception) { _receivingId = null; SetStatus("FailedStatus", true, exception); }
        finally { UpdateControls(); }
    }

    // Decline an incoming file without creating a destination file.
    private async void Reject_Click(object sender, RoutedEventArgs e)
    {
        if (_offer is null || _files is null) return;
        try { await _files.CancelIncomingAsync(_offer.Id); _offer = null; SetStatus("RejectedStatus"); }
        catch (Exception exception) { SetStatus("FailedStatus", true, exception); }
        finally { UpdateControls(); }
    }

    // Cancel an outgoing send or discard the current incoming partial file.
    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _sendCancellation?.Cancel();
        if (_receivingId is not Guid id || _files is null) return;
        try { await _files.CancelIncomingAsync(id); }
        catch (Exception exception) { SetStatus("FailedStatus", true, exception); }
    }

    // Display acknowledged progress and clear incoming UI state at a terminal phase.
    private void ShowProgress(RemoteTransferProgress progress)
    {
        _lastProgress = progress;
        TransferProgress.Value = progress.Total == 0 ? (progress.State is RemoteTransferState.Sent or RemoteTransferState.Received ? 100 : 0)
            : (double)progress.Bytes / progress.Total * 100;
        RenderProgress(progress);
        if (progress.Receiving && progress.State is RemoteTransferState.Received or RemoteTransferState.Canceled or RemoteTransferState.Failed)
        { _offer = null; _receivingId = null; }
        UpdateControls();
    }

    // Translate progress state codes independently of the peer's display language.
    private void RenderProgress(RemoteTransferProgress progress) => ProgressText.Text = _text.Format("Progress",
        _text[$"State.{progress.State}"], progress.Name, FormatSize(progress.Bytes), FormatSize(progress.Total));

    // Open a second real peer window with matching room settings for a local UI test.
    private void TestWindow_Click(object sender, RoutedEventArgs e)
    {
        var view = new RemoteFileSharingView();
        view.ServerBox.Text = ServerBox.Text;
        view.RoomBox.Text = RoomBox.Text;
        view.StunBox.Text = StunBox.Text;
        view.LanguageBox.SelectedIndex = LanguageBox.SelectedIndex;
        view.PeerBox.Text = DeviceBox.Text;
        view.RoleBox.SelectedIndex = 1;
        view.TestWindowButton.Visibility = view.BackButton.Visibility = Visibility.Collapsed;
        PeerBox.Text = view.DeviceBox.Text;
        RoleBox.SelectedIndex = 0;
        var window = new Window
        {
            Title = view._text["TestTitle"], Content = view, Width = 960, Height = 850,
            MinWidth = 720, MinHeight = 640, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        view._testHost = window;
        bool closing = false;
        window.Closing += async (_, args) =>
        {
            if (closing) return;
            args.Cancel = true;
            closing = true;
            try { await view.ShutdownAsync(); }
            finally
            {
                _testWindows.RemoveAll(item => item.Window == window);
                UpdateControls();
                window.Dispatcher.BeginInvoke(new Action(window.Close));
            }
        };
        _testWindows.Add((window, view));
        window.Show();
        SetStatus("TestHint");
        UpdateControls();
    }

    // Dispatch transport callbacks to WPF and ignore callbacks from an old session.
    private void Dispatch(WebRtcDataChannelService transport, Action action) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (!_shuttingDown && ReferenceEquals(_transport, transport)) action();
    }));

    // Keep a bounded, readable list of completed or failed transfers.
    private void AddHistory(string key, params object[] arguments)
    {
        _historyMessages.Insert(0, (DateTime.Now, key, arguments));
        if (_historyMessages.Count > 10) _historyMessages.RemoveAt(_historyMessages.Count - 1);
        RenderHistory();
    }

    // Rebuild display strings from stored history data when the language changes.
    private void RenderHistory()
    {
        _history.Clear();
        foreach (var item in _historyMessages)
            _history.Add($"{item.Time:HH:mm} · {_text.Format(item.Key, item.Arguments)}");
        HistoryEmpty.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Keep actions consistent with the current connection and transfer state.
    private void UpdateControls()
    {
        bool open = _transport?.IsOpen == true;
        bool idle = !_connecting && !_disconnecting && !_shuttingDown;
        ConnectionFields.IsEnabled = RoleBox.IsEnabled = NetworkFields.IsEnabled = idle && _transport is null;
        ConnectButton.IsEnabled = idle && _transport is null;
        DisconnectButton.IsEnabled = !_disconnecting && !_shuttingDown && (_transport is not null || _connecting);
        TestWindowButton.IsEnabled = idle && _transport is null && _testWindows.Count == 0;
        ChooseButton.IsEnabled = !_sending && !_shuttingDown;
        SendButton.IsEnabled = idle && open && !_sending && _offer is null && _receivingId is null && _selectedPath is not null;
        CancelButton.IsEnabled = !_shuttingDown && (_sending || _receivingId is not null);
        IncomingPanel.Visibility = _offer is not null ? Visibility.Visible : Visibility.Collapsed;
        AcceptButton.IsEnabled = RejectButton.IsEnabled = open && !_disconnecting;
        ConnectionBadge.Text = _text[open ? "Connected" : _connecting ? "Connecting" : "Disconnected"];
        ConnectionBadge.Foreground = (Brush)FindResource(open ? "PrimaryBrush" : "MutedTextBrush");
        RouteBadge.Text = open ? RouteLabel() : "WEBRTC";
        RouteText.Text = _text.Format("RouteSummary", RouteLabel());
        RouteText.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    // Show connection or transfer feedback using the existing theme colors.
    private void SetStatus(string key, bool error = false, params object[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        _statusError = error;
        RenderStatus();
    }

    // Translate the current status on demand, including the selected connection route.
    private void RenderStatus()
    {
        StatusText.Text = _statusKey == "ConnectionReady" ? _text.Format(_statusKey, RouteLabel())
            : _text.Format(_statusKey, _statusArguments.Select(argument => argument is Exception exception ? DescribeError(exception) : argument).ToArray());
        StatusText.Foreground = _statusError ? Brushes.Firebrick : (Brush)FindResource("MutedTextBrush");
    }

    // Report the nominated ICE route rather than inferring it from configured servers.
    private string RouteLabel() => _text[_transport?.ConnectionRoute switch
    {
        WebRtcConnectionRoute.Direct => "Direct", WebRtcConnectionRoute.Relayed => "Relayed", _ => "RouteUnknown"
    }];

    // Present localized error categories without displaying the peer's localized exception text.
    private string DescribeError(Exception exception) => exception switch
    {
        RemoteFileTransferException transfer when transfer.Code is "transfer-busy" or "offer-expired" or "destination-exists" or
            "receive-open-failed" or "receiver-canceled" or "receive-failed" or "peer-busy" or "remote-canceled" => _text[$"Error.{transfer.Code}"],
        TimeoutException => _text["Error.Timeout"],
        WebSocketException => _text["Error.Network"],
        IOException or UnauthorizedAccessException => _text["Error.File"],
        InvalidOperationException => _text["Error.Network"],
        _ => _text["Error.Unexpected"]
    };

    // Format file sizes for both progress and file metadata.
    private string FormatSize(long size) => size >= 1024 * 1024 * 1024 ? (size / (1024d * 1024 * 1024)).ToString("0.##", _text.Culture) + " GB"
        : size >= 1024 * 1024 ? (size / (1024d * 1024)).ToString("0.##", _text.Culture) + " MB"
        : size >= 1024 ? (size / 1024d).ToString("0.##", _text.Culture) + " KB" : $"{size} B";

    // Dispose file processing before the transport and its caller-owned signaling client.
    private async Task CleanupAsync()
    {
        await _cleanupGate.WaitAsync();
        try
        {
            var files = _files; var transport = _transport; var signaling = _signaling;
            _files = null; _transport = null; _signaling = null;
            _offer = null; _receivingId = null;
            try { if (files is not null) await files.DisposeAsync(); }
            finally
            {
                try { if (transport is not null) await transport.DisposeAsync(); }
                finally { if (signaling is not null) await signaling.DisposeAsync(); }
            }
        }
        finally { _cleanupGate.Release(); }
    }

    // Reuse one shutdown operation when the application or a test window closes.
    public Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    // Expose the same asynchronous cleanup to callers that own the view's lifetime.
    public ValueTask DisposeAsync() => new(ShutdownAsync());

    // Cancel transfers, close test peers, and release all connection resources.
    private async Task ShutdownCoreAsync()
    {
        _shuttingDown = true;
        _connectionCancellation?.Cancel();
        _sendCancellation?.Cancel();
        foreach (var item in _testWindows.ToArray()) { await item.View.ShutdownAsync(); item.Window.Close(); }
        await Task.WhenAll(_connectionTask, _sendTask);
        await CleanupAsync();
        UpdateControls();
    }
}
