using System.Collections.ObjectModel;
using WindowWise.Models;

namespace WindowWise.Services;

public sealed class ClipboardHistoryService
{
    private const int MaximumRegularItemCount = 300;

    private readonly ClipboardHistoryRepository _repository;

    /// <summary>
    /// Admin clipboard storatge
    /// </summary>
    private readonly ObservableCollection<ClipboardInfo> _items = [];

    private readonly ObservableCollection<ClipboardInfo> _filteredItems = [];

    private string _currentSearchKeyword = string.Empty;

    public ReadOnlyObservableCollection<ClipboardInfo> FilteredItems { get; }

    /// <summary>
    /// Items : clipboard storage
    /// </summary>
    public ClipboardHistoryService(ClipboardHistoryRepository repository)
    {
        _repository = repository;

        /// <summary>
        /// User clipboard storage, read only
        /// </summary>
        Items = new ReadOnlyObservableCollection<ClipboardInfo>(_items);
        FilteredItems = new ReadOnlyObservableCollection<ClipboardInfo>(_filteredItems);
        foreach (ClipboardInfo item in _repository.LoadRecentItems())
        {
            _items.Add(item);
            _filteredItems.Add(item);
        }
    }

    public ReadOnlyObservableCollection<ClipboardInfo> Items { get; }


    /// <summary>
    /// Add Clipboard itmes
    /// </summary>
    public void Add(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var existingItem = _items.FirstOrDefault(item =>
            string.Equals(item.Content, content, StringComparison.Ordinal));

        if (existingItem is not null)
        {
            existingItem.CopiedAt = DateTimeOffset.Now;
            _items.Remove(existingItem);
            _items.Insert(0, existingItem);
            _repository.Upsert(existingItem);
            Search(_currentSearchKeyword);
            return;
        }

        var newItem = new ClipboardInfo
        {
            Content = content,
            ContentType = ClipboardContentClassifier.Classify(content),
            CopiedAt = DateTimeOffset.Now
        };

        _items.Insert(0, newItem);
        _repository.Upsert(newItem);
        RemoveOldItems();
        Search(_currentSearchKeyword);
    }

    /// <summary>
    /// Delete clipboard items
    /// </summary>
    public bool Delete(Guid id)
    {
        var itemToDelete = _items.FirstOrDefault(item => item.Id == id);
        if (itemToDelete != null)
        {
            _repository.Delete(id);
            bool wasRemoved = _items.Remove(itemToDelete);
            Search(_currentSearchKeyword);
            return wasRemoved;
        }
        return false;
    }

    /// <summary>
    /// Clear the clipboard history 
    /// </summary>
    public void Clear()
    {
        _repository.ClearRegularItems();

        for (int index = _items.Count - 1; index >= 0; index--)
        {
            if (!_items[index].IsFavorite)
            {
                _items.RemoveAt(index);
            }
        }

        Search(_currentSearchKeyword);
    }

    /// <summary>
    /// Update the clipboard history 
    /// </summary>
    private void RemoveOldItems()
    {
        int regularItemCount = _items.Count(item => !item.IsFavorite);

        for (int index = _items.Count - 1;
             index >= 0 && regularItemCount > MaximumRegularItemCount;
             index--)
        {
            if (_items[index].IsFavorite)
            {
                continue;
            }

            _items.RemoveAt(index);
            regularItemCount--;
        }
    }


    /// <summary>
    /// searches the clipboard history based on 4 criteria: content, content type, category, and source application name.
    /// The search is case-insensitive and matches any of the criteria. If the keyword is null or whitespace, all items are returned.
    /// The results are ordered by the copied date in descending order.
    /// </summary>


    public void Search(string keyword)
    {
        _filteredItems.Clear();

        keyword = keyword?.Trim() ?? string.Empty;
        _currentSearchKeyword = keyword;

        IEnumerable<ClipboardInfo> result = _items;

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            result = _items.Where(item =>
            {
                bool contentMatches = item.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase);

                bool contentTypeMatches = item.ContentType.ToString().Contains(keyword, StringComparison.OrdinalIgnoreCase);

                bool categoryMatches = false;

                if (item.Category is not null)
                {
                    categoryMatches = item.Category.Contains(keyword, StringComparison.OrdinalIgnoreCase);

                }

                bool sourceAppNameMatches = false;

                if (item.SourceAppName is not null)
                {
                    sourceAppNameMatches = item.SourceAppName.Contains(keyword, StringComparison.OrdinalIgnoreCase);
                }

                return contentMatches || contentTypeMatches || categoryMatches || sourceAppNameMatches;

            });
        }
        result = result.OrderByDescending(item => item.CopiedAt);

        foreach (ClipboardInfo item in result) {
            _filteredItems.Add(item);
        }


    }
}
