using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowWise.Views;
using WindowWise.Services;
using System.Reflection;

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
        if (args.Contains("--login-only"))
        {
            LoginChecks.Run(args.FirstOrDefault(arg => arg != "--login-only"));
            return;
        }
        if (args.Contains("--account-only"))
        {
            AccountChecks.Run(args.FirstOrDefault(arg => arg != "--account-only"));
            return;
        }
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
        Check(((TextBlock)view.FindName("StunModeTitle")).Text == "Direct over internet" &&
            ((TextBlock)view.FindName("TurnModeTitle")).Text == "Through a server",
            "Connection choices show descriptive titles alongside protocol logos");
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
        Check(gather.Content as string == "Check connection environment", "ICE test action is translated");
        stunMode.IsChecked = true;
        Check(stunFields.Visibility == Visibility.Visible && turnFields.Visibility == Visibility.Collapsed &&
            !((Button)view.FindName("ConnectButton")).IsEnabled && gather.Visibility == Visibility.Collapsed,
            "STUN shows only its fields and blocks unimplemented connection actions");
        Render(view, args.FirstOrDefault(), "remote-sharing-stun-en.png");
        turnMode.IsChecked = true;
        Check(turnFields.Visibility == Visibility.Visible && stunFields.Visibility == Visibility.Collapsed &&
            ((Button)view.FindName("ConnectButton")).IsEnabled, "TURN selection shows credentials and enables connection actions");
        var automatic = (CheckBox)view.FindName("AutoTurnBox");
        Check(automatic.IsChecked == true && ((StackPanel)view.FindName("AutoTurnFields")).Visibility == Visibility.Visible &&
            ((StackPanel)view.FindName("ManualTurnFields")).Visibility == Visibility.Collapsed,
            "Automatic TURN credentials are the default and hide manual credentials");
        ((PasswordBox)view.FindName("TurnAccessKeyBox")).Password = "";
        ((TextBox)view.FindName("TurnApiBox")).Text = "http://localhost:5047/turn-credentials";
        gather.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(status.Text.Contains("Enter the test access key"), "Missing automatic access key is explained before networking");
        automatic.IsChecked = false;
        turn.Text = "turn:turn.example.com:3478?transport=udp";
        gather.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(status.Text.StartsWith("Enter the relay server login", StringComparison.Ordinal), "TURN requires credentials before networking");
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
        automatic.IsChecked = true;
        ((TextBox)view.FindName("TurnApiBox")).Text = "";
        Render(view, args.FirstOrDefault(), "remote-sharing-turn-auto-ko.png");
        VerifyIncomingRequestLifecycle(view);
        view.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine("PASS: Remote sharing WPF language checks completed.");
    }

    private static void VerifyIncomingRequestLifecycle(RemoteFileSharingView view)
    {
        var panel = (FrameworkElement)view.FindName("IncomingPanel");
        var text = (TextBlock)view.FindName("IncomingText");
        Guid id = Guid.NewGuid();
        Invoke("ShowIncomingOffer", new RemoteFileOffer(id, "received.txt", 8));
        Check(panel.Visibility == Visibility.Visible, "A new file offer shows the approval card");
        Invoke("ShowReceivedFile", new RemoteFileReceived(id, "received.txt", "C:\\received.txt", 8));
        Check(panel.Visibility == Visibility.Collapsed && text.Text.Length == 0,
            "Saved-file event dismisses the request without relying on progress events");
        Invoke("ShowIncomingOffer", new RemoteFileOffer(id, "receiving.txt", 8));
        Invoke("ShowProgress", new RemoteTransferProgress(id, "receiving.txt", true, 0, 8, RemoteTransferState.Receiving));
        Check(panel.Visibility == Visibility.Collapsed && !((Button)view.FindName("AcceptButton")).IsEnabled,
            "Approval controls disappear when receiving begins");
        Guid next = Guid.NewGuid();
        Invoke("ShowIncomingOffer", new RemoteFileOffer(next, "next.txt", 8));
        Invoke("ShowProgress", new RemoteTransferProgress(id, "receiving.txt", true, 8, 8, RemoteTransferState.Received));
        Check(panel.Visibility == Visibility.Visible && text.Text.Contains("next.txt"),
            "A late completion cannot dismiss a newer file request");
        foreach (var state in new[] { RemoteTransferState.Canceled, RemoteTransferState.Failed, RemoteTransferState.Received })
        {
            Invoke("ShowIncomingOffer", new RemoteFileOffer(next, "next.txt", 8));
            Invoke("ShowProgress", new RemoteTransferProgress(next, "next.txt", true, 0, 8, state));
            Check(panel.Visibility == Visibility.Collapsed, $"Request is dismissed on {state}");
        }

        void Invoke(string method, object argument) => typeof(RemoteFileSharingView)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, [argument]);
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
