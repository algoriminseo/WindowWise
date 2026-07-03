using System;
using System.Collections.Generic;
using System.Text;

namespace WindowWise.Models
{
    public sealed class AudioDeviceInfo
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public bool IsActive { get; init; }
    }
}
