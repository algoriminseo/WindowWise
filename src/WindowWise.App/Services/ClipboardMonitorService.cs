using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WindowWise.Services;

public sealed partial class ClipboardMonitorService : IDisposable
{
    /// <summary>
    /// signal that the clipboard has been updated.
    /// </summary>
    private const int ClipboardUpdateMessage = 0x031D;

    private readonly ClipboardHistoryService _historyService;

    private HwndSource? _windowSource;
    private IntPtr _windowHandle;

    public ClipboardMonitorService(ClipboardHistoryService historyService)
    {
        _historyService = historyService;
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
            _historyService.Add(content);

        }
        catch (COMException ex)
        {
            // Log the exception or handle it as needed
            Console.WriteLine($"Error capturing clipboard text: {ex.Message}");
        }
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
