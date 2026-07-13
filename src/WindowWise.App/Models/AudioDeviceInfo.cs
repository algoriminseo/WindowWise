using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Text;
using WindowWise.Services;
namespace WindowWise.Models
{
    public sealed class AudioDeviceInfo : IDisposable
    {
        private readonly WindowsAudioDeviceService _AudioServiceNotifier;
        public MMDevice? DefaultDevice { get; private set; }
        public Dictionary<string, MMDevice> Devices { get; private set; } = new();
        public AudioDeviceInfo(string id, string name, bool isActive)
        {
            _AudioServiceNotifier = new WindowsAudioDeviceService();
            DefaultDevice = _AudioServiceNotifier.GetDefaultOutputDevice();
            Devices = _AudioServiceNotifier.GetDevices();
            _AudioServiceNotifier.DeviceChanged += Refresh;
        }

        public void Refresh()
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
