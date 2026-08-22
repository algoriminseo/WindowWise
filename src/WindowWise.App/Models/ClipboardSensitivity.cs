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

public enum SensitivityKind
{
    None,
    VerificationCode,
    Password,
    Token,
    SecretLikeText,
    PasswordManagerSource
}

public sealed record ClipboardSensitivityResult(SensitivityConfidence Confidence, SensitivityKind Kind, string? Reason)
{
    public bool IsSensitive => Confidence != SensitivityConfidence.None;
    public bool ShouldClearClipboard => Confidence == SensitivityConfidence.High;
    public bool NeedUserConfirmation => Confidence == SensitivityConfidence.Possible;
}


