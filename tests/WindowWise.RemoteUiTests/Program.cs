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
        Check(view.FindName("TurnBox") is null, "No TURN configuration was added");
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
        Render(view, args.FirstOrDefault(), "remote-sharing-ko.png");
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
