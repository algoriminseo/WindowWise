using System;
using System.Collections.Generic;
using System.Text;
using WindowWise.Models;
using NAudio.CoreAudioApi;
using System.Security.Cryptography.X509Certificates;
using System.CodeDom;
using NAudio.CoreAudioApi.Interfaces;
using System.Runtime.InteropServices.Marshalling;
namespace WindowWise.Services;
// <summary>
// WindowsAudioDeviceService is responsible for calling NAudio, refreshing and storing the audio device list with the default audio device.
// This class works as an event source.

public sealed partial class WindowsAudioDeviceService : IAudioDeviceService, IDisposable
{
    private MMDeviceEnumerator _enumerator;
    private DeviceClient _notificationClient;
    private System.Threading.SynchronizationContext? syncContext;
    private bool disposed;

    public event Action? DeviceChanged;
    public WindowsAudioDeviceService()
    {
        _enumerator = new();
        disposed = false;
        syncContext = System.Threading.SynchronizationContext.Current;
        _notificationClient = new DeviceClient(this);
        _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
        
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
    public MMDevice? GetDefaultOutputDevice()
    {
        return _enumerator?.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    public Dictionary<string, MMDevice> GetDevices()
    {
        var devices = _enumerator?.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        Dictionary<string, MMDevice> deviceDict = new();
        if (devices != null)
        {
            foreach (var device in devices)
            {
                deviceDict[device.ID] = device;
            }
        }
        return deviceDict;
    }
    public void Dispose()
    {
        if (!disposed) {
            _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
            disposed = true;
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
