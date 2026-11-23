using WindowWise.Mobile.Services;

namespace WindowWise.Mobile.Pages;

// Sign in on a browser page; the result comes back to this app on 127.0.0.1:7890.
public partial class SignInPage : ContentPage
{
    public SignInPage() => InitializeComponent();

    private async void SignIn_Clicked(object? sender, EventArgs e)
    {
        SignInButton.IsEnabled = false;
        SignInHint.IsVisible = false;
        SigningIn.IsVisible = true;
        try
        {
            await MobileAuth.Current.SignInAsync(CancellationToken.None);
            Navigation.InsertPageBefore(new ConnectPage(), this);
            await Navigation.PopAsync(false);
        }
        catch (Exception exception)
        {
            SigningIn.IsVisible = false;
            SignInHint.Text = exception.Message;
            SignInHint.TextColor = Color.FromArgb("#F87171");
            SignInHint.IsVisible = true;
            SignInButton.IsEnabled = true;
        }
    }
}
