namespace WindowWise.Mobile.Pages;

// Design preview: signing in is simulated until the Auth0 flow is connected.
public partial class SignInPage : ContentPage
{
    public SignInPage() => InitializeComponent();

    private async void SignIn_Clicked(object? sender, EventArgs e)
    {
        SignInButton.IsEnabled = false;
        SignInHint.IsVisible = false;
        SigningIn.IsVisible = true;
        await Task.Delay(900);
        Navigation.InsertPageBefore(new ConnectPage(), this);
        await Navigation.PopAsync(false);
    }
}
