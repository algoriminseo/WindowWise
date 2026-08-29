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

    public GlobalHotKeyService(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        //Windows 창 번호 저장
        _windowHandle = new WindowInteropHelper(window).Handle;

        if (_windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "GlobalHotKeyService must be generated after window is created.");
        }
        //Windows가 창에 여러 종류 메시지를 보냄 (마우스 이동, 키입력, 창 크기 변경 등..)
        //이런 메시지를 WPF가 WPF 이벤트로 변환함. HwndSource 객체를 통해 이런 low level Windows message에 직접 접근 가능.
        _messageSource =
            HwndSource.FromHwnd(_windowHandle)
            ?? throw new InvalidOperationException(
                "Window Message Source not found.");
        //메세지 검사 함수. 메세지가 들어올때마다 ProcessWindowMessage 함수 호출
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
