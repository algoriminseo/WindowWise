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
using WindowWise.Models;

namespace WindowWise.Views;

/// <summary>
/// Connect connection settings, TURN credential issuance, approval, and progress to the view state.
/// Services implement the transfer protocol; Dispatch marshals their events to the UI thread.
/// </summary>
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
    private string? _shareableRoomCode;
    private WebRtcDataChannelService? _transport;
    private WebRtcFileTransferService? _files;
    private CancellationTokenSource? _connectionCancellation;
    private Task _connectionTask = Task.CompletedTask;
    private Task? _shutdownTask;
    private bool _connecting;
    // Files offered to the peer survive reconnects; the peer's list is cleared whenever the session ends.
    private readonly RemoteShareList _shares = new();
    private readonly ObservableCollection<FileRow> _mySharedFiles = new();
    private readonly ObservableCollection<FileRow> _peerFiles = new();
    private string _downloadFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    private bool _downloading;
    private bool _loadingPeerFiles;
    private bool _receiveMode;
    private CancellationTokenSource? _downloadCancellation;
    private bool _disconnecting;
    private bool _shuttingDown;
    private bool _gatheringOnly;
    private string _diagnosticStage = "Ready";
    private string _diagnosticSnapshot = "";
    private string _diagnosticSettings = "";
    private string _diagnosticError = "";
    private string[] _diagnosticSecrets = [];
    private readonly System.Windows.Threading.DispatcherTimer _diagnosticTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public event EventHandler? BackRequested;

    // Supplies the signed-in user's access token for TURN issuance and signaling. Null means nobody is signed in.
    public Func<CancellationToken, Task<string>>? AccessTokenProvider { get; set; }

    // Use the configured signaling URL when one is supplied through the environment.
    public RemoteFileSharingView()
    {
        InitializeComponent();
        _text = (RemoteSharingText)Resources["RemoteText"];
        ServerBox.Text = Environment.GetEnvironmentVariable("WINDOWWISE_SIGNAL_URL") ?? ServerBox.Text;
        HistoryList.ItemsSource = _history;
        MySharedList.ItemsSource = _mySharedFiles;
        PeerFileList.ItemsSource = _peerFiles;
        DownloadFolderText.Text = _downloadFolder;
        DownloadFolderText.ToolTip = _downloadFolder;
        RenderShareState();
        TurnApiBox.Text = Environment.GetEnvironmentVariable("WINDOWWISE_TURN_CREDENTIALS_URL") ?? "";
        _diagnosticTimer.Tick += (_, _) => RefreshDiagnostics();
        RefreshDiagnostics();
        UpdateControls();
    }

    // Direct mode adds STUN only when an address is entered; without one it finds same-network paths only.
    private bool UsesStun => TurnModeBox.IsChecked != true && StunBox.Text.Trim().Length > 0;

    // Share this entry point between connection and environment checks. Fetch fresh credentials automatically
    // or validate manual inputs.
    // TURN mode with relay-only off also tries direct paths first, because the TURN server reports this PC's
    // public address.
    private async Task<RTCConfiguration?> ReadIceConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (UsesStun) return IceServerSettings.CreateStun(StunBox.Text);
            bool turn = TurnModeBox.IsChecked == true;
            if (turn && AutoTurnBox.IsChecked == true)
            {
                var endpoint = TurnCredentialsClient.ResolveEndpoint(TurnApiBox.Text, ServerBox.Text);
                _diagnosticSettings += $"Credential endpoint: {endpoint.GetLeftPart(UriPartial.Path)}\n";
                string accessToken = await GetAccessTokenAsync(cancellationToken);
                _diagnosticSecrets = [.. _diagnosticSecrets, accessToken];
                _diagnosticStage = "TurnFetching";
                SetStatus("TurnFetching");
                var result = await new TurnCredentialsClient().FetchAsync(endpoint,
                    accessToken, RelayOnlyBox.IsChecked == true, cancellationToken);
                // Retain session secrets separately so editing inputs later cannot expose the issued password
                // in diagnostics.
                _diagnosticSecrets = _diagnosticSecrets.Concat(result.Secrets).Distinct().ToArray();
                if (result.ExpiresAtUtc is { } expires)
                    _diagnosticSettings += $"TURN expires UTC: {expires:O}\n";
                return result.Configuration;
            }
            return IceServerSettings.Create(turn, TurnBox.Text,
                TurnUsernameBox.Text, TurnCredentialBox.Password, turn && RelayOnlyBox.IsChecked == true);
        }
        catch (ArgumentException exception) { SetStatus(exception.Message, true); return null; }
    }

    // An expired or missing sign-in surfaces as a localized error instead of a raw exception.
    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (AccessTokenProvider is null) throw new TurnCredentialsException("turn-sign-in-required");
        try { return await AccessTokenProvider(cancellationToken); }
        catch (InvalidOperationException) { throw new TurnCredentialsException("turn-sign-in-required"); }
    }

    private void TurnSource_Changed(object sender, RoutedEventArgs e)
    {
        if (_text is null || ManualTurnFields is null) return;
        UpdateControls();
    }

    private void NetworkMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_text is null || TurnFields is null) return;
        UpdateControls();
    }

    // Reset diagnostics for the new attempt and collect credentials that must be redacted.
    private void BeginDiagnostics(string stage)
    {
        _diagnosticStage = stage;
        _diagnosticSnapshot = "";
        _diagnosticError = "";
        _diagnosticSecrets = [TurnCredentialBox.Password, TurnUsernameBox.Text.Trim()];
        bool turn = TurnModeBox.IsChecked == true;
        string stun = UsesStun ? StunBox.Text.Trim().ReplaceLineEndings(", ") : "not used";
        _diagnosticSettings = $"UTC: {DateTime.UtcNow:O}\nSTUN: {stun}\nTURN enabled: {turn}\n" +
            $"ICE policy: {(turn && RelayOnlyBox.IsChecked == true ? "relay" : "all")}\n";
        DiagnosticsExpander.IsExpanded = true;
        _diagnosticTimer.Start();
        RefreshDiagnostics();
    }

    private void RefreshDiagnostics()
    {
        if (_transport is not null) _diagnosticSnapshot = _transport.GetDiagnostics();
        string report = _diagnosticSettings + _text[_diagnosticStage] + "\n" + _diagnosticSnapshot +
            (_diagnosticError.Length == 0 ? "" : "\nError: " + _diagnosticError);
        // Library errors may contain credentials. Redact them before displaying or exporting diagnostics.
        foreach (string secret in _diagnosticSecrets.Where(value => value.Length > 0))
            report = report.Replace(secret, "[redacted]", StringComparison.Ordinal);
        if (DiagnosticsBox.Text != report) DiagnosticsBox.Text = report;
    }

    private async void GatherIce_Click(object sender, RoutedEventArgs e)
    {
        if (_connecting || _transport is not null || _shuttingDown) return;
        await (_connectionTask = GatherIceAsync());
    }

    // Candidate gathering is separate from peer connectivity. Dispose its channel afterward but retain results
    // in the view.
    // The overall time limit also includes the credential-issuance request.
    private async Task GatherIceAsync()
    {
        _connecting = _gatheringOnly = true;
        _connectionCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        BeginDiagnostics("IceGathering");
        UpdateControls();
        try
        {
            var config = await ReadIceConfigurationAsync(_connectionCancellation.Token);
            if (config is null) return;
            _diagnosticStage = "IceGathering";
            SetStatus("IceGathering");
            _signaling = new RemoteSignalingClient();
            _transport = new WebRtcDataChannelService(_signaling, "gather-only", "gather-only", config);
            await _transport.GatherLocalCandidatesAsync(_connectionCancellation.Token);
            _diagnosticStage = "IceGathered";
            SetStatus("IceGathered");
        }
        catch (OperationCanceledException) { _diagnosticStage = "IceGatherTimeout"; SetStatus("IceGatherTimeout"); }
        catch (Exception exception)
        {
            _diagnosticStage = "IceConnectionFailed";
            _diagnosticError = exception.Message;
            SetStatus("ConnectionFailed", true, exception);
        }
        finally
        {
            await CleanupAsync();
            _connecting = _gatheringOnly = false;
            RefreshDiagnostics();
            UpdateControls();
        }
    }

    private void SaveDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        RefreshDiagnostics();
        var dialog = new SaveFileDialog { Filter = "Text (*.txt)|*.txt", FileName = $"windowwise-ice-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { File.WriteAllText(dialog.FileName, DiagnosticsBox.Text); }
        catch (Exception exception) { SetStatus("FailedStatus", true, exception); }
    }

    // Preserve session state and inputs while rerendering this window's translations, progress, and history.
    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_text is null || StatusText is null) return;
        _text.Language = LanguageBox.SelectedIndex == 1 ? "en" : "ko";
        if (_testHost is not null) _testHost.Title = _text["TestTitle"];
        RenderStatus();
        RenderHistory();
        if (_lastProgress is not null) RenderProgress(_lastProgress);
        RenderMySharedFiles();
        for (int i = 0; i < _peerFiles.Count; i++)
            _peerFiles[i] = _peerFiles[i] with { SizeText = FormatSize(_peerFiles[i].File.Size) };
        UpdateControls();
        RefreshDiagnostics();
    }

    // Returning to the sharing selector preserves the session. Disconnect or application shutdown performs
    // cleanup.
    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    // Start a connection attempt that requests a new room code.
    private async void CreateRoom_Click(object sender, RoutedEventArgs e) =>
        await BeginRoomConnectionAsync(create: true);

    // Start a connection attempt using the room code entered by the user.
    private async void Connect_Click(object sender, RoutedEventArgs e) =>
        await BeginRoomConnectionAsync(create: false);

    // Prevent overlapping attempts and track the task so shutdown can wait for it.
    private async Task BeginRoomConnectionAsync(bool create)
    {
        if (_connecting || _transport is not null || _shuttingDown) return;
        await (_connectionTask = ConnectAsync(create));
    }

    // Copy the invitation code issued for the room hosted by this window.
    private void CopyRoom_Click(object sender, RoutedEventArgs e)
    {
        if (_shareableRoomCode is null) return;
        try { Clipboard.SetText(_shareableRoomCode); }
        catch (System.Runtime.InteropServices.ExternalException) { SetStatus("RoomCopyFailed", true); }
    }

    // Pair through signaling before creating the WebRTC transport, then wait for both peers to be ready.
    private async Task ConnectAsync(bool create)
    {
        if (!Uri.TryCreate(ServerBox.Text.Trim(), UriKind.Absolute, out var server) ||
            server.Scheme is not ("ws" or "wss") || server.UserInfo.Length > 0 ||
            server.Fragment.Length > 0)
        { SetStatus("InvalidServer", true); return; }
        if (server.Scheme == "ws" && !server.IsLoopback)
        { SetStatus("RoomSecureServer", true); return; }

        string code = RoomPairingSession.NormalizeCode(RoomBox.Text);
        if (!create && !RoomPairingSession.IsValidCode(code))
        { SetStatus("InvalidRoomCode", true); return; }

        _connecting = true;
        _connectionError = null;
        var connectionTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        _connectionCancellation = connectionTimeout;
        CancellationToken token = connectionTimeout.Token;
        BeginDiagnostics("SignalingConnecting");
        UpdateControls();
        try
        {
            string accessToken = await GetAccessTokenAsync(token);
            _diagnosticSecrets = [.. _diagnosticSecrets, accessToken, code];
            var signaling = _signaling = new RemoteSignalingClient();
            using var pairing = new RoomPairingSession(signaling);
            SetStatus("SignalingConnecting");
            await signaling.ConnectAsync(server, Guid.NewGuid().ToString("N"), token, accessToken);

            RoomEntry entry = await pairing.JoinAsync(create ? null : code, token);
            RoomBox.Text = entry.Code;
            _diagnosticSecrets = [.. _diagnosticSecrets, entry.Code];
            _shareableRoomCode = create ? entry.Code : null;
            _diagnosticStage = "RoomWaiting";
            SetStatus("RoomWaiting");
            UpdateControls();

            RoomMatch match = await pairing.WaitForPeerAsync(token);
            connectionTimeout.CancelAfter(TimeSpan.FromMinutes(2));
            var config = await ReadIceConfigurationAsync(token);
            if (config is null)
            {
                await CleanupAsync();
                return;
            }

            var transport = _transport = new WebRtcDataChannelService(
                signaling, match.PeerId, entry.SessionId, config);
            var files = _files = new WebRtcFileTransferService(transport, _shares);
            transport.Closed += (_, _) => Dispatch(transport, () =>
            {
                _diagnosticStage = "SessionClosed";
                SetStatus("SessionClosed");
                UpdateControls();
                if (!_connecting && !_disconnecting)
                    _ = CleanupClosedRoomAsync();
            });
            transport.ErrorOccurred += (_, error) =>
            {
                _connectionError = error;
                Dispatch(transport, () =>
                {
                    _diagnosticStage = "IceConnectionFailed";
                    _diagnosticError = error.Message;
                    SetStatus("ConnectionFailed", true, error);
                });
            };
            // Only downloads this peer asked for are accepted; refuse direct offers from older app versions.
            files.FileOffered += (_, offer) => _ = files.CancelIncomingAsync(offer.Id);
            files.ProgressChanged += (_, progress) => Dispatch(transport, () => ShowProgress(progress));
            files.FileReceived += (_, received) => Dispatch(transport, () => ShowReceivedFile(received));
            files.ErrorOccurred += (_, error) => Dispatch(transport, () => SetStatus("FailedStatus", true, error));
            files.RemoteFilesChanged += (_, _) => Dispatch(transport, () => _ = RefreshPeerFilesAsync());
            _diagnosticStage = "PeerConnecting";
            SetStatus("PeerConnecting");
            await pairing.ReadyAsync(entry.SessionId, token);
            if (match.Initiate) await transport.StartAsync(token);
            await transport.WaitUntilOpenAsync(token);
            connectionTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
            SetStatus("ConnectionReady");
            _ = RefreshPeerFilesAsync();
            _diagnosticStage = "Connected";
        }
        catch (OperationCanceledException)
        {
            _diagnosticStage = "ConnectionTimeout";
            await CleanupAsync();
            if (!_disconnecting && !_shuttingDown)
            {
                if (_connectionError is not null) SetStatus("ConnectionFailed", true, _connectionError);
                else SetStatus("ConnectionTimeout", true);
            }
        }
        catch (Exception exception)
        {
            _diagnosticStage = "IceConnectionFailed";
            _diagnosticError = exception.Message;
            await CleanupAsync();
            SetStatus("ConnectionFailed", true, exception);
        }
        finally { _connecting = false; RefreshDiagnostics(); UpdateControls(); }
    }

    // Cancel an active download and release connection resources after the room closes.
    private async Task CleanupClosedRoomAsync()
    {
        _disconnecting = true;
        UpdateControls();
        try
        {
            _downloadCancellation?.Cancel();
            await CleanupAsync();
        }
        catch (Exception exception) { SetStatus("ConnectionFailed", true, exception); }
        finally { _disconnecting = false; UpdateControls(); }
    }

    // Cancel connection waits and file transfers, then release network resources after pending tasks finish.
    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_disconnecting) return;
        _disconnecting = true;
        UpdateControls();
        try
        {
            _connectionCancellation?.Cancel();
            _downloadCancellation?.Cancel();
            await _connectionTask;
            await CleanupAsync();
            _diagnosticStage = "DisconnectedStatus";
            RefreshDiagnostics();
            SetStatus("DisconnectedStatus");
        }
        catch (Exception exception) { SetStatus("FailedStatus", true, exception); }
        finally { _disconnecting = false; UpdateControls(); }
    }

    // Stop the current download. The service removes the incomplete file.
    private void Cancel_Click(object sender, RoutedEventArgs e) => _downloadCancellation?.Cancel();

    private void ShowReceivedFile(RemoteFileReceived received)
    {
        AddHistory("ReceivedHistory", received.Name, FormatSize(received.Size), received.Path);
        SetStatus("Saved", false, received.Path);
        UpdateControls();
    }

    // Show receive progress in the receive panel and send progress in the share panel.
    private void ShowProgress(RemoteTransferProgress progress)
    {
        _lastProgress = progress;
        var bar = progress.Receiving ? ReceiveProgressBar : ShareProgressBar;
        bar.Value = progress.Total == 0 ? (progress.State is RemoteTransferState.Sent or RemoteTransferState.Received ? 100 : 0)
            : (double)progress.Bytes / progress.Total * 100;
        RenderProgress(progress);
        if (!progress.Receiving && progress.State == RemoteTransferState.Sent)
            AddHistory("SentHistory", progress.Name);
        UpdateControls();
    }

    // Translate protocol state codes using this window's language, independently of the peer's display
    // language.
    private void RenderProgress(RemoteTransferProgress progress) =>
        (progress.Receiving ? ReceiveProgressText : ShareProgressText).Text = _text.Format("Progress",
            _text[$"State.{progress.State}"], progress.Name, FormatSize(progress.Bytes), FormatSize(progress.Total));

    // Open a second window with matching settings so it can join the host room locally.
    private void TestWindow_Click(object sender, RoutedEventArgs e)
    {
        var view = new RemoteFileSharingView();
        view.ServerBox.Text = ServerBox.Text;
        view.RoomBox.Text = RoomBox.Text;
        view.StunBox.Text = StunBox.Text;
        view.DirectModeBox.IsChecked = DirectModeBox.IsChecked;
        view.TurnModeBox.IsChecked = TurnModeBox.IsChecked;
        view.TurnBox.Text = TurnBox.Text;
        view.TurnUsernameBox.Text = TurnUsernameBox.Text;
        view.TurnCredentialBox.Password = TurnCredentialBox.Password;
        view.AutoTurnBox.IsChecked = AutoTurnBox.IsChecked;
        view.TurnApiBox.Text = TurnApiBox.Text;
        view.AccessTokenProvider = AccessTokenProvider;
        view.RelayOnlyBox.IsChecked = RelayOnlyBox.IsChecked;
        view.LanguageBox.SelectedIndex = LanguageBox.SelectedIndex;
        view.TestWindowButton.Visibility = view.BackButton.Visibility = Visibility.Collapsed;
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

    // Switch between the share and receive panels. Unlike local sharing, receiving does not stop sharing,
    // because both directions use the same connection.
    private void ShareMode_Click(object sender, RoutedEventArgs e) { _receiveMode = false; UpdateControls(); }

    private void ReceiveMode_Click(object sender, RoutedEventArgs e)
    {
        _receiveMode = true;
        UpdateControls();
        _ = RefreshPeerFilesAsync();
    }

    // The peer sees the list only while sharing; it is told about every start and stop.
    private void ToggleSharing_Click(object sender, RoutedEventArgs e)
    {
        _shares.IsSharing = !_shares.IsSharing;
        RenderShareState();
        UpdateControls();
    }

    // Add files to the share list while sharing is stopped.
    private void AddShared_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = _text["AddShared"], Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            foreach (string path in dialog.FileNames) _shares.Add(path);
        }
        catch (Exception exception) { SetStatus("FailedStatus", true, exception); }
        RenderMySharedFiles();
    }

    private void RemoveShared_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in MySharedList.SelectedItems.Cast<FileRow>().ToArray()) _shares.Remove(row.File.Id);
        RenderMySharedFiles();
    }

    private async void RefreshPeer_Click(object sender, RoutedEventArgs e) => await RefreshPeerFilesAsync();

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RenderShareState();
        UpdateControls();
    }

    // Replace the peer's list with a fresh copy. Selections are kept for files that are still shared.
    private async Task RefreshPeerFilesAsync()
    {
        var files = _files;
        if (files is null || _loadingPeerFiles || _transport?.IsOpen != true) return;
        _loadingPeerFiles = true;
        UpdateControls();
        try
        {
            var selected = PeerFileList.SelectedItems.Cast<FileRow>().Select(row => row.File.Id).ToHashSet();
            var listed = await files.RequestFilesAsync();
            if (!ReferenceEquals(files, _files)) return;
            _peerFiles.Clear();
            foreach (var file in listed) _peerFiles.Add(new FileRow(file, "", FormatSize(file.Size)));
            foreach (var row in _peerFiles.Where(row => selected.Contains(row.File.Id))) PeerFileList.SelectedItems.Add(row);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetStatus("FailedStatus", true, exception);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _loadingPeerFiles = false;
            RenderShareState();
            UpdateControls();
        }
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = _text["SaveFolder"], InitialDirectory = _downloadFolder };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _downloadFolder = dialog.FolderName;
        DownloadFolderText.Text = _downloadFolder;
        DownloadFolderText.ToolTip = _downloadFolder;
    }

    // Download the selected files one at a time. Existing files are never overwritten; a numbered name is used.
    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var files = _files;
        var selected = PeerFileList.SelectedItems.Cast<FileRow>().Select(row => row.File).ToArray();
        if (files is null || selected.Length == 0 || _downloading) return;
        _downloading = true;
        _downloadCancellation = new CancellationTokenSource();
        UpdateControls();
        int completed = 0;
        try
        {
            Directory.CreateDirectory(_downloadFolder);
            foreach (var file in selected)
            {
                SetStatus("DownloadingStatus", false, completed + 1, selected.Length, file.Name);
                await files.DownloadAsync(file, AvailablePath(_downloadFolder, file.Name), _downloadCancellation.Token);
                completed++;
            }
            SetStatus("DownloadedStatus", false, completed, _downloadFolder);
        }
        catch (OperationCanceledException) { SetStatus("CanceledStatus"); }
        catch (Exception exception) { SetStatus("FailedStatus", true, exception); }
        finally
        {
            _downloading = false;
            _downloadCancellation.Dispose();
            _downloadCancellation = null;
            UpdateControls();
        }
    }

    // Pick "name (1).ext", "name (2).ext", ... when the save folder already has a file with that name.
    private static string AvailablePath(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
        for (int number = 1; File.Exists(path); number++) path = Path.Combine(folder, $"{stem} ({number}){extension}");
        return path;
    }

    private void RenderMySharedFiles()
    {
        _mySharedFiles.Clear();
        foreach (var (file, path) in _shares.Entries) _mySharedFiles.Add(new FileRow(file, path, FormatSize(file.Size)));
        RenderShareState();
        UpdateControls();
    }

    // Refresh counts, empty-state hints, and the sharing bar in the current language.
    private void RenderShareState()
    {
        bool open = _transport?.IsOpen == true;
        bool sharing = _shares.IsSharing;
        ShareCountText.Text = _text.Format("ShareTotal", _mySharedFiles.Count, FormatSize(_mySharedFiles.Sum(row => row.File.Size)));
        ShareEmptyState.Visibility = _mySharedFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SharingStateText.Text = _text[sharing ? "SharingActive" : "SharingStopped"];
        SharingStateText.Foreground = (Brush)FindResource(sharing ? "PrimaryBrush" : "TextBrush");
        SharingHintText.Text = _text[sharing ? "SharingActiveHint" : "SharingStoppedHint"];
        SharingButton.Content = _text[sharing ? "StopSharing" : "StartSharing"];
        PeerFileCount.Text = open ? _text.Format("PeerCount", _peerFiles.Count, PeerFileList.SelectedItems.Count) : _text["PeerFilesHint"];
        PeerEmptyText.Text = _text[open ? "PeerEmpty" : "PeerFilesHint"];
        PeerEmptyState.Visibility = _peerFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Marshal background events to the UI thread and ignore callbacks from an old session after reconnection.
    private void Dispatch(WebRtcDataChannelService transport, Action action) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (!_shuttingDown && ReferenceEquals(_transport, transport)) action();
    }));

    // Keep at most ten recent entries so history cannot grow indefinitely in the view or memory.
    private void AddHistory(string key, params object[] arguments)
    {
        _historyMessages.Insert(0, (DateTime.Now, key, arguments));
        if (_historyMessages.Count > 10) _historyMessages.RemoveAt(_historyMessages.Count - 1);
        RenderHistory();
    }

    // Store untranslated message keys and arguments so existing history can be translated again when language
    // changes.
    private void RenderHistory()
    {
        _history.Clear();
        foreach (var item in _historyMessages)
            _history.Add($"{item.Time:HH:mm} · {_text.Format(item.Key, item.Arguments)}");
        HistoryEmpty.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Keep room actions and file controls in sync with the current connection state.
    private void UpdateControls()
    {
        bool open = _transport?.IsOpen == true;
        bool idle = !_connecting && !_disconnecting && !_shuttingDown;
        bool turn = TurnModeBox.IsChecked == true;
        StunFields.Visibility = turn ? Visibility.Collapsed : Visibility.Visible;
        TurnFields.Visibility = turn ? Visibility.Visible : Visibility.Collapsed;
        AutoTurnFields.Visibility = AutoTurnBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ManualTurnFields.Visibility = AutoTurnBox.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        ConnectionFields.IsEnabled = ServerBox.IsEnabled = NetworkFields.IsEnabled = idle && _transport is null;
        CreateRoomButton.IsEnabled = ConnectButton.IsEnabled = idle && _transport is null;
        CopyRoomButton.IsEnabled = _shareableRoomCode is not null && !_disconnecting && !_shuttingDown;
        GatherButton.IsEnabled = idle && _transport is null;
        DisconnectButton.IsEnabled = !_disconnecting && !_shuttingDown && (_transport is not null || _connecting);
        TestWindowButton.IsEnabled = (idle || _shareableRoomCode is not null) && !_disconnecting && !_shuttingDown && _transport is null && _testWindows.Count == 0;
        bool sharing = _shares.IsSharing;
        AddSharedButton.IsEnabled = !sharing;
        RemoveSharedButton.IsEnabled = !sharing && MySharedList.SelectedItems.Count > 0;
        SharingButton.IsEnabled = sharing || _mySharedFiles.Count > 0;
        ShareModeButton.Background = !_receiveMode ? (Brush)new BrushConverter().ConvertFromString("#EEF4FF")! : Brushes.White;
        ReceiveModeButton.Background = _receiveMode ? (Brush)new BrushConverter().ConvertFromString("#EEF4FF")! : Brushes.White;
        ShareModeButton.BorderBrush = (Brush)FindResource(!_receiveMode ? "PrimaryBrush" : "BorderBrush");
        ReceiveModeButton.BorderBrush = (Brush)FindResource(_receiveMode ? "PrimaryBrush" : "BorderBrush");
        SharePanel.Visibility = _receiveMode ? Visibility.Collapsed : Visibility.Visible;
        ReceivePanel.Visibility = _receiveMode ? Visibility.Visible : Visibility.Collapsed;
        CancelDownloadButton.Visibility = _downloading ? Visibility.Visible : Visibility.Collapsed;
        RefreshPeerButton.IsEnabled = idle && open && !_loadingPeerFiles;
        DownloadButton.IsEnabled = idle && open && !_downloading &&
            PeerFileList.SelectedItems.Count > 0;
        ChooseFolderButton.IsEnabled = !_downloading;
        ConnectionBadge.Text = _text[open ? "Connected" : _gatheringOnly ? "IceGathering" : _connecting ? "Connecting" : "Disconnected"];
        ConnectionBadge.Foreground = (Brush)FindResource(open ? "PrimaryBrush" : "MutedTextBrush");
        RouteBadge.Text = open ? RouteLabel() : "WEBRTC";
        RouteText.Text = _text.Format("RouteSummary", RouteLabel());
        RouteText.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    // Store status as a message key and use existing theme colors to distinguish errors.
    private void SetStatus(string key, bool error = false, params object[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        _statusError = error;
        RenderStatus();
    }

    // Rerender the current status when language changes. Show user-facing error descriptions instead of raw
    // exceptions.
    private void RenderStatus()
    {
        StatusText.Text = _statusKey == "ConnectionReady" ? _text.Format(_statusKey, RouteLabel())
            : _text.Format(_statusKey, _statusArguments.Select(argument => argument is Exception exception ? DescribeError(exception) : argument).ToArray());
        StatusText.Foreground = _statusError ? Brushes.Firebrick : (Brush)FindResource("MutedTextBrush");
    }

    // Show the actual nominated ICE route instead of inferring relay use from TURN configuration.
    private string RouteLabel() => _text[_transport?.ConnectionRoute switch
    {
        WebRtcConnectionRoute.Direct => "Direct", WebRtcConnectionRoute.Relayed => "Relayed", _ => "RouteUnknown"
    }];

    // Translate room and transport errors into messages in the selected language.
    private string DescribeError(Exception exception) => exception switch
    {
        TurnCredentialsException turn => _text[$"Error.{turn.Code}"],
        RemoteFileTransferException transfer when transfer.Code is "transfer-busy" or "offer-expired" or "destination-exists" or
            "receive-open-failed" or "receiver-canceled" or "receive-failed" or "peer-busy" or "remote-canceled" or
            "not-shared" or "invalid-list" or "send-failed" => _text[$"Error.{transfer.Code}"],
        RoomPairingException room => _text["Error." + room.Code],
        TimeoutException => _text["Error.Timeout"],
        WebSocketException => _text["Error.Network"],
        IOException or UnauthorizedAccessException => _text["Error.File"],
        InvalidOperationException => _text["Error.Network"],
        _ => _text["Error.Unexpected"]
    };

    // Use consistent size units and the current language's number format for metadata and progress.
    private string FormatSize(long size) => size >= 1024 * 1024 * 1024 ? (size / (1024d * 1024 * 1024)).ToString("0.##", _text.Culture) + " GB"
        : size >= 1024 * 1024 ? (size / (1024d * 1024)).ToString("0.##", _text.Culture) + " MB"
        : size >= 1024 ? (size / 1024d).ToString("0.##", _text.Culture) + " KB" : $"{size} B";

    // Clear the invitation and dispose file transfer, WebRTC, and signaling in dependency order.
    private async Task CleanupAsync()
    {
        await _cleanupGate.WaitAsync();
        try
        {
            var files = _files; var transport = _transport; var signaling = _signaling;
            RefreshDiagnostics();
            _diagnosticTimer.Stop();
            _connectionCancellation?.Dispose();
            _connectionCancellation = null;
            _files = null; _transport = null; _signaling = null;
            _shareableRoomCode = null;
            _peerFiles.Clear();
            RenderShareState();
            try { if (files is not null) await files.DisposeAsync(); }
            finally
            {
                try { if (transport is not null) await transport.DisposeAsync(); }
                finally { if (signaling is not null) await signaling.DisposeAsync(); }
            }
        }
        finally { _cleanupGate.Release(); }
    }

    // Share a single cleanup task when application shutdown overlaps with window closing.
    public Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    // Use the same shutdown path when the view owner calls asynchronous Dispose.
    public ValueTask DisposeAsync() => new(ShutdownAsync());

    // Cancel connection and file-transfer work first, then await pending tasks and close test peers.
    private async Task ShutdownCoreAsync()
    {
        _shuttingDown = true;
        _connectionCancellation?.Cancel();
        _downloadCancellation?.Cancel();
        foreach (var item in _testWindows.ToArray()) { await item.View.ShutdownAsync(); item.Window.Close(); }
        await _connectionTask;
        await CleanupAsync();
        UpdateControls();
    }

    // A file shown in either share list. Path is empty for the peer's files, which never reveal local paths.
    private sealed record FileRow(SharedFileInfo File, string Path, string SizeText)
    {
        public string Name => File.Name;
    }
}
