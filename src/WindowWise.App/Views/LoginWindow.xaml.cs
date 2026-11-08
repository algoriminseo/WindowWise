using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using WindowWise.Services;

namespace WindowWise.Views;

public partial class LoginWindow : Window
{
    private readonly IAuthService _auth;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _closed;
    private bool _busy;

    public LoginWindow(IAuthService auth)
    {
        InitializeComponent();
        _auth = auth;
        Closed += (_, _) =>
        {
            _closed = true;
            _cancellation.Cancel();
            if (!_busy) _cancellation.Dispose();
        };
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        SetBusy(true);
        ShowStatus("Complete sign-in in your browser.");
        try
        {
            await _auth.SignInAsync(_cancellation.Token);
            if (!_closed) DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            if (!_closed) ShowStatus("Sign-in was canceled or timed out. Please try again.");
        }
        catch (HttpRequestException)
        {
            if (!_closed) ShowStatus("Unable to connect. Check your internet connection and make sure the WindowWise server is running.", true);
        }
        catch (IOException)
        {
            if (!_closed) ShowStatus("Unable to start sign-in. Close any other WindowWise sign-in windows and try again.", true);
        }
        catch (Exception exception)
        {
            if (!_closed) ShowStatus(exception is InvalidOperationException
                ? exception.Message : "Sign-in could not be completed. Please try again.", true);
        }
        finally
        {
            _busy = false;
            if (_closed) _cancellation.Dispose();
            else SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        LoginButton.IsEnabled = !busy;
        LoginButton.Content = busy ? "Waiting for sign-in..." : "Continue to sign in";
        LoginProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(185, 28, 28))
            : (Brush)FindResource("MutedTextBrush");
    }
}
