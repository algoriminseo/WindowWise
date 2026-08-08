

namespace WindowWise.Models;

public sealed class ClipboardCategoryResult
{
    public required string Category { get; init; }

    public string? SubCategory { get; init; }

    public string? SuggestedCategory { get; init; }


    public bool IsAiCategorized { get; init; }


}
