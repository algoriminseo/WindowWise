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
        var room = (TextBox)view.FindName("RoomBox");
        room.Text = "partial-code";
        var download = (Button)view.FindName("DownloadButton");
        var status = (TextBlock)view.FindName("StatusText");
        Check(download.Content as string == "Download selected files", "English is the default on the remote screen");
        language.SelectedIndex = 0;
        DrainBindings();
        Check(download.Content as string == "선택한 파일 받기", "Korean is available on the remote screen");
        language.SelectedIndex = 1;
        DrainBindings();
        Check(download.Content as string == "Download selected files", "English updates the real WPF button binding");
        Check(((Button)view.FindName("BackButton")).Content as string == "← Sharing modes", "Back navigation is translated");
        Check(((TextBox)view.FindName("StunBox")).Text.Length == 0, "No STUN server is configured by default");
        var directMode = (RadioButton)view.FindName("DirectModeBox");
        var turnMode = (RadioButton)view.FindName("TurnModeBox");
        Check(((TextBlock)view.FindName("DirectModeTitle")).Text == "Direct" &&
            ((TextBlock)view.FindName("TurnModeTitle")).Text == "Through a server",
            "Connection choices show descriptive titles alongside protocol logos");
        var stunFields = (StackPanel)view.FindName("StunFields");
        var turnFields = (StackPanel)view.FindName("TurnFields");
        var turn = (TextBox)view.FindName("TurnBox");
        var username = (TextBox)view.FindName("TurnUsernameBox");
        var credential = (PasswordBox)view.FindName("TurnCredentialBox");
        var relayOnly = (CheckBox)view.FindName("RelayOnlyBox");
        Check(directMode.IsChecked == true && turnFields.Visibility == Visibility.Collapsed && stunFields.Visibility == Visibility.Visible && credential.Password.Length == 0,
            "Direct is the default, shows the optional STUN field, and has no saved credentials");
        ((TextBox)view.FindName("StunBox")).Text = "not-a-stun-url";
        var gather = (Button)view.FindName("GatherButton");
        var diagnostics = (TextBox)view.FindName("DiagnosticsBox");
        Check(gather.Content as string == "Check connection environment", "ICE test action is translated");
        Check(stunFields.Visibility == Visibility.Visible && turnFields.Visibility == Visibility.Collapsed &&
            ((Button)view.FindName("ConnectButton")).IsEnabled && gather.Visibility == Visibility.Visible,
            "Direct mode enables connection actions with the optional STUN field");
        Render(view, args.FirstOrDefault(), "remote-sharing-stun-en.png");
        gather.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(status.Text.StartsWith("Enter valid stun:", StringComparison.Ordinal), "Invalid STUN URLs are rejected before networking");
        turnMode.IsChecked = true;
        Check(turnFields.Visibility == Visibility.Visible && stunFields.Visibility == Visibility.Collapsed &&
            ((Button)view.FindName("ConnectButton")).IsEnabled, "TURN selection shows credentials and enables connection actions");
        var automatic = (CheckBox)view.FindName("AutoTurnBox");
        Check(automatic.IsChecked == true && ((StackPanel)view.FindName("AutoTurnFields")).Visibility == Visibility.Visible &&
            ((StackPanel)view.FindName("ManualTurnFields")).Visibility == Visibility.Collapsed,
            "Automatic TURN credentials are the default and hide manual credentials");
        ((TextBox)view.FindName("TurnApiBox")).Text = "http://localhost:5047/turn-credentials";
        gather.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(status.Text.Contains("Sign in, then try again"), "Missing sign-in is explained before networking");
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
        ((TextBox)view.FindName("StunBox")).Text = ""; // Without STUN, direct mode gathers same-network paths only.
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
        Check(!download.IsEnabled, "Standalone ICE test does not enable downloads");
        Check(diagnostics.Text.Contains("STUN: not used") && !diagnostics.Text.Contains("not-a-stun-url"),
            "Direct mode does not apply STUN input to candidate gathering");
        Check(!diagnostics.Text.Contains("test-secret") && !diagnostics.Text.Contains("test-user"),
            "Diagnostics exclude TURN credentials");
        Check(room.Text == "partial-code" && view.FindName("PeerBox") is null && view.FindName("RoleBox") is null,
            "Room code is preserved and peer IDs and roles are no longer required");
        ((Button)view.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        DrainBindings();
        Check(status.Text.StartsWith("Enter the 20-character room code", StringComparison.Ordinal), "Validation errors render in English");
        Render(view, args.FirstOrDefault(), "remote-sharing-en.png");
        language.SelectedIndex = 0;
        DrainBindings();
        Check(download.Content as string == "선택한 파일 받기" && status.Text.StartsWith("20자리 룸 코드", StringComparison.Ordinal),
            "Switching back translates the existing validation status");
        Check(diagnostics.Text.Contains("후보 수집 완료") && diagnostics.Text.Contains(" typ host"),
            "Language switching preserves candidate diagnostics");
        Render(view, args.FirstOrDefault(), "remote-sharing-ko.png");
        Render(view, args.FirstOrDefault(), "remote-sharing-stun-ko.png");
        turnMode.IsChecked = true;
        Check(turn.Text.Contains("turn.example.com") && credential.Password == "test-secret",
            "Changing connection methods preserves entered settings");
        Render(view, args.FirstOrDefault(), "remote-sharing-turn-ko.png");
        automatic.IsChecked = true;
        ((TextBox)view.FindName("TurnApiBox")).Text = "";
        Render(view, args.FirstOrDefault(), "remote-sharing-turn-auto-ko.png");
        VerifyProgressPanels(view);
        view.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine("PASS: Remote sharing WPF language checks completed.");
    }

    // Receive progress belongs to the receive panel and send progress to the share panel.
    private static void VerifyProgressPanels(RemoteFileSharingView view)
    {
        var receiveBar = (ProgressBar)view.FindName("ReceiveProgressBar");
        var receiveText = (TextBlock)view.FindName("ReceiveProgressText");
        var shareBar = (ProgressBar)view.FindName("ShareProgressBar");
        var shareText = (TextBlock)view.FindName("ShareProgressText");
        Invoke("ShowProgress", new RemoteTransferProgress(Guid.NewGuid(), "incoming.txt", true, 4, 8, RemoteTransferState.Receiving));
        Check(receiveBar.Value == 50 && receiveText.Text.Contains("incoming.txt") && shareBar.Value == 0 && !shareText.Text.Contains("incoming.txt"),
            "Receive progress appears in the receive panel");
        Invoke("ShowProgress", new RemoteTransferProgress(Guid.NewGuid(), "outgoing.txt", false, 6, 8, RemoteTransferState.Sending));
        Check(shareBar.Value == 75 && shareText.Text.Contains("outgoing.txt") && receiveText.Text.Contains("incoming.txt"),
            "Send progress appears in the share panel");
        Invoke("ShowReceivedFile", new RemoteFileReceived(Guid.NewGuid(), "incoming.txt", "C:\\incoming.txt", 8));
        Check(((ItemsControl)view.FindName("HistoryList")).Items.Count > 0, "A received file is added to recent transfers");

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
