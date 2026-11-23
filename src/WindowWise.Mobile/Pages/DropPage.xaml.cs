using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using WindowWise.Mobile.Services;
using WindowWise.Models;
using WindowWise.Services;

namespace WindowWise.Mobile.Pages;

// Send: files this phone shares with the PC. Receive: files the PC shares, downloaded over WebRTC.
public partial class DropPage : ContentPage
{
    private readonly DropSession _session;
    private readonly WebRtcFileTransferService _files;
    private readonly ObservableCollection<FileItem> _shared = [];
    private readonly ObservableCollection<FileItem> _peer = [];
    private CancellationTokenSource? _receive;
    private FileItem? _receiving;
    private long _speedBytes;
    private readonly Stopwatch _speedClock = new();
    private bool _leaving;

    public DropPage(DropSession session)
    {
        InitializeComponent();
        _session = session;
        _files = session.Files!;
        BindableLayout.SetItemsSource(ShareList, _shared);
        BindableLayout.SetItemsSource(PeerList, _peer);
        _shared.CollectionChanged += (_, _) => UpdateShare();
        RouteLabel.Text = session.Relayed ? "Relay" : "Direct";
        LinkState.Text = "● Connected";
        _files.ProgressChanged += (_, progress) => MainThread.BeginInvokeOnMainThread(() => ShowProgress(progress));
        _files.RemoteFilesChanged += (_, _) => MainThread.BeginInvokeOnMainThread(() => _ = RefreshPeerAsync());
        session.Closed += (_, _) => MainThread.BeginInvokeOnMainThread(() => _ = LeaveAsync("Your PC disconnected."));
        UpdateShare();
        UpdateSelection();
        SelectTab(send: false);
        _ = RefreshPeerAsync();
    }

    // Android back leaves the room.
    protected override bool OnBackButtonPressed()
    {
        _ = LeaveAsync(null);
        return true;
    }

    private async Task LeaveAsync(string? reason)
    {
        if (_leaving) return;
        _leaving = true;
        _receive?.Cancel();
        DropSession.Current = null;
        await _session.DisposeAsync();
        if (reason is not null) await DisplayAlertAsync("Disconnected", reason, "OK");
        Navigation.InsertPageBefore(new ConnectPage(), this);
        await Navigation.PopAsync(false);
    }

    private void SendTab_Clicked(object? sender, EventArgs e) => SelectTab(send: true);
    private void ReceiveTab_Clicked(object? sender, EventArgs e) => SelectTab(send: false);

    private void SelectTab(bool send)
    {
        bool receiving = _receive is not null;
        SendPanel.IsVisible = send;
        ReceivePanel.IsVisible = !send && !receiving;
        TransferPanel.IsVisible = !send && receiving;
        ReceiveButton.IsVisible = !send && !receiving;
        CancelButton.IsVisible = !send && receiving;
        Style(SendTab, send);
        Style(ReceiveTab, !send);

        static void Style(Button tab, bool active)
        {
            tab.BackgroundColor = active ? Microsoft.Maui.Graphics.Color.FromArgb("#2E7FA2FF") : Colors.Transparent;
            tab.BorderColor = active ? Microsoft.Maui.Graphics.Color.FromArgb("#4D7FA2FF") : Colors.Transparent;
            tab.BorderWidth = active ? 1 : 0;
            tab.TextColor = active ? Color("Text") : Color("Faint");
        }
    }

    // --- Receive ---

    // Load the PC's share list, keeping selections for files that are still shared.
    private async Task RefreshPeerAsync()
    {
        try
        {
            var listed = await _files.RequestFilesAsync();
            var selected = _peer.Where(f => f.IsSelected).Select(f => f.Info!.Id).ToHashSet();
            _peer.Clear();
            foreach (var info in listed)
                _peer.Add(new FileItem(info.Name, info.Size, info) { IsSelected = selected.Contains(info.Id) });
            for (int i = 0; i < _peer.Count; i++) _peer[i].ShowDivider = i < _peer.Count - 1;
            PeerEmpty(_peer.Count == 0);
        }
        catch (Exception exception) when (!_leaving)
        {
            ShowMessage($"Could not load the PC's files: {exception.Message}", error: true);
        }
        UpdateSelection();
    }

    private void PeerEmpty(bool empty)
    {
        if (!empty) { if (DoneLabel.Text.StartsWith("Nothing", StringComparison.Ordinal)) DoneLabel.IsVisible = false; return; }
        ShowMessage("Nothing shared yet. Add files to the share list on your PC and start sharing.", error: false);
    }

    private void Peer_Tapped(object? sender, TappedEventArgs e)
    {
        if (_receive is not null || e.Parameter is not FileItem item) return;
        item.IsSelected = !item.IsSelected;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = _peer.Where(f => f.IsSelected).ToList();
        SelectionSummary.Text = $"{selected.Count} of {_peer.Count} selected";
        ReceiveButton.IsEnabled = selected.Count > 0;
        ReceiveButton.Text = selected.Count == 0 ? "Select files to receive"
            : $"↓  Receive {selected.Count} file{(selected.Count == 1 ? "" : "s")} · {FileItem.Format(selected.Sum(f => f.Size))}";
    }

