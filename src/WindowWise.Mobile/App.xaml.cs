namespace WindowWise.Mobile;

public partial class App : Application
{
    public App() => InitializeComponent();

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new NavigationPage(new Pages.SignInPage())
        {
            BarBackgroundColor = (Color)Resources["Canvas"],
            BarTextColor = (Color)Resources["Text"]
        });
}
