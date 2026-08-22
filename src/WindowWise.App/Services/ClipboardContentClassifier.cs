using WindowWise.Models;
using System.Text.RegularExpressions;
namespace WindowWise.Services;



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

    public static ClipboardSensitivityResult DetectSensitivity(string content, ClipboardSourceContext? sourceContext = null)
    {
        if(string.IsNullOrWhiteSpace(content))
        {
            return new ClipboardSensitivityResult(SensitivityConfidence.None, SensitivityKind.None, null);
        }


        /// <summary>
        /// detects the password based on the password: or pwd: label in the content
        /// </summary>
        string trimmedContent = content.Trim();

        if(sourceContext?.IsPasswordField == true)
        {
            return new ClipboardSensitivityResult(SensitivityConfidence.High, SensitivityKind.Password, "Detected as Password field.");

        }
        if (Regex.IsMatch(trimmedContent, @"(?i)^\s*(password|pwd)\s*[:=]\s*\S{4,}\s*$"))
        {
            return new ClipboardSensitivityResult(SensitivityConfidence.High, SensitivityKind.Password, "Explicit password label");
        }


        /// <summary>
        /// detects the token based on key, access token, refresh token, client secret labels in the content
        /// </summary>
        if (Regex.IsMatch(trimmedContent,
               @"(?i)^\s*(api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret)\s*[:=]\s*\S{12,}\s*$"))
        {
            return new ClipboardSensitivityResult(
                SensitivityConfidence.High,
                SensitivityKind.Token,
                "Explicit token or secret label");
        }

        /// <summary>
        /// detects the bearer token based on the i.e. "Bearer Afaewr2-5q4af" prefix in the content
        /// </summary>

        if (Regex.IsMatch(trimmedContent,
              @"(?i)^bearer\s+[A-Za-z0-9._~+/=-]{20,}$"))
        {
            return new ClipboardSensitivityResult(
                SensitivityConfidence.High,
                SensitivityKind.Token,
                "Bearer token");
        }

        /// <summary>
        /// detects the 6 digit verification code 
        /// </summary>

        if (Regex.IsMatch(trimmedContent,
            @"(?i)^\d{6}$"))
        {
            return new ClipboardSensitivityResult(
                SensitivityConfidence.Possible,
                SensitivityKind.Token,
                "six-digit verification code");
        }

        // <summary>
        /// detects the password manager application source based on the source context, 
        /// </summary>

        if (IsPasswordManager(sourceContext?.SourceAppName))
        {
            return new ClipboardSensitivityResult(
                   SensitivityConfidence.Possible,
                   SensitivityKind.PasswordManagerSource,
                   "User Copied from password manager Web."
             );

        }

        return new ClipboardSensitivityResult(SensitivityConfidence.None, SensitivityKind.None, null);

    }
    /// <summary>
    /// check if the source app name is in the frequently used password manager list
    /// </summary>
    private static bool IsPasswordManager(string? sourceAppName)
    {
        if (string.IsNullOrWhiteSpace(sourceAppName))
        {
            return false;
        }
        var knownPasswordManagers = new List<string>
        {
            "1Password",
            "lastPass",
            "Dashlane",
            "Bitwarden",
            "Keeper",
            "RoboForm",
            "NordPass",
            "Enpass",
            "Zoho Vault",
            "Sticky Password"
        };
        return knownPasswordManagers.Any(app => sourceAppName.Contains(app, StringComparison.OrdinalIgnoreCase));
    }

}
