using System.IO;
using System.Windows;
using System.Windows.Controls;
using WindowWise.Localization;

namespace WindowWise.Controls;

public partial class LanguageSelector : UserControl
{
    private bool _updating;
    public LanguageSelector()
    {
        InitializeComponent();
        AppText.Current.Track(this, static view => view.RefreshLanguage());
        RefreshLanguage();
    }

    private void RefreshLanguage()
    {
        _updating = true;
        LanguageBox.SelectedIndex = AppText.Current.SelectedIndex;
        _updating = false;
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || !IsInitialized) return;
        try { AppText.Current.SelectedIndex = LanguageBox.SelectedIndex; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RefreshLanguage();
            MessageBox.Show(UiText.Text("Unable to save the display language. Please try again."), "WindowWise",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
