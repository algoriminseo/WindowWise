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

        foreach (ClipboardInfo item in _repository.LoadRecentItems())
        {
            _items.Add(item);
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
            return _items.Remove(itemToDelete);
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
}
