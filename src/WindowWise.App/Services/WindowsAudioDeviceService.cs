using System;
using System.Collections.Generic;
using System.Text;
using WindowWise.Models;
using NAudio.CoreAudioApi;
using System.Security.Cryptography.X509Certificates;
namespace WindowWise.Services;
// <summary>
// WindowsAudioDeviceService is responsible for calling NAudio, refreshing and storing the audio device list with the default audio device.
// This class works as an event source.

public sealed class WindowsAudioDeviceService : IAudioDeviceService
{
    public MMDevice? GetDefaultOutputDevice()
    {
        var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    public Dictionary<string, MMDevice>? GetDevices()
    {
        var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        Dictionary<string, MMDevice> deviceDict = new();
        foreach (var device in devices)
        {
            deviceDict[device.ID] = device;
        }
        return deviceDict;
    }
}
