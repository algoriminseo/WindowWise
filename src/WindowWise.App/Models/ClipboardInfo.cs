namespace WindowWise.Models;

using System.ComponentModel;
using System.Runtime.CompilerServices;

/// <summary>
/// Represents the type of content stored in the clipboard.
/// </summary>
public enum ClipboardType
{
    Text,
    Link
}

public sealed class ClipboardInfo : INotifyPropertyChanged
{
    private DateTimeOffset _copiedAt = DateTimeOffset.Now;
    private bool _isFavorite;
    private string? _category;
    private string? _categoryColorHex;
    private bool _isCategoryManuallyAssigned;
    private string? _sourceAppName;
    private bool _isSensitive;
    private string? _sensitiveReason;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Content { get; init; }

    public ClipboardType ContentType { get; init; }

    public DateTimeOffset CopiedAt
    {
        get => _copiedAt;
        set => SetField(ref _copiedAt, value);
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set => SetField(ref _isFavorite, value);
    }

    public string? Category
    {
        get => _category;
        set => SetField(ref _category, value);
    }

    public string? CategoryColorHex
    {
        get => _categoryColorHex;
        set => SetField(ref _categoryColorHex, value);
    }

    public bool IsCategoryManuallyAssigned
    {
        get => _isCategoryManuallyAssigned;
        set => SetField(ref _isCategoryManuallyAssigned, value);
    }

    public string? SourceAppName
    {
        get => _sourceAppName;
        set => SetField(ref _sourceAppName, value);
    }

    public bool IsSensitive
    {
        get => _isSensitive;
        set => SetField(ref _isSensitive, value);
    }

    public string? SensitiveReason
    {
        get => _sensitiveReason;
        set => SetField(ref _sensitiveReason, value);
    }



    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
