using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO.Packaging;
using System.Text;
using WindowWise.Models;
namespace WindowWise.ViewModels
{
    public sealed class AudioManagerViewModel
    {
        public AudioDeviceInfo AudioInfo { get; } = new();

        public AudioManagerViewModel()
        {
            
        }
    }
}
