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
    /// <summary>
    /// first two ispasswordField and looksLikePasswordField is a supportative solution, not the necessary detection.
    /// other methods are used to detect the content type using regex and contains condition.
    /// </summary>
    public static ClipboardSensitivityResult DetectSensitivity(
        string content,
        ClipboardSourceContext? sourceContext = null)
    {
        if(string.IsNullOrWhiteSpace(content))
        {
            return new ClipboardSensitivityResult(SensitivityConfidence.None, SensitivityKind.None, null);
        }

        string trimmedContent = content.Trim();

        if(sourceContext?.IsPasswordField == true)
        {
            return new ClipboardSensitivityResult(
                SensitivityConfidence.High,
                SensitivityKind.Password,
                "Password field detected");

        }

        if (sourceContext?.LooksLikePasswordField == true)
        {
            return new ClipboardSensitivityResult(
                SensitivityConfidence.Possible,
                SensitivityKind.Password,
                "Focused field looks password-related");
        }

        if (LooksLikeApiKeyOrToken(trimmedContent))
        {
            return new ClipboardSensitivityResult(
                SensitivityConfidence.High,
                SensitivityKind.Token,
                "Explicit token or secret label");
        }

        if (LooksLikeBearerToken(trimmedContent))
        {
            return new ClipboardSensitivityResult(
                SensitivityConfidence.High,
                SensitivityKind.Token,
                "Bearer token");
        }

        if (IsPasswordManager(sourceContext?.SourceAppName))
        {
            return new ClipboardSensitivityResult(
                SensitivityConfidence.Possible,
                SensitivityKind.PasswordManagerSource,
                "Copied from password manager");
        }

        if (LooksLikeOtp(trimmedContent))
        {
            return new ClipboardSensitivityResult(
                SensitivityConfidence.Possible,
                SensitivityKind.VerificationCode,
                "Six-digit code-like text");
        }

        if (LooksLikePasswordCandidate(trimmedContent))
        {
            return new ClipboardSensitivityResult(
                SensitivityConfidence.Possible,
                SensitivityKind.Password,
                "Password-like text");
        }

        if (LooksLikeLongRandomString(trimmedContent))
        {
            return new ClipboardSensitivityResult(
                SensitivityConfidence.Possible,
                SensitivityKind.SecretLikeText,
                "Long random-looking text");
        }

        return new ClipboardSensitivityResult(SensitivityConfidence.None, SensitivityKind.None, null);

    }

    // Require both a token-related label and a plausible value to avoid matching normal prose.
    private static bool LooksLikeApiKeyOrToken(string content)
    {
        return Regex.IsMatch(
            content,
            @"(?i)^\s*(api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret)\s*[:=]\s*\S{12,}\s*$");
    }

    private static bool LooksLikeBearerToken(string content)
    {
        return Regex.IsMatch(
            content,
            @"(?i)^bearer\s+[A-Za-z0-9._~+/=-]{20,}$");
    }

    // Six digits are ambiguous, so this is only a possible verification-code signal.
    private static bool LooksLikeOtp(string content)
    {
        return Regex.IsMatch(content, @"^\d{6}$");
    }

    // Long encoded-looking strings can be tokens, hashes, or IDs, so keep this possible only.
    private static bool LooksLikeLongRandomString(string content)
    {
        return Regex.IsMatch(content, @"^[A-Za-z0-9+/=_-]{32,}$");
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



    /// <summary>
    /// Overall detects password well (Not perfectly since websites varies in their password requirements.)
    /// 1, length between 8 and 64
    /// 2. contains at least one letter
    /// 3. contains at least one digit or symbol
    /// </summary>
    private static bool LooksLikePasswordCandidate(string content)
    {
        string value = content;

        if (value.Length < 8 || value.Length >= 64)
        {
            return false;
        }

        if(value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        bool hasLetter = value.Any(char.IsLetter);
        bool hasDigits = value.Any(char.IsDigit);
        bool hasSymbol = value.Any(ch => !char.IsLetterOrDigit(ch));

        return hasLetter & (hasDigits || hasSymbol);
    }

}
