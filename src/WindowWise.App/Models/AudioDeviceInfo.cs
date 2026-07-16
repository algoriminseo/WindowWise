using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using WindowWise.Services;
namespace WindowWise.Models
{
    public sealed class AudioDeviceInfo : IDisposable, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private readonly WindowsAudioDeviceService _AudioServiceNotifier;
        public MMDevice? DefaultDevice {
            get => _DefaultDevice;
            private set
            {
                _DefaultDevice = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DefaultDevice)));
            }
        }
        private MMDevice? _DefaultDevice;
        public Dictionary<string, MMDevice>? Devices {
            get => _Devices;
            private set
            {
                _Devices = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Devices)));
            }
        }
        private Dictionary<string, MMDevice>? _Devices;
        public AudioDeviceInfo()
        {
            _AudioServiceNotifier = new WindowsAudioDeviceService();
            _DefaultDevice = _AudioServiceNotifier.GetDefaultOutputDevice();
            _Devices = _AudioServiceNotifier.GetDevices();
            _AudioServiceNotifier.DeviceChanged += Refresh;
        }

        private void Refresh()
        {
            DefaultDevice = _AudioServiceNotifier.GetDefaultOutputDevice();
            Devices = _AudioServiceNotifier.GetDevices();
        }




        void IDisposable.Dispose() {
            _AudioServiceNotifier.DeviceChanged -= Refresh;
            _AudioServiceNotifier.Dispose();
        }

    }
}
