using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WindowWise.Mobile.Pages;

// Design preview: lists hold sample files and transfers are simulated until WebRTC transfer is connected.
public partial class DropPage : ContentPage
{
    private readonly ObservableCollection<FileItem> _shared =
    [
        new("vacation-2026.mp4", 184_320_000),
        new("receipt-scan.jpg", 2_480_000),
        new("meeting-notes.pdf", 512_000),
    ];
    private readonly ObservableCollection<FileItem> _peer =
    [
        new("quarterly-report.xlsx", 3_420_000),
        new("presentation-final.pptx", 28_900_000),
        new("WindowWise-setup.exe", 74_100_000),
        new("photo-archive.zip", 412_000_000),
        new("readme.txt", 4_200),
    ];
    private CancellationTokenSource? _receive;
    private CancellationTokenSource? _serve;

    public DropPage()
    {
        InitializeComponent();
        BindableLayout.SetItemsSource(ShareList, _shared);
        BindableLayout.SetItemsSource(PeerList, _peer);
        _shared.CollectionChanged += (_, _) => UpdateShare();
        UpdateShare();
        UpdateSelection();
        SelectTab(send: false);
    }

    // Android back leaves the room.
    protected override bool OnBackButtonPressed()
    {
        _receive?.Cancel();
        _serve?.Cancel();
        Navigation.InsertPageBefore(new ConnectPage(), this);
        Navigation.PopAsync(false);
        return true;
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

    private void Peer_Tapped(object? sender, TappedEventArgs e)
    {
        if (_receive is not null || e.Parameter is not FileItem item) return;
        item.IsSelected = !item.IsSelected;
        DoneLabel.IsVisible = false;
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
        var files = _peer.Where(f => f.IsSelected).ToList();
        if (files.Count == 0) return;
        using var receive = _receive = new CancellationTokenSource();
        SelectTab(send: false);
        LinkLabelState("Receiving");
        long total = files.Sum(f => f.Size);
        TotalTile.Text = FileItem.Format(total);
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i];
                TransferName.Text = file.Name;
                FilesTile.Text = $"{i + 1} / {files.Count}";
                ShowNext(i + 1 < files.Count ? files[i + 1] : null);
                for (int percent = 0; percent <= 100; percent += 2)
                {
                    receive.Token.ThrowIfCancellationRequested();
                    long bytes = file.Size * percent / 100;
                    Ring.Progress = percent / 100.0;
                    PercentLabel.Text = percent.ToString();
                    SpeedLabel.Text = $"{2.1 + Random.Shared.NextDouble() * 0.6:0.0} MB/s";
                    TransferBytes.Text = $"{FileItem.Format(bytes)} of {FileItem.Format(file.Size)}";
                    LeftTile.Text = $"{Math.Max(1, (100 - percent) / 8)} s";
                    await Task.Delay(70, receive.Token);
                }
                file.IsSelected = false;
            }
            DoneLabel.Text = $"✓ Saved {files.Count} file{(files.Count == 1 ? "" : "s")} to Downloads";
        }
        catch (OperationCanceledException) { DoneLabel.Text = "Canceled. Downloading again continues where it stopped."; }
        finally
        {
            _receive = null;
            DoneLabel.IsVisible = true;
            LinkLabelState(null);
            UpdateSelection();
            SelectTab(send: SendPanel.IsVisible);
        }
    }

    private void Cancel_Clicked(object? sender, EventArgs e) => _receive?.Cancel();

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

    private void LinkLabelState(string? activity)
    {
        RouteLabel.Text = activity ?? "Direct · 4 ms";
        LinkState.Text = activity is null ? "● Connected" : "● Direct";
    }

    // --- Send ---

    private void Add_Tapped(object? sender, TappedEventArgs e) =>
        _shared.Add(new($"IMG_{Random.Shared.Next(1000, 9999)}.heic", Random.Shared.Next(1_000_000, 6_000_000)));

    private void Remove_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is FileItem item) _shared.Remove(item);
    }

    private void UpdateShare()
    {
        for (int i = 0; i < _shared.Count; i++) _shared[i].ShowDivider = i < _shared.Count - 1;
        for (int i = 0; i < _peer.Count; i++) _peer[i].ShowDivider = i < _peer.Count - 1;
        ShareSummary.Text = $"{_shared.Count} files · {FileItem.Format(_shared.Sum(f => f.Size))}";
    }

    private async void Visible_Toggled(object? sender, ToggledEventArgs e)
    {
        VisibleHint.Text = e.Value ? "● DESKTOP-PC can download these" : "Turn on so DESKTOP-PC can download these";
        VisibleHint.TextColor = e.Value ? Color("Success") : Color("Muted");
        _serve?.Cancel();
        if (!e.Value || _shared.Count == 0) return;
        // Simulate the PC downloading the first shared file.
        using var serve = _serve = new CancellationTokenSource();
        var file = _shared[0];
        try
        {
            await Task.Delay(1200, serve.Token);
            for (int percent = 0; percent <= 100; percent += 2)
            {
                file.Status = $"PC is downloading · {percent}%";
                await Task.Delay(80, serve.Token);
            }
            file.Status = $"{file.SizeText} · sent";
        }
        catch (OperationCanceledException) { file.Status = null; }
        finally { if (ReferenceEquals(_serve, serve)) _serve = null; }
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

    public FileItem(string name, long size)
    {
        Name = name;
        Size = size;
        string ext = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
        Extension = ext.Length == 0 ? "FILE" : ext[..Math.Min(3, ext.Length)];
        string color = TypeColors.TryGetValue(ext, out var c) ? c : "#C9D2E4";
        TileText = Color.FromArgb(color);
        TileFill = TileText.WithAlpha(0.15f);
    }

    public string Name { get; }
    public long Size { get; }
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
