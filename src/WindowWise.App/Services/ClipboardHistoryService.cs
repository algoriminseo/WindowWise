using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Forms;
using WindowWise.Models;

namespace WindowWise.Services;

public sealed class ClipboardHistoryService
{
    private const int MaximumRegularItemCount = 1000;

    private readonly ClipboardHistoryRepository _repository;

    /// <summary>
    /// Admin clipboard storatge
    /// </summary>
    private readonly ObservableCollection<ClipboardInfo> _items = [];

    private readonly ObservableCollection<ClipboardInfo> _filteredItems = [];

    private readonly ObservableCollection<ClipboardCategoryRule> _categoryRules = [];

    private readonly ObservableCollection<ClipboardCategoryRule> _filteredCategoryRules = [];

    private readonly ObservableCollection<ClipboardInfo> _selectedCategoryItems = [];

    private ClipboardCategoryRule? _selectedCategoryRule;

    private string _currentSearchKeyword = string.Empty;

    private string _currentCategorySearchKeyword = string.Empty;

    public ReadOnlyObservableCollection<ClipboardInfo> FilteredItems { get; }

    public ReadOnlyObservableCollection<ClipboardCategoryRule> CategoryRules { get; }

    public ReadOnlyObservableCollection<ClipboardInfo> SelectedCategoryItems { get; }

    public bool HasCategories => _categoryRules.Count > 0;

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
        CategoryRules = new ReadOnlyObservableCollection<ClipboardCategoryRule>(_filteredCategoryRules);
        SelectedCategoryItems = new ReadOnlyObservableCollection<ClipboardInfo>(_selectedCategoryItems);

        foreach (ClipboardCategoryRule rule in _repository.LoadCategoryRules())
        {
            _categoryRules.Add(rule);
        }

        foreach (ClipboardInfo item in _repository.LoadRecentItems())
        {
            ApplyCategory(item);
            _items.Add(item);
            _filteredItems.Add(item);
        }