    private async void Receive_Clicked(object? sender, EventArgs e)
    {
        var queue = _peer.Where(f => f.IsSelected).ToList();
        if (queue.Count == 0) return;
        using var receive = _receive = new CancellationTokenSource();
        SelectTab(send: false);
        RouteLabel.Text = "Receiving";
        TotalTile.Text = FileItem.Format(queue.Sum(f => f.Size));
        int saved = 0;
        string? failure = null;
        string folder = DownloadStore.WorkingFolder();
        for (int i = 0; i < queue.Count && !receive.IsCancellationRequested; i++)
        {
            var file = _receiving = queue[i];
            TransferName.Text = file.Name;
            FilesTile.Text = $"{i + 1} / {queue.Count}";
            ShowNext(i + 1 < queue.Count ? queue[i + 1] : null);
            ShowRing(0, file.Size);
            _speedBytes = 0;
            _speedClock.Restart();
            try
            {
                // The service retries and resumes on its own; a partial file from an earlier attempt is reused.
                var received = await _files.DownloadAsync(file.Info!, DownloadStore.AvailablePath(folder, file.Name), receive.Token);
                await DownloadStore.PublishAsync(received.Path);
                file.IsSelected = false;
                saved++;
            }
            catch (OperationCanceledException) when (receive.IsCancellationRequested) { }
            catch (Exception exception) { failure = $"{file.Name}: {exception.Message}"; break; }
        }
        _receive = null;
        _receiving = null;
        RouteLabel.Text = _session.Relayed ? "Relay" : "Direct";
        if (failure is not null) ShowMessage($"Download failed. {failure}", error: true);
        else if (receive.IsCancellationRequested)
            ShowMessage("Canceled. Downloading the same file again continues where it stopped.", error: false);
        else ShowMessage($"✓ Saved {saved} file{(saved == 1 ? "" : "s")} to Downloads/WindowWise", error: false, success: true);
        UpdateSelection();
        SelectTab(send: SendPanel.IsVisible);
    }

    private void Cancel_Clicked(object? sender, EventArgs e) => _receive?.Cancel();

    private void ShowProgress(RemoteTransferProgress progress)
    {
        if (progress.Receiving)
        {
            if (_receiving is null || progress.Name != _receiving.Name) return;
            if (progress.State == RemoteTransferState.Retrying)
            {
                SpeedLabel.Text = "Retrying…";
                return;
            }
            ShowRing(progress.Bytes, progress.Total);
            return;
        }
        // The PC is downloading one of this phone's shared files.
        var item = _shared.FirstOrDefault(f => f.Name == progress.Name);
        if (item is null) return;
        int percent = progress.Total == 0 ? 100 : (int)(progress.Bytes * 100 / progress.Total);
        item.Status = progress.State switch
        {
            RemoteTransferState.Sent => $"{item.SizeText} · sent",
            RemoteTransferState.Sending => $"PC is downloading · {percent}%",
            RemoteTransferState.Failed or RemoteTransferState.Canceled => $"{item.SizeText} · stopped",
            _ => item.Status
        };
    }

    private void ShowRing(long bytes, long total)
    {
        double fraction = total == 0 ? 1 : (double)bytes / total;
        Ring.Progress = fraction;
        PercentLabel.Text = ((int)(fraction * 100)).ToString();
        TransferBytes.Text = $"{FileItem.Format(bytes)} of {FileItem.Format(total)}";
        // Speed over the last second or more, so the number does not jump on every chunk.
        double seconds = _speedClock.Elapsed.TotalSeconds;
        if (seconds >= 1 && bytes > _speedBytes)
        {
            double perSecond = (bytes - _speedBytes) / seconds;
            SpeedLabel.Text = $"{FileItem.Format((long)perSecond)}/s";
            LeftTile.Text = perSecond > 0 ? FormatDuration((total - bytes) / perSecond) : "—";
            _speedBytes = bytes;
            _speedClock.Restart();
        }
        else if (_speedBytes == 0 && seconds < 1) { SpeedLabel.Text = "Starting…"; LeftTile.Text = "—"; }
    }

    private static string FormatDuration(double seconds) => seconds switch
    {
        < 60 => $"{Math.Ceiling(seconds)} s",
        < 3600 => $"{Math.Ceiling(seconds / 60)} min",
        _ => $"{seconds / 3600:0.0} h"
    };

    private void ShowNext(FileItem? next)
    {
        NextCard.IsVisible = next is not null;
        if (next is null) return;
        NextTile.BackgroundColor = next.TileFill;
        NextExt.Text = next.Extension;
        NextExt.TextColor = next.TileText;
        NextName.Text = next.Name;
        NextSize.Text = next.SizeText;
    }

