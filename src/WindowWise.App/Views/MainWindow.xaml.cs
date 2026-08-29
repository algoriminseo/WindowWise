using System.Windows;
using System.Windows.Controls;
using WindowWise.Services;
using WindowWise.ViewModels;

namespace WindowWise.Views;

public partial class MainWindow : Window
{
    private readonly ClipboardHistoryService _clipboardHistoryService;
    private readonly ClipboardMonitorService _clipboardMonitorService;
    private readonly SmartClipboardView _smartClipboardView;
    private readonly AudioManagerViewModel _audioManagerViewModel;
    private bool _exitRequested;

    public void ShowOverview()
    {
        MainContent.Content = new OverviewView();
        SetActiveNavigation(OverviewButton);
    }

    public MainWindow(AudioManagerViewModel audioManagerViewModel)
    {
        InitializeComponent();

        var clipboardHistoryRepository = new ClipboardHistoryRepository();
        var clipboardSourceContextService = new ClipboardSourceContextService();

        _clipboardHistoryService = new ClipboardHistoryService(clipboardHistoryRepository);
        _clipboardMonitorService = new ClipboardMonitorService(
            _clipboardHistoryService,
            clipboardSourceContextService);
        _smartClipboardView = new SmartClipboardView(_clipboardHistoryService);
        _audioManagerViewModel = audioManagerViewModel;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        ShowOverview();
    }

    public void ShowSmartClipboard()
    {
        MainContent.Content = _smartClipboardView;
        SetActiveNavigation(SmartClipboardButton);
    }

    public void RequestExit()
    {
        _exitRequested = true;
        Close();
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
        MainContent.Content = new AudioManagerView(_audioManagerViewModel);
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
    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _clipboardMonitorService.Dispose();
    }


}