        RefreshCategoryRuleItems();
        RefreshFilteredCategoryRules();
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
            ApplyCategory(existingItem);
            _items.Remove(existingItem);
            _items.Insert(0, existingItem);
            _repository.Upsert(existingItem);
            Search(_currentSearchKeyword);
            RefreshSelectedCategoryItems();
            RefreshCategoryRuleItems();
            return;
        }

        var newItem = new ClipboardInfo
        {
            Content = content,
            ContentType = ClipboardContentClassifier.Classify(content),
            CopiedAt = DateTimeOffset.Now
        };

        ApplyCategory(newItem);
        _items.Insert(0, newItem);
        _repository.Upsert(newItem);
        RemoveOldItems();
        Search(_currentSearchKeyword);
        RefreshSelectedCategoryItems();
        RefreshCategoryRuleItems();
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
            RefreshSelectedCategoryItems();
            RefreshCategoryRuleItems();
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
        RefreshSelectedCategoryItems();
        RefreshCategoryRuleItems();
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
        RefreshSelectedCategoryItems();
        RefreshCategoryRuleItems();
    }

    public bool AddCategoryRule(string name, string colorHex)
    {
        string categoryName = name.Trim();

        if (string.IsNullOrWhiteSpace(categoryName))
        {
            return false;
        }

        bool alreadyExists = _categoryRules.Any(rule =>
            string.Equals(rule.Name, categoryName, StringComparison.OrdinalIgnoreCase));

        if (alreadyExists)
        {
            return false;
        }

        var rule = new ClipboardCategoryRule
        {
            Name = categoryName,
            Keywords = [],
            ColorHex = string.IsNullOrWhiteSpace(colorHex) ? GetCategoryColorHex(_categoryRules.Count) : colorHex
        };

        _repository.AddCategoryRule(rule);
        _categoryRules.Add(rule);
        _selectedCategoryRule = rule;
        RefreshItemCategories();
        RefreshFilteredCategoryRules();

        return true;
    }

    public void DeleteCategoryRule(Guid id)
    {
        ClipboardCategoryRule? rule = _categoryRules.FirstOrDefault(rule => rule.Id == id);

        if (rule is null)
        {
            return;
        }

        _repository.DeleteCategoryRule(id);
        _categoryRules.Remove(rule);
        if (_selectedCategoryRule?.Id == id)
        {
            _selectedCategoryRule = null;
        }
        RefreshItemCategories();
        RefreshFilteredCategoryRules();
    }

    public void SelectCategoryRule(ClipboardCategoryRule? rule)
    {
        _selectedCategoryRule = rule;
        RefreshSelectedCategoryItems();
    }

    public bool AssignItemToCategory(Guid itemId, ClipboardCategoryRule rule)
    {
        ClipboardInfo? item = _items.FirstOrDefault(item => item.Id == itemId);

        if (item is null)
        {
            return false;
        }

        item.Category = rule.Name;
        item.CategoryColorHex = rule.ColorHex;
        item.IsCategoryManuallyAssigned = true;
        _repository.Upsert(item);
        RefreshFilteredItems();
        RefreshCategoryRuleItems();
        SelectCategoryRule(rule);

        return true;
    }

    public bool RemoveItemFromCategory(Guid itemId)
    {
        ClipboardInfo? item = _items.FirstOrDefault(item => item.Id == itemId);

        if (item is null || string.IsNullOrWhiteSpace(item.Category))
        {
            return false;
        }

        item.Category = null;
        item.CategoryColorHex = null;
        item.IsCategoryManuallyAssigned = false;
        _repository.Upsert(item);
        RefreshFilteredItems();
        RefreshSelectedCategoryItems();
        RefreshCategoryRuleItems();

        return true;
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

    public void SearchCategories(string keyword)
    {
        _currentCategorySearchKeyword = keyword?.Trim() ?? string.Empty;
        RefreshFilteredCategoryRules();
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

        // Return true if at least one field matches the search keyword.
        return contentMatches ||
               typeMatches ||
               categoryMatches ||
               sourceAppMatches;
    }
    // Create category box for users
    private void ApplyCategory(ClipboardInfo item)
    {
        if (item.IsCategoryManuallyAssigned &&
            item.Category is not null)
        {
            ClipboardCategoryRule? manualRule = _categoryRules.FirstOrDefault(rule =>
                string.Equals(rule.Name, item.Category, StringComparison.OrdinalIgnoreCase));

            if (manualRule is not null)
            {
                item.CategoryColorHex = manualRule.ColorHex;
                return;
            }

            item.Category = null;
            item.CategoryColorHex = null;
            item.IsCategoryManuallyAssigned = false;
        }
        else if (!item.IsCategoryManuallyAssigned)
        {
            item.Category = null;
            item.CategoryColorHex = null;
        }
    }


    // update the categories 
    private void RefreshItemCategories()
    {
        foreach (ClipboardInfo item in _items)
        {
            ApplyCategory(item);
            _repository.Upsert(item);
        }

        RefreshFilteredItems();
        RefreshSelectedCategoryItems();
        RefreshCategoryRuleItems();
    }

    // Apply Condiiton that enumerates the selected contagories
    private void RefreshSelectedCategoryItems()
    {
        _selectedCategoryItems.Clear();

        if (_selectedCategoryRule is null)
        {
            return;
        }

        IEnumerable<ClipboardInfo> result = _items
            .Where(item => string.Equals(item.Category, _selectedCategoryRule.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.IsFavorite)
            .ThenByDescending(item => item.CopiedAt);

        foreach (ClipboardInfo item in result)
        {
            _selectedCategoryItems.Add(item);
        }
    }

    // Apply Condiiton that enumerates the refreshed contagories
    private void RefreshCategoryRuleItems()
    {
        foreach (ClipboardCategoryRule rule in _categoryRules)
        {
            rule.Items.Clear();

            IEnumerable<ClipboardInfo> matchingItems = _items
                .Where(item => string.Equals(item.Category, rule.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.IsFavorite)
                .ThenByDescending(item => item.CopiedAt);

            foreach (ClipboardInfo item in matchingItems)
            {
                rule.Items.Add(item);
            }

            rule.ItemCount = rule.Items.Count;
            rule.NotifyItemsChanged();
        }
    }



    private void RefreshFilteredCategoryRules()
    {
        _filteredCategoryRules.Clear();

        IEnumerable<ClipboardCategoryRule> result = _categoryRules;

        if (!string.IsNullOrWhiteSpace(_currentCategorySearchKeyword))
        {
            result = result.Where(rule =>
                rule.Name.Contains(_currentCategorySearchKeyword, StringComparison.OrdinalIgnoreCase));
        }

        foreach (ClipboardCategoryRule rule in result.OrderBy(rule => rule.Name))
        {
            _filteredCategoryRules.Add(rule);
        }
    }

    private static string GetCategoryColorHex(int index)
    {
        string[] colors =
        [
            "#2563EB",
            "#0891B2",
            "#22A06B",
            "#D97706",
            "#DC2626",
            "#9333EA",
            "#0F766E",
            "#475569"
        ];

        return colors[index % colors.Length];
    }
}
