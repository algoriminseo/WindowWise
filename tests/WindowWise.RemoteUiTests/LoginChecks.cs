using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Duende.IdentityModel.OidcClient.Browser;
using WindowWise.Services;
using WindowWise.Views;

internal static class LoginChecks
{
    public static void Run(string? previewDirectory)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var auth = new PendingAuth();
        var window = new LoginWindow(auth);
        var button = (Button)window.FindName("LoginButton");
        var status = (TextBlock)window.FindName("StatusText");
        Check((string)button.Content == "Continue to sign in", "Login starts with an English action");
        Render(window, previewDirectory);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!button.IsEnabled && auth.Attempts == 1, "Pending login disables duplicate requests");
        Check(((ProgressBar)window.FindName("LoginProgress")).Visibility == Visibility.Visible,
            "Pending login shows progress");
        auth.Completion.SetException(new HttpRequestException("Do not expose raw upstream details"));
        PumpUntil(() => button.IsEnabled);
        Check(status.Text.StartsWith("Unable to connect.", StringComparison.Ordinal) &&
            !status.Text.Contains("upstream"), "Network failure shows a useful English retry message");
        Check(((ProgressBar)window.FindName("LoginProgress")).Visibility == Visibility.Collapsed,
            "Failed login clears the busy indicator");
        window.Close();
        Task.Run(VerifyCallbacksAsync).GetAwaiter().GetResult();
        Console.WriteLine("PASS: Login checks completed.");
    }

    private static async Task VerifyCallbacksAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        Task? requests = null;
        var browser = new SystemBrowser(_ => requests = Task.Run(async () =>
        {
            Console.WriteLine("CHECK: Sending a loopback callback with a foreign state.");
            using var invalid = await http.GetAsync(SystemBrowser.CallbackUrl + "?code=ignored&state=wrong", deadline.Token);
            Check(invalid.StatusCode == HttpStatusCode.BadRequest, "Callback rejects a foreign login state");
            using var valid = await http.GetAsync(SystemBrowser.CallbackUrl + "?code=test-code&state=expected", deadline.Token);
            Check(valid.IsSuccessStatusCode, "Matching callback is accepted");
            Check(valid.Headers.CacheControl?.NoStore == true, "Callback response cannot be cached");
            string body = await valid.Content.ReadAsStringAsync(deadline.Token);
            Check(!body.Contains("test-code") && body.Contains("Return to WindowWise"),
                "Callback page uses English and does not echo authorization codes");
        }, deadline.Token));
        var options = new BrowserOptions("https://example.invalid/authorize?state=expected", SystemBrowser.CallbackUrl);
        BrowserResult result;
        try { result = await browser.InvokeAsync(options, deadline.Token); }
        catch
        {
            if (requests is not null) await requests;
            throw;
        }
        await requests!;
        Check(result.ResultType == BrowserResultType.Success && result.Response.Contains("code=test-code"),
            "Matching callback reaches the OIDC client");

        // Rebinding the same port also proves the previous listener was stopped.
        using var cancel = new CancellationTokenSource();
        var canceledBrowser = new SystemBrowser(_ => cancel.Cancel());
        try
        {
            await canceledBrowser.InvokeAsync(options, cancel.Token);
            throw new InvalidOperationException("Canceled login unexpectedly completed.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Console.WriteLine("PASS: Canceling login releases the callback listener.");
        }
    }

    private static void PumpUntil(Func<bool> completed)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        timer.Tick += (_, _) =>
        {
            if (completed() || DateTime.UtcNow >= deadline) frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Check(completed(), "Login operation returns control to the UI");
    }

    private static void Render(LoginWindow window, string? directory)
    {
        if (directory is null) return;
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(460, double.PositiveInfinity));
        int height = (int)Math.Ceiling(content.DesiredSize.Height);
        content.Arrange(new Rect(0, 0, 460, height));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(460, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(244, 246, 248)), null, new Rect(0, 0, 460, height));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, "login-window.png"));
        encoder.Save(file);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }

    private sealed class PendingAuth : IAuthService
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attempts { get; private set; }
        public bool IsAuthenticated => false;
        public string? UserId => null;
        public string? Email => null;
        public string? DisplayName => null;
        public Task SignInAsync(CancellationToken cancellationToken)
        {
            Attempts++;
            return Completion.Task.WaitAsync(cancellationToken);
        }
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
