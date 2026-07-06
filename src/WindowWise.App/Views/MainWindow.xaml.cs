using System.Windows;
using System.Windows.Controls;
using WindowWise.Services;

namespace WindowWise.Views;

public partial class MainWindow : Window
{
    private readonly ClipboardHistoryService _clipboardHistoryService;
    private readonly ClipboardMonitorService _clipboardMonitorService;

    public void ShowOverview()
    {
        MainContent.Content = new OverviewView();
        SetActiveNavigation(OverviewButton);
    }

    public MainWindow()
    {
        InitializeComponent();

        _clipboardHistoryService = new ClipboardHistoryService();
        _clipboardMonitorService = new ClipboardMonitorService(_clipboardHistoryService);

        SourceInitialized += MainWindow_SourceInitialized;
        Closed += MainWindow_Closed;
        ShowOverview();
    }

    public void ShowSmartClipboard()
    {
        MainContent.Content = new SmartClipboardView();
        SetActiveNavigation(SmartClipboardButton);
    }

    // Navigate to SmartClipboardView
    private void SmartClipboardButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSmartClipboard();
    }

    //Navigate to Home Screen
    private void OverviewButton_Click(object sender, RoutedEventArgs e)
    {
        ShowOverview();
    }

    private void SetActiveNavigation(Button activeButton)
    {
        var defaultStyle = (Style)FindResource("NavigationButtonStyle");
        var activeStyle = (Style)FindResource("PrimaryNavigationButtonStyle");

        foreach (var button in new[]
                 {OverviewButton, SmartClipboardButton, AudioManagerButton, WindowLayoutsButton
                 })
        {
            button.Style = defaultStyle;
        }

        activeButton.Style = activeStyle;
    }
    public void ShowAudioManager()
    {
        MainContent.Content = new AudioManagerView();
        SetActiveNavigation(AudioManagerButton);
    }


    // Navigate to AudioManagerView
    public void AudioManagerButton_Click(object sender, RoutedEventArgs e)
    {
        ShowAudioManager();
    }

    // Navigate to WindowLayoutsView
    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _clipboardMonitorService.Start(this);
    }
    // close the clipboard monitor service when the window is closed
    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _clipboardMonitorService.Dispose();
    }


}
