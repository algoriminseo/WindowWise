using System;
using System.Collections.Generic;
using System.Text;
using System.Net;

namespace WindowWise.Models
{
    public sealed class DiscoveredDevice
    {
        public required Guid DeviceId { get; init; }
        public required string DeviceName { get; init; }

        public required IPEndPoint DiscoveryEndpoint { get; init; }
        public required int HttpPort { get; init; }
        public required DateTimeOffset LastSeenAt { get; init; }
    }
}
