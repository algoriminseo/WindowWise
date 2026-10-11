using System;
using System.Collections.Generic;
using System.Text;

namespace WindowWise.Models
{
    public sealed class DiscoveryMessage
    {
        public required string Protocol { get; init; }
        public required int Version { get; init; }
        public required string Type { get; init; }
        public required Guid RequestId { get; init; }

        public required Guid DeviceId { get; init; }
        public required string DeviceName { get; init; }
    }
}
