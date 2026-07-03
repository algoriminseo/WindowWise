using System;
using System.Collections.Generic;
using System.Text;
using WindowWise.Models;
namespace WindowWise.Services;

public sealed class WindowsAudioDeviceService : IAudioDeviceService
{
    public AudioDeviceInfo? GetDefaultOutputDevice()
    {
        return null;
    }
}
