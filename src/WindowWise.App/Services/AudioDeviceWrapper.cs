using System;
using System.ComponentModel;
using System.Threading;
using NAudio.CoreAudioApi;

namespace WindowWise.Services
{
    public sealed class AudioDeviceWrapper : IDisposable, INotifyPropertyChanged
    {
        private readonly AudioEndpointVolume _endpointVolume;
        private readonly SynchronizationContext? _synchronizationContext;
        private float _volume;
        private bool _disposed;

        public MMDevice Device { get; }
        public string Id => Device.ID;
        public string Name => Device.FriendlyName;

        public float Volume
        {
            get => _volume;
            set
            {
                if (_disposed)
                    return;

                float clampedVolume = Math.Clamp(value, 0f, 100f);
                if (Math.Abs(_volume - clampedVolume) < 0.01f)
                    return;

                _endpointVolume.MasterVolumeLevelScalar = clampedVolume / 100f;
                UpdateVolume(clampedVolume);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public AudioDeviceWrapper(MMDevice device)
        {
            ArgumentNullException.ThrowIfNull(device);

            Device = device;
            _endpointVolume = device.AudioEndpointVolume;
            _volume = _endpointVolume.MasterVolumeLevelScalar * 100f;
            _synchronizationContext = SynchronizationContext.Current;

            _endpointVolume.OnVolumeNotification += OnVolumeNotification;
        }

        private void OnVolumeNotification(AudioVolumeNotificationData data)
        {
            if (_disposed)
                return;

            UpdateVolume(data.MasterVolume * 100f);
        }

        private void UpdateVolume(float volume)
        {
            _volume = volume;
            RaisePropertyChanged(nameof(Volume));
        }

        private void RaisePropertyChanged(string propertyName)
        {

            if (_synchronizationContext is not null)
                _synchronizationContext.Post(_ => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)), null);
            else
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _endpointVolume.OnVolumeNotification -= OnVolumeNotification;
            _endpointVolume.Dispose();
            Device.Dispose();
        }
    }
}
