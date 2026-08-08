using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO.Packaging;
using System.Text;
using WindowWise.Models;
using WindowWise.Services;
namespace WindowWise.ViewModels
{
    public sealed class AudioManagerViewModel
    {
        public AudioDeviceInfo AudioInfo { get; } = new();
        public AudioPreset AudioPreset { get; }
        public AudioPresetInfo PresetInfo { get; }
        public AudioManagerViewModel()
        {
            AudioPreset = new AudioPreset(AudioInfo);
            PresetInfo = new AudioPresetInfo(AudioPreset);
        }
    }
}
