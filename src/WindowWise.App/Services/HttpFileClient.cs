using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WindowWise.Models;

namespace WindowWise.Services
{
    public sealed class HttpFileClient
    {
        private static readonly HttpClient _client = new();

        public async Task<IReadOnlyList<SharedFileInfo>> GetFilesAsync(
            DiscoveredDevice device,
            CancellationToken cancellationToken = default)
        {
            Uri uri = CreateUri(device, "/api/files");
            var files = await _client.GetFromJsonAsync<List<SharedFileInfo>>(uri, cancellationToken);

            if (files == null)
                throw new JsonException("Server returned an empty file list.");

            return files;
        }

        public async Task DownloadFileAsync(
            DiscoveredDevice device,
            Guid fileId,
            string destinationPath,
            CancellationToken cancellationToken = default)
        {
            Uri uri = CreateUri(device, $"/api/files/{fileId}");
            string fullPath = Path.GetFullPath(destinationPath);

            using var response = await _client.GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            string temporaryPath = Path.Combine(Path.GetDirectoryName(fullPath)!,
                $".{Guid.NewGuid():N}.download");

            try
            {
                await using (var incoming = await response.Content.ReadAsStreamAsync(cancellationToken))
                {
                    await using var output = new FileStream(temporaryPath, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

                    await incoming.CopyToAsync(output, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, fullPath);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }

        private static Uri CreateUri(DiscoveredDevice device, string path)
        {
            ArgumentNullException.ThrowIfNull(device);
            if (device.HttpPort < 1 || device.HttpPort > 65535)
                throw new ArgumentOutOfRangeException(nameof(device), "HTTP port must be between 1 and 65535.");

            return new UriBuilder(Uri.UriSchemeHttp, device.DiscoveryEndpoint.Address.ToString(),
                device.HttpPort, path).Uri;
        }
    }
}
