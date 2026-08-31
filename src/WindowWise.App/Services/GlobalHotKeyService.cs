using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace WindowWise.Services;


public sealed class GlobalHotKeyService : IDisposable
{
    private const uint ModNoRepeat = 0x4000;

    private readonly IntPtr _windowHandle;
    private readonly HwndSource _messageSource;
    private readonly Dictionary<int, Action> _actions = new();
    //hwnd_message (-3번)을 부모 창으로 사용하면 메시지 전용 창이 만들어짐.
    private static readonly IntPtr HwndMessage = new(-3);

    private bool _disposed;
    //Windows user32.dll의 RegisterHotKey 및 UnregisterHotKey 함수를 C#에서 호출
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        IntPtr windowHandle,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(
        IntPtr windowHandle,
        int id);

    public GlobalHotKeyService()
    {
        var parameters =
            new HwndSourceParameters("WindowWiseHotKeyWindow")
            {
                ParentWindow = HwndMessage,
                WindowStyle = 0,
                ExtendedWindowStyle = 0,
                Width = 0,
                Height = 0
            };

        _messageSource = new HwndSource(parameters);
        _windowHandle = _messageSource.Handle;

        if (_windowHandle == IntPtr.Zero)
        {
            _messageSource.Dispose();

            throw new InvalidOperationException(
                "Hot key message window could not be created.");
        }

        _messageSource.AddHook(ProcessWindowMessage);
    }

    public bool RegisterPresetHotKey(int registrationId, ModifierKeys modifiers, Key key, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GlobalHotKeyService));
        }

        if (_actions.ContainsKey(registrationId))
        {
            return false;
        }
        //wpf의 키 표현을 windows virtual key code로 변환.
        int virtualKey = KeyInterop.VirtualKeyFromKey(key);

        bool succeeded = RegisterHotKey(
            _windowHandle,
            registrationId,
            (uint) modifiers | ModNoRepeat,
            (uint)virtualKey);

        if (!succeeded)
        {
            return false;
        }

        _actions.Add(registrationId, action);

        return true;
    }

    public void UnregisterPresetHotKey(int registrationId)
    {
        if (!_actions.Remove(registrationId))
        {
            return;
        }

        UnregisterHotKey(_windowHandle, registrationId);
    }

    private IntPtr ProcessWindowMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != 0x0312)
        {
            return IntPtr.Zero;
        }

        int presetId = wParam.ToInt32();

        if (_actions.TryGetValue(presetId, out Action? action))
        {
            action();
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (int presetId in new List<int>(_actions.Keys))
        {
            UnregisterPresetHotKey(presetId);
        }

        _messageSource.RemoveHook(ProcessWindowMessage);

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
