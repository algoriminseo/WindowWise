using System;
using System.Collections.Generic;
using System.Text;
using WindowWise.Models;
using NAudio.CoreAudioApi;
using System.Security.Cryptography.X509Certificates;
using System.CodeDom;
using NAudio.CoreAudioApi.Interfaces;
using System.Runtime.InteropServices.Marshalling;
using System.Numerics;
namespace WindowWise.Services;
// <summary>
// WindowsAudioDeviceService is responsible for calling NAudio, refreshing and storing the audio device list with the default audio device.
// This class works as an event source.

public sealed partial class WindowsAudioDeviceService : IAudioDeviceService, IDisposable
{
    private MMDeviceEnumerator _enumerator;
    private DeviceClient _notificationClient;
    private System.Threading.SynchronizationContext? syncContext;
    private bool _disposed;

    private readonly Dictionary<string, AudioDeviceWrapper> _deviceDict;

    public event Action? DeviceChanged;
    public WindowsAudioDeviceService()
    {
        _enumerator = new();
        _disposed = false;
        syncContext = System.Threading.SynchronizationContext.Current;
        _notificationClient = new DeviceClient(this);
        _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
        _deviceDict = new();
    }

    private void Raise()
    {
        // Observer / Publisher-Subscriber pattern
        // Other objects can subscribe to the DeviceChanged event and assign their own action to be executed.
        var handler = DeviceChanged;
        if (handler == null) return;
        if (syncContext != null)
        {
            syncContext.Post(_ => handler(), null);
        }
        else
        {
            handler();
        }
    }
    public AudioDeviceWrapper? GetDefaultOutputDevice()
    {
        MMDevice? device = _enumerator?.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        if (device is null) return null;
        if (_deviceDict.TryGetValue(device.ID, out AudioDeviceWrapper? wrapper))
        {
            device.Dispose();
            return wrapper;
        }
        else
        {
            _deviceDict[device.ID] = new AudioDeviceWrapper(device); 
            return _deviceDict[device.ID];
        }
    }

    public Dictionary<string, AudioDeviceWrapper> GetDevices()
    {
        var devices = _enumerator?.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        AddDevice(devices);
        RemoveDevices();
        return _deviceDict;
    }
    private void AddDevice(MMDeviceCollection? devices)
    {

        if (devices != null)
        {
            foreach (var device in devices)
            {
                //Add new device to the dictionary.
                if (!_deviceDict.TryGetValue(device.ID, out AudioDeviceWrapper? wrapper))
                {
                    _deviceDict[device.ID] = new AudioDeviceWrapper(device);
                }
                else
                {
                    device.Dispose();
                }

            }

        }
    }
    private void RemoveDevices()
    {
        //Remove inactive devices from the dictionary.
        List<String> tempVec = new();
        foreach (var device in _deviceDict.Values)
        {
            if (device.Device.State != DeviceState.Active)
            {
                tempVec.Add(device.Id);
                device.Dispose();
            }
        }
        foreach (var device in tempVec)
        {
            _deviceDict.Remove(device);
        }
    }
    public void Dispose()
    {
        if (!_disposed) {
            _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
            _disposed = true;
            foreach (var device in _deviceDict.Values)
            {
                device.Dispose();
            }
        }
        
    }
    //GeneratedComClass creates the rest part of DeviceClient class on its own in other files.
    //so 'partial' keyword must be used.
    [GeneratedComClass]
    private partial class DeviceClient : IMMNotificationClient
    {
        private readonly WindowsAudioDeviceService owner;
        public DeviceClient(WindowsAudioDeviceService owner)
        {
            this.owner = owner;
        }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => owner.Raise();
        public void OnDeviceAdded(string pwstrDeviceId) => owner.Raise();
        public void OnDeviceRemoved(string deviceId) => owner.Raise();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => owner.Raise();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { /* high frequency, ignore for refresh */ }
    }
}
