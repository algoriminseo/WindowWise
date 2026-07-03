using System.Windows.Controls;

namespace WindowWise.Views;

public partial class OverviewView : UserControl
{
    public OverviewView()
    {
        InitializeComponent();
    }


    // Navigate to SmartClipboardView
    private void SmartClipboardCard_ActionClick(object sender, System.Windows.RoutedEventArgs e)
    {

        var mainWindow = System.Windows.Application.Current.MainWindow as MainWindow;
        if (mainWindow != null)
        {
            mainWindow.ShowSmartClipboard();
        }
    }
}
