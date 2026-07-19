namespace WindowWise.Models;

/// <summary>
/// Represents the type of content stored in the clipboard.
/// </summary>
public enum ClipboardType
{
    Text,
    Link
}

public sealed class ClipboardInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Content { get; init; }

    public ClipboardType ContentType { get; init; }

    public DateTimeOffset CopiedAt { get; set; } = DateTimeOffset.Now;

    public bool IsFavorite { get; set; }

    public string? Category { get; set; }

    public string? SourceAppName { get; set; }

    public bool IsSensitive { get; set; }

    public string? SensitiveReason { get; set; }
}
