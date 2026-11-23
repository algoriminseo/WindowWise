namespace WindowWise.Mobile.Pages;

// Design preview: joining and creating rooms are simulated until signaling is connected.
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
        await ShowProgressAsync("Connecting directly…", 1200);
        await OpenDropAsync();
    }

    private async void Create_Clicked(object? sender, EventArgs e)
    {
        CreateButton.IsEnabled = false;
        CreatedCode.Text = "7F3A 91C2 0B5E D4A8 66E1";
        CreatedCard.IsVisible = true;
        await ShowProgressAsync("Waiting for your PC to join…", 2500);
        await OpenDropAsync();
    }

    private async Task ShowProgressAsync(string text, int milliseconds)
    {
        JoinButton.IsEnabled = false;
        StatusText.Text = text;
        StatusRow.IsVisible = true;
        await Task.Delay(milliseconds);
    }

    private async Task OpenDropAsync()
    {
        Navigation.InsertPageBefore(new DropPage(), this);
        await Navigation.PopAsync(false);
    }

    private static Color Color(string key) => key.StartsWith('#')
        ? Microsoft.Maui.Graphics.Color.FromArgb(key)
        : (Color)Application.Current!.Resources[key];
}
