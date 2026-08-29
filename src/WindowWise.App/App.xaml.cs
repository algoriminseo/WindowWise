using System.Security.Cryptography.X509Certificates;
using System.Windows;
using WindowWise.ViewModels;
using WindowWise.Views;
using WindowWise.Services;
namespace WindowWise;

public partial class App : Application
{
    public AudioManagerViewModel? AudioViewModel { get; set; }

    private MainWindow _mainWindow = null!;
    private TrayIconService? _trayIconService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AudioViewModel = new AudioManagerViewModel();

        _mainWindow = new MainWindow(AudioViewModel);
        _trayIconService = new TrayIconService(ShowMainWindow, ExitApplication);
        _mainWindow.Show();
    }

    private void ShowMainWindow()
    {
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }
        _mainWindow.Activate();
    }

    private void ExitApplication()
    {
        _mainWindow.RequestExit();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIconService?.Dispose();
        base.OnExit(e);
    }
}
