namespace WindowWise.Services;

public sealed record ClipboardSourceContext(string? SourceAppName, bool IsPasswordField, bool LooksLikePasswordField);
