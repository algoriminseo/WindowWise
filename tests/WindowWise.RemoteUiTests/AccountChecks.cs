using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowWise.Controls;
using WindowWise.Services;
using WindowWise.Views;

internal static class AccountChecks
{
    public static void Run(string? previewDirectory)
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "account-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var preferences = new AccountPreferences(Path.Combine(directory, "preferences.json"));
        var auth = new TestAccount();
        var menu = new AccountMenu();
        menu.Configure(auth, preferences);
        var button = (Button)menu.FindName("AccountButton");
        var dropdown = button.ContextMenu;
        Check(((TextBlock)menu.FindName("AccountEmail")).Text == auth.Email,
            "Signed-in header displays the account email");
        var alerts = (MenuItem)((MenuItem)dropdown.Items[1]).Items[0];
        alerts.IsChecked = false;
        alerts.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(!preferences.AudioDeviceAlertsEnabled &&
            !new AccountPreferences(Path.Combine(directory, "preferences.json")).AudioDeviceAlertsEnabled,
            "Alert preference survives a new preferences instance");
        bool signOutRequested = false;
        menu.SignOutRequested += (_, _) => signOutRequested = true;
        ((MenuItem)dropdown.Items[3]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(signOutRequested, "Sign out dispatches the account action");

        var settings = new AccountSettingsWindow(auth, preferences);
        Check(((TextBlock)settings.FindName("ProfileEmail")).Text == auth.Email &&
            ((CheckBox)settings.FindName("AudioAlertsBox")).IsChecked == false,
            "Account settings shows the profile and saved alert preference");

        if (previewDirectory is not null)
        {
            var header = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 231, 238)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(20, 8, 20, 8),
                Child = menu
            };
            menu.HorizontalAlignment = HorizontalAlignment.Right;
            Render(header, previewDirectory, "account-header.png", 800, 64);
            Render(dropdown, previewDirectory, "account-dropdown.png", 250, 125);
            Render((FrameworkElement)settings.Content, previewDirectory, "account-settings.png", 470, 460);
        }
        settings.Close();

        auth.IsAuthenticated = false;
        menu.Refresh();
        Check(((TextBlock)menu.FindName("AccountEmail")).Text == "Sign in",
            "Signed-out header clears the previous email");
        bool signInRequested = false;
        menu.SignInRequested += (_, _) => signInRequested = true;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(signInRequested, "Signed-out account button requests sign-in");
        Console.WriteLine("PASS: Account checks completed.");
    }

    private static void Render(FrameworkElement element, string directory, string name, int width, int height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(244, 246, 248)), null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class TestAccount : IAuthService
    {
        public bool IsAuthenticated { get; set; } = true;
        public string? Email => IsAuthenticated ? "alex@example.com" : null;
        public string? DisplayName => "Alex";
        public string? UserId => "test-user";
        public Task SignInAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("test-token");
        public Task SignOutAsync(CancellationToken cancellationToken) { IsAuthenticated = false; return Task.CompletedTask; }
    }
}
