using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using WindowWise.Services;
using System.Windows.Threading;


namespace WindowWise.Views;

/// <summary>
/// Recent Local Activities, and real-local time header, Naviagte through clipboard, audio manager page
/// </summary>
public partial class OverviewView : UserControl
{
    private readonly RecentActivityService? _recentActivityService;
    private readonly DispatcherTimer _clockTimer = new()
    {
        Interval = TimeSpan.FromMinutes(1)
    };

    public OverviewView()
    {
        InitializeComponent();
        Localization.AppText.Current.Track(this, static view => view.UpdateLocalTimeHeader());
        _clockTimer.Tick += ClockTimer_Tick;
        UpdateLocalTimeHeader();
    }

    public OverviewView(RecentActivityService recentActivityService)
        : this()
    {
        _recentActivityService = recentActivityService;
        ActivityList.ItemsSource = _recentActivityService.Items;
        UpdateEmptyActivityState();

        Loaded += OverviewView_Loaded;
        Unloaded += OverviewView_Unloaded;
    }

    private void OverviewView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_recentActivityService?.Items is INotifyCollectionChanged activities)
        {
            activities.CollectionChanged += Activities_CollectionChanged;
        }

        UpdateLocalTimeHeader();
        UpdateEmptyActivityState();
        _clockTimer.Start();
    }

    private void OverviewView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_recentActivityService?.Items is INotifyCollectionChanged activities)
        {
            activities.CollectionChanged -= Activities_CollectionChanged;
        }
        _clockTimer.Stop();
    }

    private void ClockTimer_Tick(object? sender, EventArgs e)
    {
        UpdateLocalTimeHeader();
    }


    private void Activities_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
       Dispatcher.Invoke(UpdateEmptyActivityState);
    }

    private void UpdateLocalTimeHeader()
    {
        DateTime now = DateTime.Now;
        GreetingText.Text = GetGreeting(now);
        HeaderStatusText.Text = now.ToString(Localization.AppText.Current.Language == "ko"
            ? "yyyy년 M월 d일 dddd · tt h:mm" : "dddd, MMMM d · h:mm tt", Localization.AppText.Current.Culture);
    }

    private void UpdateEmptyActivityState()
    {
        EmptyActivityText.Visibility = _recentActivityService?.Items.Count > 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static string GetGreeting(DateTime now)
    {
        return now.Hour switch
        {
            < 12 => Localization.UiText.Text("Good morning"),
            < 18 => Localization.UiText.Text("Good afternoon"),
            _ => Localization.UiText.Text("Good evening")
        };
    }

    private void SmartClipboardCard_ActionClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.ShowSmartClipboard();
        }
    }

    private void AudioManagerCard_ActionClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.ShowAudioManager();
        }
    }

    private void FileSharingCard_ActionClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.ShowFileSharing();
        }
    }
}
