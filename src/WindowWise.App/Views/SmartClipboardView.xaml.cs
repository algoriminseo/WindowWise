using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WindowWise.Models;
using WindowWise.Services;

namespace WindowWise.Views;

public partial class SmartClipboardView : UserControl
{
    private readonly ClipboardHistoryService _historyService;

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

        if (sender is FrameworkElement { DataContext: ClipboardInfo item })
        {
            _historyService.Delete(item.Id);
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
}
