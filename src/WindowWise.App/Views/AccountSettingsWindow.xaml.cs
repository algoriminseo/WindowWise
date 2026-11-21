using WindowWise.Localization;
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
        if (string.IsNullOrWhiteSpace(auth.DisplayName)) UiText.SetText(ProfileName, "Not provided");
        else ProfileName.Text = auth.DisplayName;
        if (string.IsNullOrWhiteSpace(auth.Email)) UiText.SetText(ProfileEmail, "Not provided");
        else ProfileEmail.Text = auth.Email;
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
            UiText.SetText(SettingsStatus, "Unable to save alert preferences. Please try again.");
            SettingsStatus.Visibility = Visibility.Visible;
        }
    }
}
