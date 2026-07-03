using System;
using System.Collections.Generic;
using System.Text;
using WindowWise.Models;
namespace WindowWise.Services;

public interface IAudioDeviceService
{
    AudioDeviceInfo? GetDefaultOutputDevice();
}
