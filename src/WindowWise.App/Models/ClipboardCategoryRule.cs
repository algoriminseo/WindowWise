namespace WindowWise.Models;

using System.Collections.ObjectModel;
using System.ComponentModel;

public sealed class ClipboardCategoryRule : INotifyPropertyChanged
{
    private int _itemCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; init; }

    public required IReadOnlyList<string> Keywords { get; init; }

    public string ColorHex { get; init; } = "#2563EB";

    public string KeywordDisplay => string.Join(", ", Keywords);

    public ObservableCollection<ClipboardInfo> Items { get; } = [];

    public int ItemCount
    {
        get => _itemCount;
        internal set
        {
            if (_itemCount == value)
            {
                return;
            }

            _itemCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemCountLabel)));
        }
    }

    public string ItemCountLabel => ItemCount == 1
        ? "1 item"
        : $"{ItemCount} items";

    internal void NotifyItemsChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Items)));
}
