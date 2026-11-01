using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowWise.Views;

internal static class Program
{
    // Verify real WPF bindings without starting the app's clipboard/audio services or showing a window.
    [STAThread]
    private static void Main(string[] args)
    {
        var application = new Application();
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/WindowWise;component/Themes/WindowWiseTheme.xaml")
        });
        var view = new RemoteFileSharingView();
        var language = (ComboBox)view.FindName("LanguageBox");
        var peer = (TextBox)view.FindName("PeerBox");
        var send = (Button)view.FindName("SendButton");
        var status = (TextBlock)view.FindName("StatusText");
        string deviceId = ((TextBox)view.FindName("DeviceBox")).Text;
        Check(send.Content as string == "파일 보내기", "Korean is available on the remote screen");
        language.SelectedIndex = 1;
        DrainBindings();
        Check(send.Content as string == "Send file", "English updates the real WPF button binding");
        Check(((Button)view.FindName("BackButton")).Content as string == "← Sharing modes", "Back navigation is translated");
        Check(((TextBox)view.FindName("StunBox")).Text.Length == 0, "No STUN server is configured by default");
        var directMode = (RadioButton)view.FindName("DirectModeBox");
        var stunMode = (RadioButton)view.FindName("StunModeBox");
        var turnMode = (RadioButton)view.FindName("TurnModeBox");
        var stunFields = (StackPanel)view.FindName("StunFields");
        var turnFields = (StackPanel)view.FindName("TurnFields");
        var turn = (TextBox)view.FindName("TurnBox");
        var username = (TextBox)view.FindName("TurnUsernameBox");
        var credential = (PasswordBox)view.FindName("TurnCredentialBox");
        var relayOnly = (CheckBox)view.FindName("RelayOnlyBox");
        Check(directMode.IsChecked == true && turnFields.Visibility == Visibility.Collapsed && stunFields.Visibility == Visibility.Collapsed && credential.Password.Length == 0,
            "TURN defaults to disabled with no saved credentials");
        Check(!((Button)view.FindName("StunTestButton")).IsEnabled, "STUN test is a UI-only placeholder");
        ((TextBox)view.FindName("StunBox")).Text = "not-applied-stun-setting";
        var gather = (Button)view.FindName("GatherButton");
        var diagnostics = (TextBox)view.FindName("DiagnosticsBox");
        Check(gather.Content as string == "Test connection path", "ICE test action is translated");
        stunMode.IsChecked = true;
        Check(stunFields.Visibility == Visibility.Visible && turnFields.Visibility == Visibility.Collapsed &&
            !((Button)view.FindName("ConnectButton")).IsEnabled && gather.Visibility == Visibility.Collapsed,
            "STUN shows only its fields and blocks unimplemented connection actions");
        Render(view, args.FirstOrDefault(), "remote-sharing-stun-en.png");
        turnMode.IsChecked = true;
        Check(turnFields.Visibility == Visibility.Visible && stunFields.Visibility == Visibility.Collapsed &&
            ((Button)view.FindName("ConnectButton")).IsEnabled, "TURN selection shows credentials and enables connection actions");
        turn.Text = "turn:turn.example.com:3478?transport=udp";
        gather.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(status.Text.StartsWith("Enter the TURN username", StringComparison.Ordinal), "TURN requires credentials before networking");
        username.Text = "test-user";
        credential.Password = "test-secret";
        language.SelectedIndex = 0;
        language.SelectedIndex = 1;
        DrainBindings();
        Check(turn.IsEnabled && username.Text == "test-user" && credential.Password == "test-secret",
            "Language switching preserves TURN inputs");
        directMode.IsChecked = true;
        relayOnly.IsChecked = true; // A hidden TURN-only option must not affect Direct mode.
        gather.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!gather.IsEnabled && DateTime.UtcNow < deadline)
        {
            DrainBindings();
            Thread.Sleep(20);
        }
        DrainBindings();
        Check(gather.IsEnabled && diagnostics.Text.Contains(" typ host") && diagnostics.Text.Contains("Gathering: complete"),
            "Standalone UI test gathers and retains real ICE candidates");
        Check(!send.IsEnabled, "Standalone ICE test does not enable file sending");
        Check(diagnostics.Text.Contains("STUN: UI only / not applied") && !diagnostics.Text.Contains("not-applied-stun-setting"),
            "STUN input is not applied to candidate gathering");
        Check(!diagnostics.Text.Contains("test-secret") && !diagnostics.Text.Contains("test-user"),
            "Diagnostics exclude TURN credentials");
        Check(peer.Text.Length == 0 && ((TextBox)view.FindName("DeviceBox")).Text == deviceId,
            "Language switching preserves device and peer inputs");
        ((Button)view.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        DrainBindings();
        Check(status.Text.StartsWith("Enter different local", StringComparison.Ordinal), "Validation errors render in English");
        Render(view, args.FirstOrDefault(), "remote-sharing-en.png");
        language.SelectedIndex = 0;
        DrainBindings();
        Check(send.Content as string == "파일 보내기" && status.Text.StartsWith("서로 다른", StringComparison.Ordinal),
            "Switching back translates the existing validation status");
        Check(diagnostics.Text.Contains("후보 수집 완료") && diagnostics.Text.Contains(" typ host"),
            "Language switching preserves candidate diagnostics");
        Render(view, args.FirstOrDefault(), "remote-sharing-ko.png");
        stunMode.IsChecked = true;
        Render(view, args.FirstOrDefault(), "remote-sharing-stun-ko.png");
        turnMode.IsChecked = true;
        Check(turn.Text.Contains("turn.example.com") && credential.Password == "test-secret",
            "Changing connection methods preserves entered settings");
        Render(view, args.FirstOrDefault(), "remote-sharing-turn-ko.png");
        view.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine("PASS: Remote sharing WPF language checks completed.");
    }

    // Let WPF process indexer change notifications before checking displayed text.
    private static void DrainBindings() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    // Render optional preview images without manipulating the user's desktop windows.
    private static void Render(FrameworkElement view, string? directory, string filename)
    {
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        view.Measure(new Size(960, 1050));
        view.Arrange(new Rect(0, 0, 960, 1050));
        view.UpdateLayout();
        DrainBindings();
        var bitmap = new RenderTargetBitmap(960, 1050, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, filename));
        encoder.Save(output);
    }

    // Exit with a failure if an actual screen binding does not match the selected language.
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {description}");
        Console.WriteLine($"PASS: {description}");
    }
}