    private void ShowMessage(string text, bool error, bool success = false)
    {
        DoneLabel.Text = text;
        DoneLabel.TextColor = error ? Microsoft.Maui.Graphics.Color.FromArgb("#F87171") : success ? Color("Success") : Color("Muted");
        DoneLabel.HorizontalTextAlignment = TextAlignment.Center;
        DoneLabel.IsVisible = true;
    }

    // --- Send ---

    private async void Add_Tapped(object? sender, TappedEventArgs e)
    {
        IEnumerable<FileResult?>? picked;
        try { picked = await FilePicker.Default.PickMultipleAsync(); }
        catch (Exception exception) { await DisplayAlertAsync("Could not add files", exception.Message, "OK"); return; }
        var paths = picked?.Where(f => f is not null).Select(f => f!.FullPath).ToList() ?? [];
        if (paths.Count == 0) return;
        ChangeShares(() =>
        {
            foreach (string path in paths)
            {
                var info = _session.Shares.Add(path);
                if (_shared.All(f => f.Info!.Id != info.Id)) _shared.Add(new FileItem(info.Name, info.Size, info));
            }
        });
    }

    private void Remove_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not FileItem item) return;
        ChangeShares(() =>
        {
            _session.Shares.Remove(item.Info!.Id);
            _shared.Remove(item);
        });
    }

    // The share list cannot change while shared, so pause sharing for the change. The PC refreshes its view.
    private void ChangeShares(Action change)
    {
        bool sharing = _session.Shares.IsSharing;
        _session.Shares.IsSharing = false;
        try { change(); }
        finally { _session.Shares.IsSharing = sharing; }
    }

    private void UpdateShare()
    {
        for (int i = 0; i < _shared.Count; i++) _shared[i].ShowDivider = i < _shared.Count - 1;
        ShareSummary.Text = $"{_shared.Count} file{(_shared.Count == 1 ? "" : "s")} · {FileItem.Format(_shared.Sum(f => f.Size))}";
    }

    private void Visible_Toggled(object? sender, ToggledEventArgs e)
    {
        _session.Shares.IsSharing = e.Value;
        VisibleHint.Text = e.Value ? "● Your PC can download these" : "Turn on so your PC can download these";
        VisibleHint.TextColor = e.Value ? Color("Success") : Color("Muted");
    }

    private static Color Color(string key) => (Color)Application.Current!.Resources[key];
}

// One file row. Colors follow the file type; selection drives the check circle.
public sealed class FileItem : INotifyPropertyChanged
{
    private static readonly Dictionary<string, string> TypeColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["XLS"] = "#4ADE80", ["XLSX"] = "#4ADE80", ["CSV"] = "#4ADE80",
        ["PPT"] = "#FB923C", ["PPTX"] = "#FB923C",
        ["DOC"] = "#7FA2FF", ["DOCX"] = "#7FA2FF", ["EXE"] = "#9FB8FF", ["MSI"] = "#9FB8FF",
        ["ZIP"] = "#FACC15", ["RAR"] = "#FACC15", ["7Z"] = "#FACC15",
        ["MP4"] = "#C084FC", ["MOV"] = "#C084FC", ["MP3"] = "#C084FC",
        ["JPG"] = "#FB7185", ["JPEG"] = "#FB7185", ["PNG"] = "#FB7185", ["HEIC"] = "#FB7185",
        ["PDF"] = "#F87171",
    };
    private bool _selected, _divider;
    private string? _status;

    public FileItem(string name, long size, SharedFileInfo? info = null)
    {
        Name = name;
        Size = size;
        Info = info;
        string ext = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
        Extension = ext.Length == 0 ? "FILE" : ext[..Math.Min(3, ext.Length)];
        string color = TypeColors.TryGetValue(ext, out var c) ? c : "#C9D2E4";
        TileText = Color.FromArgb(color);
        TileFill = TileText.WithAlpha(0.15f);
    }

    public string Name { get; }
    public long Size { get; }
    // The share-list entry this row stands for.
    public SharedFileInfo? Info { get; }
    public string SizeText => Format(Size);
    public string Extension { get; }
    public Color TileFill { get; }
    public Color TileText { get; }

    public string? Status { get => _status; set { _status = value; Changed(nameof(Subtitle)); Changed(nameof(SubtitleColor)); } }
    public string Subtitle => _status ?? SizeText;
    public Color SubtitleColor => _status?.StartsWith("PC is", StringComparison.Ordinal) == true ? Color.FromArgb("#A9C0FF") : Color.FromArgb("#8F9BB8");

    public bool ShowDivider { get => _divider; set { _divider = value; Changed(); } }
    public bool IsSelected
    {
        get => _selected;
        set { _selected = value; Changed(); Changed(nameof(CheckFill)); Changed(nameof(CheckStroke)); }
    }
    public Color CheckFill => _selected ? Color.FromArgb("#7FA2FF") : Colors.Transparent;
    public Color CheckStroke => _selected ? Color.FromArgb("#7FA2FF") : Color.FromArgb("#33FFFFFF");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));

    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B"
    };
}
