using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WindowWise.Services;
using WindowWise.Views;

namespace WindowWise.Controls;

public partial class AccountMenu : UserControl
{
    private IAuthService? _auth;
    private AccountPreferences? _preferences;
    public event EventHandler? SignInRequested;
    public event EventHandler? SignOutRequested;

    public AccountMenu() => InitializeComponent();

    public void Configure(IAuthService auth, AccountPreferences preferences)
    {
        _auth = auth;
        _preferences = preferences;
        Refresh();
    }

    public void Refresh()
    {
        bool signedIn = _auth?.IsAuthenticated == true;
        string identity = !string.IsNullOrWhiteSpace(_auth?.Email) ? _auth.Email :
            !string.IsNullOrWhiteSpace(_auth?.DisplayName) ? _auth.DisplayName : "Your account";
        AccountEmail.Text = signedIn ? identity : "Sign in";
        AccountEmail.ToolTip = signedIn ? identity : "Sign in to WindowWise";
        AccountButton.SetValue(AutomationProperties.NameProperty, signedIn ? $"Account menu for {identity}" : "Sign in");
        AvatarText.FontFamily = new FontFamily(signedIn ? "Segoe UI" : "Segoe MDL2 Assets");
        AvatarText.Text = signedIn && identity.Length > 0 ? identity[..1].ToUpperInvariant() : "\uE77B";
        DropdownArrow.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        AudioAlertsItem.IsChecked = _preferences?.AudioDeviceAlertsEnabled ?? true;
    }

    private void Account_Click(object sender, RoutedEventArgs e)
    {
        Refresh();
        if (_auth?.IsAuthenticated != true)
        {
            SignInRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        AccountDropdown.PlacementTarget = AccountButton;
        AccountDropdown.Placement = PlacementMode.Bottom;
        AccountDropdown.IsOpen = true;
    }

    private void Account_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_auth?.IsAuthenticated != true) e.Handled = true;
    }

    private void Dropdown_Opened(object sender, RoutedEventArgs e)
    {
        Refresh();
        if (_auth?.IsAuthenticated != true) AccountDropdown.IsOpen = false;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_auth?.IsAuthenticated != true || _preferences is null) return;
        new AccountSettingsWindow(_auth, _preferences) { Owner = Window.GetWindow(this) }.ShowDialog();
        Refresh();
    }

    private void AudioAlerts_Click(object sender, RoutedEventArgs e)
    {
        try { _preferences?.SetAudioDeviceAlerts(AudioAlertsItem.IsChecked); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var owner = Window.GetWindow(this);
            if (owner is not null)
                MessageBox.Show(owner, "Unable to save alert preferences. Please try again.", "WindowWise",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Refresh();
    }

    private void SignOut_Click(object sender, RoutedEventArgs e) => SignOutRequested?.Invoke(this, EventArgs.Empty);
}
