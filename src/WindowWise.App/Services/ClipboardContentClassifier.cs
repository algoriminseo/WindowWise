using WindowWise.Models;
using System.Text.RegularExpressions;
namespace WindowWise.Services;

public sealed record ClipboardSensitivityResult(bool IsSensitive, string? Reaosn);

public static class ClipboardContentClassifier
{

    /// <synnary>
    /// distinguish the content type for the clipboard content, either text or link
    /// </synnary>
    public static ClipboardType Classify(string content)
    {
        if (!Uri.TryCreate(content, UriKind.Absolute, out var uri) ||
            uri is null)
        {
            return ClipboardType.Text;
        }

        bool isHttp = uri.Scheme == Uri.UriSchemeHttp;
        bool isHttps = uri.Scheme == Uri.UriSchemeHttps;

        if (isHttp || isHttps)
        {
            return ClipboardType.Link;
        }

        return ClipboardType.Text;
    }

    public static ClipboardSensitivityResult DetectSensitivity(string conetnt)
    {



    }

}
