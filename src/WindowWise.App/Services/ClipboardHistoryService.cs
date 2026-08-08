using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Forms;
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

    private ClipboardViewFilter _currentFilter = ClipboardViewFilter.All;


    public void SetFilter(ClipboardViewFilter filter)
    {
        _currentFilter = filter;
        RefreshFilteredItems();
    }

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
    /// Toggle the favorite status of a clipboard item
    /// </summary>
    public void ToggleFavorite(Guid id)
    {
        var item = _items.FirstOrDefault(item => item.Id == id);

        if (item is null)
        {
            return;
        }
        item.IsFavorite = !item.IsFavorite;
        _repository.UpdateFavorite(item.Id, item.IsFavorite);
        Search(_currentSearchKeyword);
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
        _currentSearchKeyword = keyword?.Trim() ?? string.Empty; 
        RefreshFilteredItems();
    }

    private void RefreshFilteredItems()
    {
        // Clear the current list displayed on the UI.
        _filteredItems.Clear();

        IEnumerable<ClipboardInfo> result = _items;

        // Apply the selected clipboard filter.
        if (_currentFilter == ClipboardViewFilter.Favorites)
        {
            result = result.Where(item => item.IsFavorite);
        }
        else if (_currentFilter == ClipboardViewFilter.Links)
        {
            result = result.Where(item => item.ContentType == ClipboardType.Link);
        }
        else if (_currentFilter == ClipboardViewFilter.Text)
        {
            result = result.Where(item => item.ContentType == ClipboardType.Text);
        }

        // Apply the search keyword if it is not empty.
        if (!string.IsNullOrWhiteSpace(_currentSearchKeyword))
        {
            result = result.Where(ItemMatchesSearch);
        }

        // Show favorite items first, then sort by the most recently copied items.
        result = result.OrderByDescending(item => item.IsFavorite).ThenByDescending(item => item.CopiedAt);

        // Add the filtered and sorted items to the UI collection.
        foreach (ClipboardInfo item in result)
        {
            _filteredItems.Add(item);
        }
    }

    private bool ItemMatchesSearch(ClipboardInfo item)
    {
        string keyword = _currentSearchKeyword;

        // Check whether the clipboard content contains the keyword.
        bool contentMatches = item.Content.Contains(
            keyword,
            StringComparison.OrdinalIgnoreCase);

        // Check whether the clipboard content type contains the keyword.
        bool typeMatches = item.ContentType
            .ToString()
            .Contains(
                keyword,
                StringComparison.OrdinalIgnoreCase);

        bool categoryMatches = false;

        // Check the category only when it is not null.
        if (item.Category != null)
        {
            categoryMatches = item.Category.Contains(
                keyword,
                StringComparison.OrdinalIgnoreCase);
        }

        bool sourceAppMatches = false;

        // Check the source application name only when it is not null.
        if (item.SourceAppName != null)
        {
            sourceAppMatches = item.SourceAppName.Contains(
                keyword,
                StringComparison.OrdinalIgnoreCase);
        }

        bool subCategoryMatches = false;
        if(item.SubCategory != null)
        {
            subCategoryMatches = item.SubCategory.Contains(
                keyword, StringComparison.OrdinalIgnoreCase);
        }

        bool suggestedCategoryMatches = false;

        if(suggestedCategoryMatches != null)
        {
            suggestedCategoryMatches = item.SuggestedCategory.Contains(
                keyword, StringComparison.OrdinalIgnoreCase);
        }

        // Return true if at least one field matches the search keyword.
        return contentMatches ||
               typeMatches ||
               categoryMatches ||
               sourceAppMatches ||
               subCategoryMatches ||
               suggestedCategoryMatches;
    }
    




   }
