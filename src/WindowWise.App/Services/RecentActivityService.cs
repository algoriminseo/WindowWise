using WindowWise.Localization;
using System.ComponentModel;
using System.Collections.ObjectModel;

namespace WindowWise.Services;

public sealed class RecentActivityService
{
    private const int MaximumActivityCount = 4;

    private readonly ObservableCollection<RecentActivityItem> _items = [];

    public ReadOnlyObservableCollection<RecentActivityItem> Items { get; }

    public RecentActivityService()
    {
        Items = new ReadOnlyObservableCollection<RecentActivityItem>(_items);
    }

    public void Add(string title, string detail, RecentActivityKind kind)
    {
        _items.Insert(0, new RecentActivityItem
        {
            Title = title,
            Detail = detail,
            Kind = kind,
            CreatedAt = DateTimeOffset.Now
        });

        while (_items.Count > MaximumActivityCount)
        {
            _items.RemoveAt(_items.Count - 1);
        }
    }
}

public sealed class RecentActivityItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public RecentActivityItem() => AppText.Current.Track(this, static item =>
        item.PropertyChanged?.Invoke(item, new PropertyChangedEventArgs(string.Empty)));
    public string LocalizedTitle => UiText.Text(Title);
    public string LocalizedDetail => Detail == "Unknown app" ? UiText.Text(Detail) : Detail;
    public required string Title { get; init; }

    public required string Detail { get; init; }

    public required RecentActivityKind Kind { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string IconText =>
        Kind switch
        {
            RecentActivityKind.AudioDevice => "\uE767",
            RecentActivityKind.Layout => "\uE8A7",
            RecentActivityKind.Focus => "\uE823",
            _ => "\uE8C8"
        };

    public string RelativeTimeText
    {
        get
        {
            TimeSpan elapsed = DateTimeOffset.Now - CreatedAt;

            if (elapsed.TotalSeconds < 60)
            {
                return UiText.Text("Just now");
            }

            if (elapsed.TotalMinutes < 60)
            {
                int minutes = Math.Max(1, (int)elapsed.TotalMinutes);
                return UiText.Text("{0} min ago", minutes);
            }

            if (elapsed.TotalHours < 24)
            {
                int hours = Math.Max(1, (int)elapsed.TotalHours);
                return UiText.Text("{0} hr ago", hours);
            }

            if (elapsed.TotalDays < 2)
            {
                return UiText.Text("Yesterday");
            }

            return CreatedAt.LocalDateTime.ToString(AppText.Current.Language == "ko" ? "M월 d일" : "MMM d", AppText.Current.Culture);
        }
    }
}

public enum RecentActivityKind
{
    Clipboard,
    AudioDevice,
    Layout,
    Focus,
    System
}
