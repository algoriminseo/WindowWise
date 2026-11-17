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
        Check(((TextBox)view.FindName("StunBox")).Text == "stun:stun.cloudflare.com:3478",
            "The direct attempt has a default STUN server");
        Check(view.FindName("DirectModeBox") is null && view.FindName("TurnModeBox") is null,
            "Room connections use one network configuration");
        VerifyConnectionLoader(view);
        var turn = (TextBox)view.FindName("TurnBox");
        var username = (TextBox)view.FindName("TurnUsernameBox");
        var credential = (PasswordBox)view.FindName("TurnCredentialBox");
        Check(view.FindName("RelayOnlyBox") is null, "Room connections have no force-relay option");
        Check(credential.Password.Length == 0, "No TURN credentials are saved");
        ((TextBox)view.FindName("StunBox")).Text = "not-a-stun-url";
        var gather = (Button)view.FindName("GatherButton");
        var diagnostics = (TextBox)view.FindName("DiagnosticsBox");
        Check(gather.Content as string == "Check connection environment", "ICE test action is translated");
        Check(((Button)view.FindName("ConnectButton")).IsEnabled && gather.Visibility == Visibility.Visible,
            "Automatic mode enables room connections and the connection check");
        gather.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(status.Text.StartsWith("Enter valid stun:", StringComparison.Ordinal), "Invalid STUN URLs are rejected before networking");
        ((TextBox)view.FindName("StunBox")).Text = "";
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
        turn.Text = "";
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
            "Gathering without an ICE server does not reuse previously entered STUN addresses");
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
        automatic.IsChecked = true;
        ((TextBox)view.FindName("TurnApiBox")).Text = "";
        VerifyProgressPanels(view);
        view.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine("PASS: Remote sharing WPF language checks completed.");
    }

    private static void VerifyConnectionLoader(RemoteFileSharingView view)
    {
        var loader = (Grid)view.FindName("ConnectionLoader");
        var label = (TextBlock)view.FindName("ConnectionLoaderText");
        var setStatus = typeof(RemoteFileSharingView).GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Show(string key) => setStatus.Invoke(view, [key, false, Array.Empty<object>()]);

        Check(loader.Visibility == Visibility.Collapsed, "Room connection loader is hidden while idle");
        Show("RoomWaiting");
        Check(loader.Visibility == Visibility.Visible && label.Text == "Waiting for peer…", "The loader shows room waiting");
        Show("TryingDirect");
        Check(label.Text == "Trying direct connection…", "The loader shows the direct attempt");
        Show("SwitchingToTurn");
        Check(label.Text == "Switching to TURN relay…", "The loader shows the switch to TURN");
        Show("Ready");
        Check(loader.Visibility == Visibility.Collapsed, "The loader stops when the connection is idle");
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
