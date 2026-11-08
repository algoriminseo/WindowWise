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
    private readonly RecentActivityService _recentActivityService;
    private DropView? _dropView;
    private readonly IAuthService _auth = new AuthService();
    private bool _loginWindowOpen;
    private bool _accountBusy;

    private bool _exitRequested;

    public void ShowOverview()
    {
        MainContent.Content = new OverviewView(_recentActivityService);
        SetActiveNavigation(OverviewButton);
    }

    public MainWindow(AudioManagerViewModel audioManagerViewModel, ClipboardHistoryService clipboardHistoryService, ClipboardMonitorService clipboardMonitorService, RecentActivityService recentActivityService)
    {
        InitializeComponent();


        _clipboardHistoryService = clipboardHistoryService;
        _clipboardMonitorService = clipboardMonitorService;
        _smartClipboardView = new SmartClipboardView(_clipboardHistoryService, _clipboardMonitorService);
        _audioManagerViewModel = audioManagerViewModel;
        _recentActivityService = recentActivityService;
        AccountHeader.Configure(_auth, AccountPreferences.Current);
        AccountHeader.SignInRequested += (_, _) => EnsureSignedIn();
        AccountHeader.SignOutRequested += SignOutRequested;
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
                 {OverviewButton, SmartClipboardButton, AudioManagerButton, FileSharingButton, WindowLayoutsButton
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

    public void ShowFileSharing()
    {
        if (_accountBusy || !EnsureSignedIn()) return;

        _dropView ??= new DropView { AccessTokenProvider = _auth.GetAccessTokenAsync };
        _dropView.ShowSelection();
        MainContent.Content = _dropView;
        SetActiveNavigation(FileSharingButton);
    }

    private bool EnsureSignedIn()
    {
        if (_accountBusy) return false;
        if (!_auth.IsAuthenticated)
        {
            if (_loginWindowOpen) return false;
            _loginWindowOpen = true;
            try
            {
                var loginWindow = new LoginWindow(_auth) { Owner = this };
                if (loginWindow.ShowDialog() != true) return false;
            }
            finally
            {
                _loginWindowOpen = false;
                AccountHeader.Refresh();
            }
        }
        AccountHeader.Refresh();
        return true;
    }

    private async void SignOutRequested(object? sender, EventArgs e)
    {
        if (_accountBusy || _loginWindowOpen) return;
        _accountBusy = true;
        AccountHeader.IsEnabled = MainContent.IsEnabled = false;
        try
        {
            // Stop transfers and disconnect peers before discarding the authenticated session.
            try { await StopFileSharingAsync(); }
            catch (Exception)
            {
                MessageBox.Show(this, "Unable to close sharing connections. Please restart WindowWise before signing out.",
                    "WindowWise", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _dropView = null;
            ShowOverview();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await _auth.SignOutAsync(timeout.Token);
            }
            catch (Exception)
            {
                MessageBox.Show(this,
                    "You are signed out of WindowWise on this device, but browser sign-out could not be confirmed. " +
                    "Your browser may still remember your account.", "WindowWise", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            _accountBusy = false;
            AccountHeader.IsEnabled = MainContent.IsEnabled = true;
            AccountHeader.Refresh();
        }
    }

    private void FileSharingButton_Click(object sender, RoutedEventArgs e) => ShowFileSharing();

    public Task StopFileSharingAsync() => _dropView?.ShutdownAsync() ?? Task.CompletedTask;


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
