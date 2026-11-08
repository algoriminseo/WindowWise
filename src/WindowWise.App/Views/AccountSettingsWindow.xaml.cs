using System.IO;
using System.Windows;
using WindowWise.Services;

namespace WindowWise.Views;

public partial class AccountSettingsWindow : Window
{
    private readonly AccountPreferences _preferences;
    public AccountSettingsWindow(IAuthService auth, AccountPreferences preferences)
    {
        InitializeComponent();
        _preferences = preferences;
        ProfileName.Text = string.IsNullOrWhiteSpace(auth.DisplayName) ? "Not provided" : auth.DisplayName;
        ProfileEmail.Text = string.IsNullOrWhiteSpace(auth.Email) ? "Not provided" : auth.Email;
        AudioAlertsBox.IsChecked = preferences.AudioDeviceAlertsEnabled;
    }

    private void AudioAlerts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _preferences.SetAudioDeviceAlerts(AudioAlertsBox.IsChecked == true);
            SettingsStatus.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AudioAlertsBox.IsChecked = _preferences.AudioDeviceAlertsEnabled;
            SettingsStatus.Text = "Unable to save alert preferences. Please try again.";
            SettingsStatus.Visibility = Visibility.Visible;
        }
    }
}
