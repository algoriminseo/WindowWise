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

    // Give each window an independent device ID so local test windows are treated as distinct peers.
    public RemoteFileSharingView()
    {
        InitializeComponent();
        _text = (RemoteSharingText)Resources["RemoteText"];
        DeviceBox.Text = $"{Environment.MachineName}-{Guid.NewGuid():N}"[..Math.Min(Environment.MachineName.Length + 7, 80)];
        HistoryList.ItemsSource = _history;
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

    private void BeginDiagnostics(string stage)
    {
        _diagnosticStage = stage;
        _diagnosticSnapshot = "";
        _diagnosticError = "";
        _diagnosticSecrets = [TurnCredentialBox.Password, TurnUsernameBox.Text.Trim()];
        bool turn = TurnModeBox.IsChecked == true;
        string stun = UsesStun ? StunBox.Text.Trim().ReplaceLineEndings(", ") : "not used";
        _diagnosticSettings = $"UTC: {DateTime.UtcNow:O}\nSTUN: {stun}\nTURN enabled: {turn}\n" +
            $"ICE policy: {(turn && RelayOnlyBox.IsChecked == true ? "relay" : "all")}\nRole: {(RoleBox.SelectedIndex == 0 ? "Initiate" : "Listen")}\n";
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
        if (_offer is not null) IncomingText.Text = $"{_offer.Name} · {FormatSize(_offer.Size)}";
        if (_selectedPath is not null && File.Exists(_selectedPath))
            SelectedDetail.Text = $"{FormatSize(new FileInfo(_selectedPath).Length)} · {_selectedPath}";
        UpdateControls();
        RefreshDiagnostics();
    }

    // Returning to the sharing selector preserves the session. Disconnect or application shutdown performs
    // cleanup.
    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    // Track the connection task so window shutdown can wait for pending network work.
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_connecting || _transport is not null || _shuttingDown) return;
        await (_connectionTask = ConnectAsync());
    }

    // Prepare the selected ICE configuration, connect signaling, then initiate or await the peer connection.
    private async Task ConnectAsync()
    {
        if (!Uri.TryCreate(ServerBox.Text.Trim(), UriKind.Absolute, out var server) || server.Scheme is not ("ws" or "wss"))
        { SetStatus("InvalidServer", true); return; }
        string device = DeviceBox.Text.Trim(), peer = PeerBox.Text.Trim(), room = RoomBox.Text.Trim();
        if (device.Length is 0 or > 128 || peer.Length is 0 or > 128 || room.Length is 0 or > 128 || device == peer)
        { SetStatus("InvalidIds", true); return; }
        bool initiate = RoleBox.SelectedIndex == 0;
        _connecting = true;
        _connectionError = null;
        _connectionCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        BeginDiagnostics("SignalingConnecting");
        UpdateControls();
        try
        {
            var config = await ReadIceConfigurationAsync(_connectionCancellation.Token);
            if (config is null) return;
            _diagnosticStage = "SignalingConnecting";
            _signaling = new RemoteSignalingClient();
            var transport = _transport = new WebRtcDataChannelService(_signaling, peer, room, config);
            var files = _files = new WebRtcFileTransferService(transport);
            transport.Closed += (_, _) => Dispatch(transport, () =>
            {
                _diagnosticStage = "SessionClosed";
                _offer = null;
                _receivingId = null;
                SetStatus("SessionClosed");
                UpdateControls();
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
            files.FileOffered += (_, offer) => Dispatch(transport, () => ShowIncomingOffer(offer));
            files.ProgressChanged += (_, progress) => Dispatch(transport, () => ShowProgress(progress));
            files.FileReceived += (_, received) => Dispatch(transport, () => ShowReceivedFile(received));
            files.ErrorOccurred += (_, error) => Dispatch(transport, () => SetStatus("FailedStatus", true, error));
            SetStatus("SignalingConnecting");
            string? signalingToken = null;
            if (AccessTokenProvider is not null)
            {
                try { signalingToken = await AccessTokenProvider(_connectionCancellation.Token); }
                catch (InvalidOperationException) { } // /signal does not require sign-in yet.
            }
            await _signaling.ConnectAsync(server, device, _connectionCancellation.Token, signalingToken);
            _diagnosticStage = initiate ? "PeerConnecting" : "PeerWaiting";
            SetStatus(initiate ? "PeerConnecting" : "PeerWaiting");
            if (initiate) await transport.StartAsync(_connectionCancellation.Token);
            await transport.WaitUntilOpenAsync(_connectionCancellation.Token);
            SetStatus("ConnectionReady");
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

    // Cancel connection waits and file transfers, then release network resources after pending tasks finish.
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
            _diagnosticStage = "DisconnectedStatus";
            RefreshDiagnostics();
            SetStatus("DisconnectedStatus");
        }
        catch (Exception exception) { SetStatus("FailedStatus", true, exception); }
        finally { _disconnecting = false; UpdateControls(); }
    }

    // Select one file at a time. The transfer service streams its contents in chunks.
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

    // Track the selected file's send task to prevent duplicate sends and coordinate shutdown.
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_files is null || _selectedPath is null || _sending) return;
        await (_sendTask = SendSelectedAsync(_files, _selectedPath));
    }

    // Keep the UI responsive and reflect peer rejection, timeout, and cancellation in status and history.
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

    // Approve receipt only after destination selection. Preserve existing destination files.
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
        // After approval, hide the request card and use progress/cancel controls. The service call does not
        // wait for the full download.
        UpdateControls();
        try
        {
            await files.AcceptAsync(offer.Id, dialog.FileName);
            ClearIncomingRequest(offer.Id, finished: false);
        }
        catch (Exception exception)
        {
            if (exception is RemoteFileTransferException { Code: "destination-exists" })
            {
                if (_receivingId == offer.Id) _receivingId = null;
            }
            else ClearIncomingRequest(offer.Id);
            SetStatus("FailedStatus", true, exception);
        }
        finally { UpdateControls(); }
    }

    // Reject a pending request. No receive file exists yet because approval has not occurred.
    private async void Reject_Click(object sender, RoutedEventArgs e)
    {
        var offer = _offer;
        var files = _files;
        if (offer is null || files is null) return;
        try { await files.CancelIncomingAsync(offer.Id); ClearIncomingRequest(offer.Id); SetStatus("RejectedStatus"); }
        catch (Exception exception) { SetStatus("FailedStatus", true, exception); }
        finally { UpdateControls(); }
    }

    // Cancel an outgoing transfer or remove incoming partial data. This is separate from rejecting an
    // unapproved offer.
    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _sendCancellation?.Cancel();
        if (_receivingId is not Guid id || _files is null) return;
        try { await _files.CancelIncomingAsync(id); }
        catch (Exception exception) { SetStatus("FailedStatus", true, exception); }
    }

    // Show filename, size, and approval controls only for an offer that is still pending.
    private void ShowIncomingOffer(RemoteFileOffer offer)
    {
        _offer = offer;
        IncomingText.Text = $"{offer.Name} · {FormatSize(offer.Size)}";
        SetStatus("OfferReceived");
        UpdateControls();
    }

    // Dismiss the request on the saved-file event as well as progress, so a missed progress callback cannot
    // leave a stale card.
    private void ShowReceivedFile(RemoteFileReceived received)
    {
        ClearIncomingRequest(received.Id);
        AddHistory("ReceivedHistory", received.Name, FormatSize(received.Size), received.Path);
        SetStatus("Saved", false, received.Path);
        UpdateControls();
    }

    // Clear only matching transfer IDs so a late completion/cancellation cannot dismiss the next file's
    // request.
    // finished=false closes only the approval card. Retain the receiving ID to keep cancellation available
    // during transfer.
    private void ClearIncomingRequest(Guid id, bool finished = true)
    {
        if (_offer?.Id == id)
        {
            _offer = null;
            IncomingText.Text = "";
        }
        if (finished && _receivingId == id) _receivingId = null;
    }

    // Hide approval controls when receiving starts; clear the active receiving ID only on completion,
    // cancellation, or failure.
    private void ShowProgress(RemoteTransferProgress progress)
    {
        _lastProgress = progress;
        TransferProgress.Value = progress.Total == 0 ? (progress.State is RemoteTransferState.Sent or RemoteTransferState.Received ? 100 : 0)
            : (double)progress.Bytes / progress.Total * 100;
        RenderProgress(progress);
        if (progress.Receiving)
        {
            if (progress.State == RemoteTransferState.Receiving && _offer?.Id == progress.Id)
            {
                _receivingId = progress.Id;
                ClearIncomingRequest(progress.Id, finished: false);
            }
            else if (progress.State is RemoteTransferState.Received or RemoteTransferState.Canceled or RemoteTransferState.Failed)
                ClearIncomingRequest(progress.Id);
        }
        UpdateControls();
    }

    // Translate protocol state codes using this window's language, independently of the peer's display
    // language.
    private void RenderProgress(RemoteTransferProgress progress) => ProgressText.Text = _text.Format("Progress",
        _text[$"State.{progress.State}"], progress.Name, FormatSize(progress.Bytes), FormatSize(progress.Total));

    // Open a second peer window with the same server/room and swapped device IDs for local bidirectional
    // testing.
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

    // Compute enabled actions and visible panels together so connection, transfer, and shutdown states remain
    // consistent.
    private void UpdateControls()
    {
        bool open = _transport?.IsOpen == true;
        bool idle = !_connecting && !_disconnecting && !_shuttingDown;
        bool turn = TurnModeBox.IsChecked == true;
        StunFields.Visibility = turn ? Visibility.Collapsed : Visibility.Visible;
        TurnFields.Visibility = turn ? Visibility.Visible : Visibility.Collapsed;
        AutoTurnFields.Visibility = AutoTurnBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ManualTurnFields.Visibility = AutoTurnBox.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        ConnectionFields.IsEnabled = RoleBox.IsEnabled = NetworkFields.IsEnabled = idle && _transport is null;
        ConnectButton.IsEnabled = idle && _transport is null;
        GatherButton.IsEnabled = idle && _transport is null;
        DisconnectButton.IsEnabled = !_disconnecting && !_shuttingDown && (_transport is not null || _connecting);
        TestWindowButton.IsEnabled = idle && _transport is null && _testWindows.Count == 0;
        ChooseButton.IsEnabled = !_sending && !_shuttingDown;
        SendButton.IsEnabled = idle && open && !_sending && _offer is null && _receivingId is null && _selectedPath is not null;
        CancelButton.IsEnabled = !_shuttingDown && (_sending || _receivingId is not null);
        bool pendingApproval = _offer is not null && _receivingId != _offer.Id;
        IncomingPanel.Visibility = pendingApproval ? Visibility.Visible : Visibility.Collapsed;
        AcceptButton.IsEnabled = RejectButton.IsEnabled = pendingApproval && open && !_disconnecting;
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

    // Map network, file, and issuance failures to stable codes with localized descriptions.
    private string DescribeError(Exception exception) => exception switch
    {
        TurnCredentialsException turn => _text[$"Error.{turn.Code}"],
        RemoteFileTransferException transfer when transfer.Code is "transfer-busy" or "offer-expired" or "destination-exists" or
            "receive-open-failed" or "receiver-canceled" or "receive-failed" or "peer-busy" or "remote-canceled" => _text[$"Error.{transfer.Code}"],
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

    // Dispose file processing before the DataChannel and signaling client so services cannot use an
    // already-closed channel.
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

    // Share a single cleanup task when application shutdown overlaps with window closing.
    public Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    // Use the same shutdown path when the view owner calls asynchronous Dispose.
    public ValueTask DisposeAsync() => new(ShutdownAsync());

    // Cancel connection and file-transfer work first, then await pending tasks and close test peers.
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
