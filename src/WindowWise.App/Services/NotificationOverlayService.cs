using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WindowWise.Models;
using WindowWise.Views;

namespace WindowWise.Services;

public sealed class NotificationOverlayService : IDisposable
{
    private readonly AudioDeviceInfo _audioDeviceInfo;
    private readonly DefaultDeviceOverlayWindow _window;
    private readonly DispatcherTimer _hideTimer;
    private bool _disposed;

    public NotificationOverlayService(AudioDeviceInfo audioDeviceInfo)
    {
        _audioDeviceInfo = audioDeviceInfo;
        _window = new DefaultDeviceOverlayWindow();

        _hideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };

        _hideTimer.Tick += OnHideTimerTick;
        _audioDeviceInfo.DefaultDeviceChanged += OnDefaultDeviceChanged;
    }

    public void ShowDefaultDevice(AudioDeviceWrapper device)
    {
        if (!_window.Dispatcher.CheckAccess())
        {
            _window.Dispatcher.Invoke(
                () => ShowDefaultDevice(device));

            return;
        }

        // 기존 자동 숨김 시간을 초기화.
        _hideTimer.Stop();

        // 진행 중인 투명도 애니메이션을 제거.
        _window.BeginAnimation(
            Window.OpacityProperty,
            null);

        _window.SetDevice(device, IsHeadphoneDevice(device.Name));

        if (!_window.IsVisible)
            _window.Show();

        _window.UpdateLayout();
        PositionWindow();

        _window.Opacity = 1;

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(150)
        };

        _window.BeginAnimation(
            Window.OpacityProperty,
            fadeIn);

        _hideTimer.Start();
    }

    private void PositionWindow()
    {
        Rect workArea = SystemParameters.WorkArea;

        _window.Left =
            workArea.Left +
            (workArea.Width - _window.ActualWidth) / 2;

        _window.Top =
            workArea.Bottom -
            _window.ActualHeight -
            48;
    }

    private void OnHideTimerTick(
        object? sender,
        EventArgs e)
    {
        _hideTimer.Stop();

        var fadeOut = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(150)
        };

        fadeOut.Completed += (_, _) =>
        {
            _window.BeginAnimation(
                Window.OpacityProperty,
                null);

            _window.Hide();
            _window.Opacity = 1;
        };

        _window.BeginAnimation(
            Window.OpacityProperty,
            fadeOut);
    }
    private void OnDefaultDeviceChanged()
    {
        AudioDeviceWrapper? device =
            _audioDeviceInfo.DefaultDevice;

        if (device is null)
            return;

        ShowDefaultDevice(device);
    }

    private static bool IsHeadphoneDevice(string deviceName)
    {
        string[] headphoneKeywords =
        [
            "headphone",
            "headset",
            "earphone",
            "earbud",
            "airpods",
            "buds",
            "헤드폰",
            "헤드셋",
            "이어폰"
        ];

        foreach (string keyword in headphoneKeywords)
        {
            if (deviceName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _audioDeviceInfo.DefaultDeviceChanged -= OnDefaultDeviceChanged;
        _hideTimer.Stop();
        _hideTimer.Tick -= OnHideTimerTick;
        _window.Close();
    }
}
