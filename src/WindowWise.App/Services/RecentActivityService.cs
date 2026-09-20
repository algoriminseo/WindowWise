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

public sealed class RecentActivityItem
{
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
                return "Just now";
            }

            if (elapsed.TotalMinutes < 60)
            {
                int minutes = Math.Max(1, (int)elapsed.TotalMinutes);
                return $"{minutes} min ago";
            }

            if (elapsed.TotalHours < 24)
            {
                int hours = Math.Max(1, (int)elapsed.TotalHours);
                return $"{hours} hr ago";
            }

            if (elapsed.TotalDays < 2)
            {
                return "Yesterday";
            }

            return CreatedAt.LocalDateTime.ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture);
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
