namespace WindowWise.Models;


/// <summary>
/// Attributes for the sensitivity (i,e, password, verification code) of the clipboard content
/// None: No sensitive content detected
/// Possible : Possible sensitive content detected
/// High: High confidence of sensitive content detected
/// </summary>
public enum  SensitivityConfidence
{
    None,
    Possible,
    High
}
/// <summary>
/// Attributes for the sensitivity categories
/// VerificationCode: 6 digit verification code, OTP,
/// Password: User password, Pin
/// Token: Bearer token, API key, or other secret token
/// SecretLikeText: Copied text that looks like a secret
/// PasswordManagerSource: Copied from a password manager application i.e.1Password, Bitwarden, etc.
/// </summary>


public enum SensitivityKind
{
    None,
    VerificationCode,
    Password,
    Token,
    SecretLikeText,
    PasswordManagerSource
}

/// <summary>
/// These are for password related attributes.
/// None: No protection applied
/// NeedsReview: Clipboard content needs user's review
/// Protected: Clipboard content is protected with masking
/// Blocked: Clipboard content is blocked
/// </summary>
public enum ProtectionState
{
    None,
    NeedsReview,
    Protected,
    Blocked
}

public sealed record ClipboardSensitivityResult(SensitivityConfidence Confidence, SensitivityKind Kind, string? Reason)
{
    public bool IsSensitive => Confidence != SensitivityConfidence.None;
    public bool ShouldClearClipboard => Confidence == SensitivityConfidence.High;
    public bool NeedUserConfirmation => Confidence == SensitivityConfidence.Possible;
}


