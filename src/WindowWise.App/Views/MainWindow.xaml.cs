using System.Windows;
using System.Windows.Controls;

namespace WindowWise.Views;

public partial class MainWindow : Window
{
    public void ShowOverview()
    {
        MainContent.Content = new OverviewView();
        SetActiveNavigation(OverviewButton);
    }

    public MainWindow()
    {
        InitializeComponent();
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
    public void AudioManagerButton_Click(object sender, RoutedEventArgs e)
    {
        ShowAudioManager();
    }
}
