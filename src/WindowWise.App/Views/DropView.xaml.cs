using System.Windows;
using System.Windows.Controls;

namespace WindowWise.Views;

public partial class DropView : UserControl
{
    private FileSharingView? _localView;

    public DropView()
    {
        InitializeComponent();
    }

    public void ShowSelection()
    {
        LocalContent.Visibility = Visibility.Collapsed;
        SelectionPanel.Visibility = Visibility.Visible;
    }

    private void LocalSharing_Click(object sender, RoutedEventArgs e)
    {
        if (_localView is null)
        {
            _localView = new FileSharingView();
            _localView.BackRequested += (_, _) => ShowSelection();
            LocalContent.Content = _localView;
        }

        SelectionPanel.Visibility = Visibility.Collapsed;
        LocalContent.Visibility = Visibility.Visible;
    }

    public Task ShutdownAsync() => _localView?.ShutdownAsync() ?? Task.CompletedTask;
}
