using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WindowWise.Models;
namespace WindowWise.Services;


public sealed partial class ClipboardMonitorService : IDisposable
{
    /// <summary>
    /// signal that the clipboard has been updated.
    /// </summary>
    private const int ClipboardUpdateMessage = 0x031D;

    private readonly ClipboardHistoryService _historyService;

    private readonly ClipboardSourceContextService _sourceContextService;

    private HwndSource? _windowSource;
    private IntPtr _windowHandle;

    public ClipboardMonitorService(ClipboardHistoryService historyService, ClipboardSourceContextService sourceContextService)
    {
        _historyService = historyService;
        _sourceContextService = sourceContextService;
    }
    /// <summary>
    /// window handle detection starts here
    /// </summary>
    public void Start(Window window)
    {
        if(_windowSource is not null)
        {
            return;
        }

        _windowHandle = new WindowInteropHelper(window).EnsureHandle();
        _windowSource = HwndSource.FromHwnd(_windowHandle);
        _windowSource?.AddHook(ProcessWindowMessage);
        if(AddClipboardFormatListener(_windowHandle) == 0)
        {
            _windowSource?.RemoveHook(ProcessWindowMessage);
            _windowSource = null;
            _windowHandle = IntPtr.Zero;

            throw new InvalidOperationException("Failed to register clipboard listener.");
        }
    }
    /// <summary>
    /// stops the clipboard monnitoring and cleans up resources.
    /// </summary>
    public void Stop()
    {
        if(_windowSource is null)
        {
            return;
        }

        RemoveClipboardFormatListener(_windowHandle);
        _windowSource?.RemoveHook(ProcessWindowMessage);

        _windowSource = null;
        _windowHandle = IntPtr.Zero;

    }

    /// <summary>
    /// Process the delieverd window msg 
    /// </summary>
    private IntPtr ProcessWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == ClipboardUpdateMessage)
        {
            CaptureClipboardText();
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Captures the text content from the clipboard and adds it to the history service.
    /// </summary>
    private void CaptureClipboardText()
    {
        try
        {
            if(!Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                return;
            }

            string content = Clipboard.GetText(TextDataFormat.UnicodeText);
            ClipboardSourceContext sourceContext = _sourceContextService.GetCurrentContext();
            string? sourceUrl = GetClipboardSourceUrl(content);
            ClipboardSensitivityResult sensitivity = ClipboardContentClassifier.DetectSensitivity(content, sourceContext);

            if(sensitivity.ShouldClearClipboard)
            {
                Clipboard.Clear();

                _historyService.Add(
                    ClipboardHistoryService.CreateBlockedContentPlaceholder(),
                    sourceAppName: sourceContext.SourceAppName,
                    sourceUrl: sourceUrl,
                    isSensitive: true,
                    sensitiveReason: sensitivity.Reason,
                    sensitivityConfidence: sensitivity.Confidence);
                return;
            }

            _historyService.Add(
                content,
                sourceAppName: sourceContext.SourceAppName,
                sourceUrl: sourceUrl,
                isSensitive: sensitivity.IsSensitive,
                sensitiveReason: sensitivity.Reason,
                sensitivityConfidence: sensitivity.Confidence);

        }
        catch (COMException ex)
        {
            // Log the exception or handle it as needed
            Console.WriteLine($"Error capturing clipboard text: {ex.Message}");
        }
    }


    /// <summary>
    /// Gets the clipboard url contents and adds it to the history service.
    /// </summary>

    private static string? GetClipboardSourceUrl(string content)
    {
        if (Uri.TryCreate(content, UriKind.Absolute, out Uri? contentUri) &&
            (contentUri.Scheme == Uri.UriSchemeHttp || contentUri.Scheme == Uri.UriSchemeHttps))
        {
            return contentUri.ToString();
        }

        if (!Clipboard.ContainsData(DataFormats.Html))
        {
            return null;
        }

        string? htmlClipboardData = Clipboard.GetData(DataFormats.Html) as string;

        if (string.IsNullOrWhiteSpace(htmlClipboardData))
        {
            return null;
        }

        const string sourceUrlPrefix = "SourceURL:";

        string? sourceUrlLine = htmlClipboardData.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .FirstOrDefault(line => line.StartsWith("SourceURL:", StringComparison.OrdinalIgnoreCase));

        if (sourceUrlLine is null) {
            return null;
         }

        string sourceUrl = sourceUrlLine[sourceUrlPrefix.Length..].Trim();

        return Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? uri) ? uri.ToString() : null;
    }



    public void Dispose()
    {
        Stop();
    }


    /// <summary>
    /// P/Invoke declarations for clipboard format listener functions
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int AddClipboardFormatListener(IntPtr windowHandle);


    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int RemoveClipboardFormatListener(IntPtr windowHandle);


}
