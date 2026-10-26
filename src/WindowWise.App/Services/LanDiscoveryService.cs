using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Threading;
using System.Windows.Forms;
using WindowWise.Models;

namespace WindowWise.Services
{
    internal class LanDiscoveryService : IAsyncDisposable
    {
        private UdpClient? _client;
        private readonly int _httpPort;
        private readonly JsonSerializerOptions _jsonO = new(JsonSerializerDefaults.Web);
        private Guid _deviceId = Guid.NewGuid();
        private System.Threading.SynchronizationContext? syncContext;
        private bool _isfinding;
        private bool _disposed = false;
        private CancellationTokenSource? _cts;
        private Task _runningTask = Task.CompletedTask;
        public Dictionary<Guid, DiscoveredDevice> DiscoveredDevice = new();
        public event Action? DeviceChanged;
        public LanDiscoveryService(int httpPort)
        {
            if (httpPort < 1 || httpPort > 65535)
                throw new ArgumentOutOfRangeException(nameof(httpPort));

            _httpPort = httpPort;
            syncContext = System.Threading.SynchronizationContext.Current;
            try {
                _client = new UdpClient(5000);
            }
            catch
            {
                _client?.Dispose();
                throw;
            }
            
        }

        public async Task FindDevicesAsync()
        {
            if (_client is null)
                throw new InvalidOperationException("No UDP socket available");
            if (_isfinding)
                return;
            _isfinding = true;
            _client?.EnableBroadcast = true;
            if (_cts != null) _cts.Dispose();
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            CancellationToken token = _cts.Token;
            
            Task receiver = ReceiveLoopAsync(token);
            Task sender = SendLoopAsync(token);
            _runningTask = Task.WhenAll(receiver, sender);
            try
            {
                await _runningTask;
            }
            finally {
                _isfinding = false;
                if (!_disposed)
                {
                    _client?.EnableBroadcast = false;
                } 
            }
            
           

        }
        private async Task<UdpReceiveResult> SendLoopAsync(CancellationToken t)
        {
            Guid requestId = Guid.NewGuid();
            var request = new DiscoveryMessage
            {
                Protocol = "drop",
                Version = 1,
                Type = "discover",
                RequestId = requestId,
                DeviceId = _deviceId,
                DeviceName = Environment.MachineName,
                HttpPort = _httpPort
            };
            byte[] requestB = JsonSerializer.SerializeToUtf8Bytes(request, _jsonO);
            while (true)
            {
                t.ThrowIfCancellationRequested();
                
                foreach (var endpoint in GetBroadcastEndpoints())
                {
                    await _client!.SendAsync(requestB.AsMemory(), endpoint, t);
                }

                await Task.Delay(3000, t);
            }
            
        }
        private static List<IPEndPoint> GetBroadcastEndpoints()
        {
            var endpoints = new List<IPEndPoint>();
            var addresses = new HashSet<IPAddress>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        unicast.PrefixLength is <= 0 or >= 31)
                        continue;

                    
                    byte[] address = unicast.Address.GetAddressBytes();
                    byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                    for (int i = 0; i < address.Length; i++)
                        address[i] = (byte)(address[i] | (byte)~mask[i]);

                    var broadcast = new IPAddress(address);
                    if (addresses.Add(broadcast))
                        endpoints.Add(new IPEndPoint(broadcast, 5000));
                }
            }
            return endpoints;
        }

        public async Task<UdpReceiveResult> ReceiveLoopAsync(CancellationToken t)
        {
            while (true) {
                t.ThrowIfCancellationRequested();
                UdpReceiveResult result = await _client.ReceiveAsync(t);
                DiscoveryMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<DiscoveryMessage>(result.Buffer, _jsonO);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (message is null ||
                    message.Protocol != "drop" ||
                    message.Version != 1 ||
                    message.RequestId == Guid.Empty ||
                    message.DeviceId == Guid.Empty ||
                    message.HttpPort < 1 || message.HttpPort > 65535 ||
                    string.IsNullOrWhiteSpace(message.DeviceName))
                {
                    continue;
                }
                if (message.DeviceId == _deviceId) continue;
                if (message.Type != "discover" && message.Type != "response") continue;
                DiscoveredDevice[message.DeviceId] = new DiscoveredDevice
                {
                    DeviceId = message.DeviceId,
                    DeviceName = message.DeviceName,
                    DiscoveryEndpoint = result.RemoteEndPoint,
                    HttpPort = message.HttpPort,
                    LastSeenAt = DateTime.UtcNow,
                };
                RaiseDeviceChanged();
                if (message.Type == "response") continue;

                var response = new DiscoveryMessage
                {
                    Protocol = "drop",
                    Version = 1,
                    Type = "response",
                    RequestId = message.RequestId,
                    DeviceId = _deviceId,
                    DeviceName = Environment.MachineName,
                    HttpPort = _httpPort
                };

                byte[] responseBytes =
                    JsonSerializer.SerializeToUtf8Bytes(response, _jsonO);

                
                await _client.SendAsync(
                    responseBytes.AsMemory(),
                    result.RemoteEndPoint,
                    t);
            }
            
        }

        public void RaiseDeviceChanged()
        {
            DeviceChanged?.Invoke();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _cts?.Cancel();
            try
            {
                await _runningTask;
            }
            finally
            {
                _client?.Dispose();
                _cts?.Dispose();
                _cts = null;
                _client = null;
                _disposed = true;
            }

        }
    }
}
