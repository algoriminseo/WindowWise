using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using WindowWise.Controls;
using WindowWise.Localization;
using WindowWise.Models;
using WindowWise.Services;
using WindowWise.Views;

internal static class AppLanguageChecks
{
    public static void Run(string? previewDirectory)
    {
        CheckCatalogs();
        var language = AppText.Current;
        string preferences = Path.Combine(Environment.CurrentDirectory, "artifacts", "language-checks", Guid.NewGuid().ToString("N"), "language.json");
        language.UsePreferences(preferences);
        var selector = new LanguageSelector();
        var selection = (ComboBox)selector.FindName("LanguageBox");
        var activities = new RecentActivityService();
        activities.Add("Clipboard item captured", "Notepad", RecentActivityKind.Clipboard);
        var overview = new OverviewView(activities);
        var drop = new DropView();
        var local = new FileSharingView();
        var remote = new RemoteFileSharingView();
        var secondRemote = new RemoteFileSharingView();
        var auth = new TestAccount();
        var settings = new AccountSettingsWindow(auth, new AccountPreferences(Path.ChangeExtension(preferences, ".account.json")));
        var login = new LoginWindow(auth);
        var account = new AccountMenu();
        account.Configure(auth, new AccountPreferences(Path.ChangeExtension(preferences, ".account.json")));
        var clipboard = LoadView("SmartClipboardView");
        var audio = LoadView("AudioManagerView");
        const string userContent = "Keep this English text unchanged";
        var item = new ClipboardInfo { Content = userContent, SourceAppName = "Notepad" };
        var category = new ClipboardCategoryRule { Name = "My category", Keywords = [] };
        var protectedItem = new ClipboardInfo { Content = "test", ProtectionState = ProtectionState.Blocked };
        clipboard.DataContext = new { FilteredItems = new[] { item } };
        var room = (TextBox)remote.FindName("RoomBox");
        var server = (TextBox)remote.FindName("ServerBox");
        room.Text = "partial-room-code";
        string serverAddress = server.Text;
        ((TextBox)audio.FindName("PresetNameInput")).Text = "My audio preset";
        typeof(FileSharingView).GetMethod("SetStatus", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(local, [new LocalizedMessage("Downloaded {0} file(s) · {1}", 3, "C:\\My files"), false]);
        typeof(RemoteFileSharingView).GetMethod("SetStatus", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(remote, ["TryingDirect", false, Array.Empty<object>()]);
        var files = (IList)typeof(RemoteFileSharingView).GetField("_peerFiles", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(remote)!;
        var rowType = typeof(RemoteFileSharingView).GetNestedType("FileRow", BindingFlags.NonPublic)!;
        object row = Activator.CreateInstance(rowType, new SharedFileInfo(Guid.NewGuid(), "My file.txt", 42), "", "42 B")!;
        files.Add(row);
        var fileList = (ListBox)remote.FindName("PeerFileList");
        fileList.SelectedItem = row;

        selection.SelectedIndex = 0;
        Drain();
        Check(language.Language == "ko" && selection.SelectedIndex == 0, "The common selector changes the application language");
        var newSelector = new LanguageSelector();
        Check(((ComboBox)newSelector.FindName("LanguageBox")).SelectedIndex == 0 && language.Language == "ko", "A newly opened selector inherits Korean without resetting the app");
        Check(Texts(overview).Contains("기능 바로가기"), "The existing overview switches to Korean");
        Check((string)((Button)drop.FindName("LocalSharingButton")).Content == "로컬 파일 공유 열기", "Sharing mode selection switches to Korean");
        Check(Texts(clipboard).Contains("스마트 클립보드") && Texts(audio).Contains("오디오 관리"), "Clipboard and audio pages switch to Korean");
        Check(Texts(settings.Content as DependencyObject ?? settings).Contains("계정 설정") && Texts(login.Content as DependencyObject ?? login).Contains("로그인하고 파일 공유하기"), "Account and login windows switch to Korean");
        Check((string)((MenuItem)((Button)account.FindName("AccountButton")).ContextMenu.Items[0]).Header == "계정 설정", "The account dropdown switches to Korean");
        Check(((TextBlock)account.FindName("AccountEmail")).Text == auth.Email, "The account email remains user data");
        Check(((TextBlock)local.FindName("StatusText")).Text == "파일 3개 다운로드 완료 · C:\\My files", "An existing formatted local status is translated without losing its arguments");
        Check(((TextBlock)remote.FindName("StatusText")).Text.Contains("직접", StringComparison.Ordinal), "The current remote connection status switches to Korean");
        Check((string)((Button)secondRemote.FindName("CreateRoomButton")).Content == "룸 만들기", "Every open remote window follows the common language");
        Check(room.Text == "partial-room-code" && server.Text == serverAddress && ReferenceEquals(fileList.SelectedItem, row), "Language changes preserve room inputs, file rows and selection");
        Check(((TextBox)audio.FindName("PresetNameInput")).Text == "My audio preset" && item.Content == userContent && category.Name == "My category", "Typed text and saved user content are not translated");
        Check(activities.Items[0].LocalizedTitle == "클립보드 항목 저장됨" && activities.Items[0].RelativeTimeText == "방금" && protectedItem.DisplayContent == "[민감한 내용 차단됨]", "Recent activity and computed protection labels use the common language");
        var restored = new AppText();
        restored.UsePreferences(preferences);
        Check(restored.Language == "ko", "The display language survives reloading the preference file");
        string damaged = Path.ChangeExtension(preferences, ".damaged.json");
        File.WriteAllText(damaged, "invalid json");
        restored.UsePreferences(damaged);
        Check(restored.Language == "en", "A damaged language preference falls back to English");
        string blocked = Path.ChangeExtension(preferences, ".blocked");
        File.WriteAllText(blocked, "This file cannot be a preference directory.");
        restored.UsePreferences(Path.Combine(blocked, "language.json"));
        try { restored.Language = "ko"; throw new InvalidOperationException("An unwritable preference unexpectedly succeeded."); }
        catch (IOException) { Check(restored.Language == "en", "A failed preference save preserves the current language"); }
        var newDrop = new DropView();
        Drain();
        Check((string)((Button)newDrop.FindName("RemoteSharingButton")).Content == "원격 파일 공유 열기", "New pages inherit the selected language");
        if (previewDirectory is not null)
        {
            var shell = LoadShell();
            ((ContentControl)shell.FindName("MainContent")).Content = new OverviewView(activities);
            Check(Texts((DependencyObject)shell.Content).Contains("홈"), "Sidebar and shell labels use the common language");
            Render((FrameworkElement)shell.Content, previewDirectory, "shell-ko.png", 1240);
            ((ContentControl)shell.FindName("MainContent")).Content = null;
            shell.Close();
            Render(overview, previewDirectory, "overview-ko.png");
            Render(drop, previewDirectory, "drop-ko.png");
            Render(clipboard, previewDirectory, "clipboard-ko.png");
            Render(audio, previewDirectory, "audio-ko.png");
        }
        ((ComboBox)remote.FindName("LanguageBox")).SelectedIndex = 1;
        Drain();
        Check(language.Language == "en" && selection.SelectedIndex == 1 && Texts(overview).Contains("Your tools"), "Drop's language selector also updates the rest of the app");
        Check(((TextBlock)local.FindName("StatusText")).Text.StartsWith("Downloaded 3 file(s)", StringComparison.Ordinal), "Existing formatted status switches back to English");
        if (previewDirectory is not null) Render(overview, previewDirectory, "overview-en.png");
        local.ShutdownAsync().GetAwaiter().GetResult();
        remote.ShutdownAsync().GetAwaiter().GetResult();
        secondRemote.ShutdownAsync().GetAwaiter().GetResult();
        login.Close();
        settings.Close();
        Console.WriteLine("PASS: Application-wide language checks completed.");
    }

    private static void CheckCatalogs()
    {
        Dictionary<string, string> Catalog(string code)
        {
            using var stream = typeof(AppText).Assembly.GetManifestResourceStream($"WindowWise.Localization.App.{code}.json")!;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        }
        var english = Catalog("en");
        var korean = Catalog("ko");
        Check(english.Keys.Order().SequenceEqual(korean.Keys.Order()), "Application catalogs have identical keys");
        foreach (var (key, source) in english)
            if (string.IsNullOrWhiteSpace(korean[key]) || CompositeFormat.Parse(source).MinimumArgumentCount != CompositeFormat.Parse(korean[key]).MinimumArgumentCount)
                throw new InvalidOperationException($"Incomplete application translation: {key}");
        Check(true, "All application translations preserve formatting arguments");
    }

    // Load presentation templates without starting microphone/audio or clipboard-monitor services.
    private static UserControl LoadView(string name)
    {
        string source = File.ReadAllText(Path.Combine("src", "WindowWise.App", "Views", name + ".xaml"))
            .Replace("clr-namespace:WindowWise.Localization", "clr-namespace:WindowWise.Localization;assembly=WindowWise", StringComparison.Ordinal);
        var document = XDocument.Parse(source);
        document.Root!.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))!.Remove();
        foreach (var attribute in document.Descendants().Attributes().Where(a => a.Name.LocalName is "Click" or "MouseLeftButtonUp" or "SelectionChanged" or "TextChanged").ToList()) attribute.Remove();
        return (UserControl)XamlReader.Parse(document.ToString());
    }

    private static Window LoadShell()
    {
        string source = File.ReadAllText(Path.Combine("src", "WindowWise.App", "Views", "MainWindow.xaml"))
            .Replace("clr-namespace:WindowWise.Localization", "clr-namespace:WindowWise.Localization;assembly=WindowWise", StringComparison.Ordinal)
            .Replace("clr-namespace:WindowWise.Controls", "clr-namespace:WindowWise.Controls;assembly=WindowWise", StringComparison.Ordinal)
            .Replace("/Assets/", "/WindowWise;component/Assets/", StringComparison.Ordinal);
        var document = XDocument.Parse(source);
        document.Root!.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))!.Remove();
        foreach (var attribute in document.Descendants().Attributes().Where(a => a.Name.LocalName == "Click").ToList()) attribute.Remove();
        return (Window)XamlReader.Parse(document.ToString());
    }
    private static string[] Texts(DependencyObject root)
    {
        if (root is FrameworkElement view) { view.Measure(new Size(960, 820)); view.Arrange(new Rect(0, 0, 960, 820)); view.UpdateLayout(); }
        Drain();
        return Children<TextBlock>(root).Select(block => block.Text).ToArray();
    }
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    private static void Render(FrameworkElement view, string directory, string name, int width = 960)
    {
        view.Measure(new Size(width, 820)); view.Arrange(new Rect(0, 0, width, 820)); view.UpdateLayout(); Drain();
        var bitmap = new RenderTargetBitmap(width, 820, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory); using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
    }
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }
    private sealed class TestAccount : IAuthService
    {
        public bool IsAuthenticated => true;
        public string? UserId => "test-user";
        public string? Email => "sample@example.com";
        public string? DisplayName => "Sample User";
        public Task SignInAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("unused-test-token");
        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
