using System.Collections.ObjectModel;
using System.Windows.Media.Converters;
using WindowWise.Models;

namespace WindowWise.Services;

public sealed class ClipboardHistoryService
{
    private const int MaximumItemCount = 100;
    /// <summary>
    /// Admin clipboard storatge
    /// </summary>
    private readonly ObservableCollection<ClipboardInfo> _items = [];

    /// <summary>
    /// Items : clipboard storage
    /// </summary>
    public ClipboardHistoryService()
    {
        /// <summary>
        /// User clipboard storage, read only
        /// </summary>
        Items = new ReadOnlyObservableCollection<ClipboardInfo>(_items);
    }

    public ReadOnlyObservableCollection<ClipboardInfo> Items { get; }


    /// <summary>
    /// Add Clipboard itmes
    /// </summary>
    public void Add(string content)
    {
        if(string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var newItem = new ClipboardInfo
        {
            Content = content,
            ContentType = ClipboardContentClassifier.Classify(content),
            CopiedAt = DateTimeOffset.Now
        };

        _items.Insert(0, newItem);
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
            return _items.Remove(itemToDelete); ;
        }
        return false;
    }

    /// <summary>
    /// Clear the clipboard history 
    /// </summary>
    public void Clear()
    {
        _items.Clear();
    }

    /// <summary>
    /// Update the clipboard history 
    /// </summary>
    public void RemoveOldItems()
    {
        if(_items.Count > MaximumItemCount)
        {
            _items.RemoveAt(_items.Count - 1);
        }
    }


}
