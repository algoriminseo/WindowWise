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
    private string _content = string.Empty;
    private ClipboardType _contentType;
    private DateTimeOffset _copiedAt = DateTimeOffset.Now;
    private bool _isFavorite;
    private string? _category;
    private string? _categoryColorHex;
    private bool _isCategoryManuallyAssigned;
    private string? _sourceAppName;
    private bool _isSensitive;
    private string? _sensitiveReason;
    private SensitivityConfidence _sensitivityConfidence;
    private ProtectionState _protectionState;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Content
    {
        get => _content;
        set
        {
            SetField(ref _content, value);
            OnPropertyChanged(nameof(DisplayContent));
        }
    }

    public ClipboardType ContentType
    {
        get => _contentType;
        set => SetField(ref _contentType, value);
    }

    public string DisplayContent
    {
        get
        {
            if (ProtectionState == ProtectionState.Blocked)
            {
                return "[Sensitive content blocked]";
            }

            if (IsMasked)
            {
                int maskLength = Math.Clamp(Content.Length, 8, 24);
                return new string('*', maskLength);
            }

            return Content;
        }
    }

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
        set
        {
            SetField(ref _sourceAppName, value);
            OnPropertyChanged(nameof(SourceAppDisplayName));
        }
    }

    public string SourceAppDisplayName =>
        string.IsNullOrWhiteSpace(SourceAppName) ? "Unknown app" : SourceAppName;

    public bool IsSensitive
    {
        get => _isSensitive;
        set
        {
            SetField(ref _isSensitive, value);
            OnPropertyChanged(nameof(ProtectionStatus));
        }
    }

    public string? SensitiveReason
    {
        get => _sensitiveReason;
        set => SetField(ref _sensitiveReason, value);
    }

    public SensitivityConfidence SensitivityConfidence
    {
        get => _sensitivityConfidence;
        set
        {
            SetField(ref _sensitivityConfidence, value);
            OnPropertyChanged(nameof(DisplayContent));
            OnPropertyChanged(nameof(IsBlocked));
            OnPropertyChanged(nameof(NeedsUserConfirmation));
            OnPropertyChanged(nameof(ProtectionStatus));
        }
    }

    public ProtectionState ProtectionState
    {
        get => _protectionState;
        set
        {
            if (_protectionState == value)
            {
                return;
            }

            _protectionState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayContent));
            OnPropertyChanged(nameof(IsMasked));
            OnPropertyChanged(nameof(IsBlocked));
            OnPropertyChanged(nameof(NeedsUserConfirmation));
            OnPropertyChanged(nameof(ProtectionStatus));
        }
    }

    public bool IsMasked =>
        ProtectionState == ProtectionState.NeedsReview ||
        ProtectionState == ProtectionState.Protected ||
        ProtectionState == ProtectionState.Blocked;

    public bool IsBlocked => ProtectionState == ProtectionState.Blocked;

    public bool NeedsUserConfirmation => ProtectionState == ProtectionState.NeedsReview;

    public string ProtectionStatus =>
        ProtectionState switch
        {
            ProtectionState.NeedsReview => "Needs review",
            ProtectionState.Protected => "Protected",
            ProtectionState.Blocked => "Blocked",
            _ => string.Empty
        };

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
