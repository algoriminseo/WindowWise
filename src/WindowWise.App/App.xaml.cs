using System.Security.Cryptography.X509Certificates;
using System.Windows;
using WindowWise.ViewModels;
using WindowWise.Views;
namespace WindowWise;

public partial class App : Application
{
    public AudioManagerViewModel? AudioViewModel { get; set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AudioViewModel = new AudioManagerViewModel();

        var mainWindow = new MainWindow(AudioViewModel);
        mainWindow.Show();
    }
}
