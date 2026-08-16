using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WindowWise.Models;
using WindowWise.Services;

namespace WindowWise.Views;

public partial class SmartClipboardView : UserControl
{
    private readonly ClipboardHistoryService _historyService;
    private ClipboardInfo? _pendingCategoryItem;
    private string _selectedCategoryColorHex = "#2563EB";

    public SmartClipboardView(ClipboardHistoryService historyService)
    {
        InitializeComponent();

        _historyService = historyService;
        DataContext = historyService;
    }

    /// <summary>
    /// Handle the MouseLeftButtonUp event for a history item.
    /// When a history item is clicked, this method retrieves that ClipboardInfo object and copies its content to the clipboard.
    /// </summary>
    private void HistoryItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipboardInfo item })
        {
            return;
        }

        var result = MessageBox.Show(
            "Do you want to copy this clipboard item?",
            "Copy clipboard item",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Clipboard.SetText(item.Content, TextDataFormat.UnicodeText);
            FeedbackText.Text = "Copied to clipboard";
            FeedbackText.Visibility = Visibility.Visible;
        }
        catch (COMException)
        {
            FeedbackText.Text = "Clipboard is busy. Please try again.";
            FeedbackText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Handle the Click event for the delete button of a history item.
    /// </summary>
    private void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is  not FrameworkElement { DataContext: ClipboardInfo item })
        {
            return;
        }

        string message = item.IsFavorite
            ? "Are you sure you want to delete this favorite clipboard item? This will also remove it from any category."
            : "Are you sure you want to delete this clipboard item? This will also remove it from any category.";

        var result = MessageBox.Show(
            message,
            "Delete clipboard item",
            MessageBoxButton.YesNo,
            item.IsFavorite ? MessageBoxImage.Warning : MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _historyService.Delete(item.Id);
    }

    /// <summary>
    /// Handle the Click event for the favorite button of a history item.
    /// </summary>
    private void FavoriteItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is FrameworkElement { DataContext : ClipboardInfo item})
        {
            _historyService.ToggleFavorite(item.Id);
        }
    }


    /// <summary>
    /// Handle the Click event for the "Clear All" button.
    /// </summary>
    private void ClearAll_Click(
       object sender,
       RoutedEventArgs e)
    {
        if (_historyService.Items.Count == 0)
        {
            return;
        }

        var result = MessageBox.Show(
            "Do you want to remove all clipboard history?",
            "Clear clipboard history",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _historyService.Clear();
        }
    }

    /// <summary>
    /// event handler for the TextChanged event of the search TextBox.
    /// This method is called whenever the text in the search box changes. It retrieves the current text from the TextBox and calls the Search method of the ClipboardHistoryService to filter the clipboard history based on the search query.
    /// </summary>
    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            _historyService.Search(textBox.Text);
        }
    }


    private void AllFilter_Click(object sender, RoutedEventArgs e) {
        _historyService.SetFilter(ClipboardViewFilter.All);

    }

    private void FavoritesFilter_Click(object sender, RoutedEventArgs e)
    {
        _historyService.SetFilter(ClipboardViewFilter.Favorites);
    }

    private void LinksFilter_Click(object sender, RoutedEventArgs e)
    {
        _historyService.SetFilter(ClipboardViewFilter.Links);
    }

    private void TextFilter_Click(object sender, RoutedEventArgs e)
    {
        _historyService.SetFilter(ClipboardViewFilter.Text);
    }

    private void AddItemToCategory_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: ClipboardInfo item })
        {
            return;
        }

        if (!_historyService.HasCategories)
        {
            MessageBox.Show(
                "There are no categories.",
                "No categories",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _pendingCategoryItem = item;
        PendingCategoryContentText.Text = item.Content;
        CategorySearchTextBox.Text = string.Empty;
        PendingCategoryItemBox.Visibility = Visibility.Visible;
        CategoryEditorBox.Visibility = Visibility.Collapsed;
        CategoryAddInstructionText.Visibility = Visibility.Visible;
        SelectedCategoryTitle.Text = "Choose a category";
        CategoryListBox.SelectedItem = null;
        _historyService.SelectCategoryRule(null);
        CategoryOverlay.Visibility = Visibility.Visible;
    }

    private void OpenCategories_Click(object sender, RoutedEventArgs e)
    {
        _pendingCategoryItem = null;
        PendingCategoryItemBox.Visibility = Visibility.Collapsed;
        CategoryEditorBox.Visibility = Visibility.Visible;
        CategoryAddInstructionText.Visibility = Visibility.Collapsed;
        CategoryOverlay.Visibility = Visibility.Visible;
    }

    private void CloseCategories_Click(object sender, RoutedEventArgs e)
    {
        CategoryOverlay.Visibility = Visibility.Collapsed;
        CategoryListBox.SelectedItem = null;
        _historyService.SelectCategoryRule(null);
        _pendingCategoryItem = null;
        PendingCategoryItemBox.Visibility = Visibility.Collapsed;
        CategoryEditorBox.Visibility = Visibility.Visible;
        CategoryAddInstructionText.Visibility = Visibility.Collapsed;
        SelectedCategoryTitle.Text = "Select a category";
    }

    private void AddCategoryRule_Click(object sender, RoutedEventArgs e)
    {
        string categoryName = CategoryNameTextBox.Text.Trim();
        bool added = _historyService.AddCategoryRule(categoryName, GetSelectedCategoryColorHex());

        if (!added)
        {
            FeedbackText.Text = "Enter a unique category name.";
            FeedbackText.Visibility = Visibility.Visible;
            return;
        }

        CategoryNameTextBox.Text = string.Empty;
        CategorySearchTextBox.Text = string.Empty;
        FeedbackText.Text = "Category added";
        FeedbackText.Visibility = Visibility.Visible;

        ClipboardCategoryRule? addedRule = _historyService.CategoryRules.FirstOrDefault(rule =>
            string.Equals(rule.Name, categoryName, StringComparison.OrdinalIgnoreCase));
        if (addedRule is not null)
        {
            CategoryListBox.SelectedItem = addedRule;
            SelectedCategoryTitle.Text = addedRule.Name;
        }
    }

    private void DeleteCategoryRule_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: ClipboardCategoryRule rule })
        {
            return;
        }

        var result = MessageBox.Show(
            "Do you want to delete this category?",
            "Delete category",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _historyService.DeleteCategoryRule(rule.Id);
        if (CategoryListBox.SelectedItem == rule)
        {
            CategoryListBox.SelectedItem = null;
            SelectedCategoryTitle.Text = "Select a category";
        }

        FeedbackText.Text = "Category deleted";
        FeedbackText.Visibility = Visibility.Visible;
    }

    private void CategoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryListBox.SelectedItem is not ClipboardCategoryRule rule)
        {
            _historyService.SelectCategoryRule(null);
            SelectedCategoryTitle.Text = _pendingCategoryItem is null
                ? "Select a category"
                : "Choose a category";
            return;
        }

        _historyService.SelectCategoryRule(rule);
        SelectedCategoryTitle.Text = rule.Name;
    }

    private void CategoryBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipboardCategoryRule rule })
        {
            return;
        }

        CategoryListBox.SelectedItem = rule;
        _historyService.SelectCategoryRule(rule);
        SelectedCategoryTitle.Text = rule.Name;
    }

    private void AssignPendingItemToCategory_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (_pendingCategoryItem is null ||
            sender is not FrameworkElement { DataContext: ClipboardCategoryRule rule })
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_pendingCategoryItem.Category) &&
            !string.Equals(_pendingCategoryItem.Category, rule.Name, StringComparison.OrdinalIgnoreCase))
        {
            MessageBoxResult result = MessageBox.Show(
                "This item is already in another category. Are you sure you want to move it to this category?",
                "Move category item",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        bool assigned = _historyService.AssignItemToCategory(_pendingCategoryItem.Id, rule);

        if (!assigned)
        {
            return;
        }

        FeedbackText.Text = "Added item to category";
        FeedbackText.Visibility = Visibility.Visible;
        PendingCategoryItemBox.Visibility = Visibility.Collapsed;
        CategoryEditorBox.Visibility = Visibility.Visible;
        CategoryAddInstructionText.Visibility = Visibility.Collapsed;
        _pendingCategoryItem = null;
        CategoryListBox.SelectedItem = rule;
        SelectedCategoryTitle.Text = rule.Name;
    }

    private void CategorySearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            _historyService.SearchCategories(textBox.Text);
        }
    }

    private void CopyCategoryItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is FrameworkElement { DataContext: ClipboardInfo item })
        {
            CopyClipboardItem(item);
        }
    }

    private void RemoveCategoryItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: ClipboardInfo item })
        {
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            "Are you sure you want to remove category item? Clipboard item will not be removed.",
            "Remove category item",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        if (_historyService.RemoveItemFromCategory(item.Id))
        {
            FeedbackText.Text = "Removed from category";
            FeedbackText.Visibility = Visibility.Visible;
        }
    }

    private void CategoryColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string colorHex })
        {
            return;
        }

        _selectedCategoryColorHex = colorHex;
        SelectedCategoryColorPreview.Background = (Brush)new BrushConverter().ConvertFromString(colorHex)!;
    }

    private string GetSelectedCategoryColorHex()
    {
        return _selectedCategoryColorHex;
    }

    private void CopyClipboardItem(ClipboardInfo item)
    {
        try
        {
            Clipboard.SetText(item.Content, TextDataFormat.UnicodeText);
            FeedbackText.Text = "Copied to clipboard";
            FeedbackText.Visibility = Visibility.Visible;
        }
        catch (COMException)
        {
            FeedbackText.Text = "Clipboard is busy. Please try again.";
            FeedbackText.Visibility = Visibility.Visible;
        }
    }
}
