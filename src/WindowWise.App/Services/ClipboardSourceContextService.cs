using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WindowWise.Services;

public sealed partial class ClipboardSourceContextService 
{

    /// <summary>
    /// gets the current context of the clipboard source,
    /// including the name of the foreground process,
    /// whether the focused element is a password field, and whether it looks like a password field. 
    /// </summary>
    public ClipboardSourceContext GetCurrentContext()
    {
        AutomationElement? focusedElement = GetFocusedElement();

        return new ClipboardSourceContext(
            GetForegroundProcessName(),
            IsPasswordField(focusedElement),
            LooksLikePasswordField(focusedElement));

    }

    private static AutomationElement? GetFocusedElement()
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch
        {
            return null;
        }
    }
    /// <summary>
    /// finds the password field using the focused element property(Automation Library) and returns true if the focused element is a password field
    /// </summary>
    private static bool IsPasswordField(AutomationElement? element)
    {
        if(element == null)
        {
            return false;
        }
        try
        {
            object value = element.GetCurrentPropertyValue(
                AutomationElement.IsPasswordProperty,
                ignoreDefaultValue: true);

            return value is bool isPassword && isPassword;
        }
        catch
        {
            return false;
        }

    }

    /// <summary>
    /// check if the password field contains the word password, passwd, pwd, passcode, pin in the name or automation id of the focused element 
    /// </summary>

    private static bool LooksLikePasswordField(AutomationElement? element)
    {
        if (element == null)
        {
            return false;
        }
        try
        {
            string name = element.Current.Name ?? string.Empty;
            string automationId = element.Current.AutomationId ?? string.Empty;
            string combinedText = $"{name} {automationId}";

            return combinedText.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                combinedText.Contains("passwd", StringComparison.OrdinalIgnoreCase) ||
                combinedText.Contains("pwd", StringComparison.OrdinalIgnoreCase) ||
                combinedText.Contains("passcode", StringComparison.OrdinalIgnoreCase) ||
                combinedText.Contains("pin", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// .gets the name of the current process that is in the foreground using the GetForegroundWindow and GetWindowThreadProcessId functions from user32.dll 
    /// </summary>
    /// <returns></returns>
    private static string? GetForegroundProcessName()
    {
        IntPtr foregroundWindow = GetForegroundWindow();

        if(foregroundWindow == IntPtr.Zero)
        {
            return null;
        }

        GetWindowThreadProcessId(foregroundWindow, out int processId);

        try
        {
            return Process.GetProcessById(processId).ProcessName;

        }
        catch
        {
            return null;
        }        

    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

}
