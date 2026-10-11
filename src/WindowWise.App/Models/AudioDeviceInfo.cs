using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Linq;
using WindowWise.Services;
namespace WindowWise.Models
{
    public sealed class AudioDeviceInfo : IDisposable, INotifyPropertyChanged
    // 관리
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private readonly WindowsAudioDeviceService _AudioServiceNotifier;
        public event Action? DefaultDeviceChanged;
        public AudioDeviceWrapper? DefaultDevice {
            get => _DefaultDevice;
            set
            {
                //실제로 DefaultDevice를 변경할때는 DefaultDevice = ... 이런식으로 할당하지 않기.
                //TrySetDefaultOutputDevice를 사용해서 변경해야함.
                //이 setter는 UI에 DefaultDevice변경을 알려 Refresh해주기 위한 용도.
                //AudioDeviceInfo의 역할 자체가 NAudio에서 받아온 실제 device 정보를 Wrapper로 감싸고 메모리에 올려놓은 채 
                _DefaultDevice = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DefaultDevice)));
            }
        }
        private AudioDeviceWrapper? _DefaultDevice;
        public Dictionary<string, AudioDeviceWrapper> Devices {
            get => _Devices;
            set
            {
                _Devices = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Devices)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvailableDevices)));
            }
        }
        private Dictionary<string, AudioDeviceWrapper> _Devices;
        public Dictionary<string, AudioDeviceWrapper> AvailableDevices {
            get
            {
                return _Devices.Where(x => x.Value.Id != DefaultDevice?.Id).ToDictionary(x => x.Key, x=> x.Value);
            }
        }

        public AudioDeviceInfo()
        {
            //AudioDeviceInfo의 역할..
            //WindowsAudioDeviceService를 가진 채로 현재 디바이스 목록과 현재 기본 디바이스에 대한 정보를 관리.
            //디바이스 상태 변화를 구독하고 있다가 현재 디바이스 정보를 갱신함.
            _AudioServiceNotifier = new WindowsAudioDeviceService();
            _DefaultDevice = _AudioServiceNotifier.GetDefaultOutputDevice();
            _Devices = _AudioServiceNotifier.GetDevices();
            _AudioServiceNotifier.DeviceChanged += Refresh;
            _AudioServiceNotifier.DefaultDeviceChanged += OnDefaultDeviceChanged;
        }

        private void Refresh()
        {
            DefaultDevice = _AudioServiceNotifier.GetDefaultOutputDevice();
            Devices = _AudioServiceNotifier.GetDevices();
        }

        private void OnDefaultDeviceChanged()
        {
            var handler = DefaultDeviceChanged;
            if (handler == null) return;
            handler();
        }
        public bool TrySetDefaultOutputDevice(string deviceId)
        {
            return _AudioServiceNotifier.TrySetDefaultOutputDevice(deviceId);
        }




        void IDisposable.Dispose() {
            _AudioServiceNotifier.DeviceChanged -= Refresh;
            _AudioServiceNotifier.DefaultDeviceChanged -= OnDefaultDeviceChanged;
            _AudioServiceNotifier.Dispose();
        }

    }
}
