using WindowWise.Models;

namespace WindowWise.Services;

public static class ClipboardContentClassifier
{
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
}
