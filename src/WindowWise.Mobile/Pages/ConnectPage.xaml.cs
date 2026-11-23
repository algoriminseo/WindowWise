using WindowWise.Mobile.Services;
using WindowWise.Services;

namespace WindowWise.Mobile.Pages;

// Join the room the PC created, or create one here and show its code for the PC.
public partial class ConnectPage : ContentPage
{
    private readonly (Border Box, Label Text)[] _groups = new (Border, Label)[5];

    public ConnectPage()
    {
        InitializeComponent();
        for (int i = 0; i < _groups.Length; i++)
        {
            var text = new Label { FontFamily = "monospace", FontSize = 15, FontAttributes = FontAttributes.Bold,
                CharacterSpacing = 1, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
            var box = new Border { Style = (Style)Application.Current!.Resources["GlassCard"], StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 }, Content = text };
            Grid.SetColumn(box, i);
            CodeBoxes.Add(box);
            _groups[i] = (box, text);
        }
        RenderCode();
    }

    private void Code_TextChanged(object? sender, TextChangedEventArgs e)
    {
        RenderCode();
        JoinButton.IsEnabled = (CodeEntry.Text ?? "").Length == 20;
    }

    private void Code_FocusChanged(object? sender, FocusEventArgs e) => RenderCode();

    // Fill each box with its four characters and highlight the box being typed into.
    private void RenderCode()
    {
        string code = (CodeEntry.Text ?? "").ToUpperInvariant();
        int active = Math.Min(code.Length / 4, 4);
        for (int i = 0; i < _groups.Length; i++)
        {
            string part = code.Length > i * 4 ? code.Substring(i * 4, Math.Min(4, code.Length - i * 4)) : "";
            _groups[i].Text.Text = part.Length > 0 ? part : "····";
            _groups[i].Text.TextColor = part.Length > 0 ? Color("Text") : Color("#4A5674");
            bool focus = CodeEntry.IsFocused && i == active;
            _groups[i].Box.Stroke = focus ? Color("Primary") : Color("GlassStroke");
            _groups[i].Box.StrokeThickness = focus ? 1.5 : 1;
        }
    }

    private async void Join_Clicked(object? sender, EventArgs e)
    {
        CodeEntry.Unfocus();
        string code = RoomPairingSession.NormalizeCode(CodeEntry.Text ?? "");
        if (!RoomPairingSession.IsValidCode(code)) { ShowError("Room codes use only 0–9 and A–F."); return; }
        await ConnectAsync(code);
    }

    private async void Create_Clicked(object? sender, EventArgs e) => await ConnectAsync(null);

    // Join (code) or create (null) a room, then open the Drop screen once the PC is connected.
    private async Task ConnectAsync(string? code)
    {
        JoinButton.IsEnabled = CreateButton.IsEnabled = CodeEntry.IsEnabled = false;
        CreatedCard.IsVisible = false;
        var session = new DropSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            await session.ConnectAsync(code, ShowStage, created => MainThread.BeginInvokeOnMainThread(() =>
            {
                CreatedCode.Text = string.Join(" ", Enumerable.Range(0, 5).Select(i => created.Substring(i * 4, 4)));
                CreatedCard.IsVisible = true;
            }), timeout.Token);
            DropSession.Current = session;
            Navigation.InsertPageBefore(new DropPage(session), this);
            await Navigation.PopAsync(false);
        }
        catch (Exception exception)
        {
            await session.DisposeAsync();
            ShowError(exception switch
            {
                OperationCanceledException => "Timed out waiting for the PC.",
                RoomPairingException { Code: "room-not-found" } => "No room with that code. Check the code on your PC.",
                RoomPairingException room => $"Could not join the room ({room.Code}).",
                WebRtcConnectionAttemptException => "Could not reach your PC directly or through the relay.",
                InvalidOperationException when !MobileAuth.Current.IsSignedIn => "Your sign-in expired. Restart the app.",
                _ => $"Connection failed: {exception.Message}"
            });
            CreateButton.IsEnabled = CodeEntry.IsEnabled = true;
            JoinButton.IsEnabled = (CodeEntry.Text ?? "").Length == 20;
        }
    }

    private void ShowStage(string text) => MainThread.BeginInvokeOnMainThread(() =>
    {
        Busy.IsVisible = true;
        StatusText.TextColor = Color("Muted");
        StatusText.Text = text;
        StatusRow.IsVisible = true;
    });

    private void ShowError(string text)
    {
        Busy.IsVisible = false;
        StatusText.TextColor = Color("#F87171");
        StatusText.Text = text;
        StatusRow.IsVisible = true;
    }

    private static Color Color(string key) => key.StartsWith('#')
        ? Microsoft.Maui.Graphics.Color.FromArgb(key)
        : (Color)Application.Current!.Resources[key];
}
