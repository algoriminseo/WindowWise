using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Input;
using WindowWise.Models;
using WindowWise.Services;
using WindowWise.ViewModels;
using WindowWise.Views;
namespace WindowWise;

public partial class App : Application
{
    public AudioManagerViewModel? AudioViewModel { get; set; }

    private MainWindow _mainWindow = null!;
    private TrayIconService? _trayIconService;
    private GlobalHotKeyService? _hotKeyService;
    private static readonly Key[] NumberKeys =
[
    Key.D1,
    Key.D2,
    Key.D3,
    Key.D4,
    Key.D5,
    Key.D6,
    Key.D7,
    Key.D8,
    Key.D9
];
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AudioViewModel = new AudioManagerViewModel();

        _mainWindow = new MainWindow(AudioViewModel);
        _trayIconService = new TrayIconService(ShowMainWindow, ExitApplication);
        _mainWindow.Show();
        _hotKeyService = new GlobalHotKeyService(_mainWindow);
        HotKeyRefresh();
        AudioViewModel.AudioPreset.PresetsChanged += HotKeyRefresh;
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
        Shutdown();
    }
    private void HotKeyRefresh()
    {
        int i = 1;
        foreach (PresetInfo preset in AudioViewModel.PresetInfo.Presets) {
            if (i > 9) break;
            _hotKeyService?.RegisterPresetHotKey(i, ModifierKeys.Control | ModifierKeys.Alt, NumberKeys[i-1], ()=>AudioViewModel?.AudioPreset.LoadPreset(preset.Id));
            i++;
        }

    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIconService?.Dispose();
        base.OnExit(e);
    }

}
