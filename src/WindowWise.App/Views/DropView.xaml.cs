using System.Windows;
using System.Windows.Controls;

namespace WindowWise.Views;

public partial class DropView : UserControl
{
    private FileSharingView? _localView;
    private RemoteFileSharingView? _remoteView;

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
        }

        LocalContent.Content = _localView;
        SelectionPanel.Visibility = Visibility.Collapsed;
        LocalContent.Visibility = Visibility.Visible;
    }

    // Open the remote sharing screen while preserving its connection across navigation.
    private void RemoteSharing_Click(object sender, RoutedEventArgs e)
    {
        if (_remoteView is null)
        {
            _remoteView = new RemoteFileSharingView();
            _remoteView.BackRequested += (_, _) => ShowSelection();
        }
        LocalContent.Content = _remoteView;
        SelectionPanel.Visibility = Visibility.Collapsed;
        LocalContent.Visibility = Visibility.Visible;
    }

    public Task ShutdownAsync() => Task.WhenAll(
        _localView?.ShutdownAsync() ?? Task.CompletedTask,
        _remoteView?.ShutdownAsync() ?? Task.CompletedTask);
}
