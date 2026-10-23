using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using WindowWise.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace WindowWise.Services
{
    public sealed class HttpFileServer
    {
        private WebApplication? _app;
        
        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

        public int HttpPort { get; }
        public Dictionary<string, SharedFileInfo> SharedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public event Action? DeviceChanged;

        public HttpFileServer(int httpPort)
        {
            if (httpPort < 1 || httpPort > 65535)
                throw new ArgumentOutOfRangeException(nameof(httpPort));

            HttpPort = httpPort;

        }
        private void Raise()
        {
            DeviceChanged?.Invoke();
        }
        public void SetSharedFiles(IEnumerable<string> filePaths)
        {

            foreach (var file in filePaths)
            {
                if (!File.Exists(file))
                    continue;

                var fileInfo = new FileInfo(file);
                string fullPath = fileInfo.FullName;

                Guid id;
                if (SharedFiles.TryGetValue(fullPath, out var existingFile))
                    id = existingFile.Id;
                else
                    id = Guid.NewGuid();

                SharedFiles[fullPath] = new SharedFileInfo(id, fileInfo.Name, fileInfo.Length);
            }
            Raise();
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycleLock.WaitAsync(cancellationToken);
            try
            {
                if (_app != null)
                    return;

                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
                {
                    Args = Array.Empty<string>(),
                    ContentRootPath = AppContext.BaseDirectory
                });

                
                builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(HttpPort));
                var app = builder.Build();
                app.MapGet("/api/files", GetFiles);
                app.MapGet("/api/files/{fileId:guid}", DownloadFile);
                try
                {
                    
                    await app.StartAsync(cancellationToken);
                    _app = app;
                }
                catch
                {
                    
                    await app.DisposeAsync();
                    throw;
                }
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycleLock.WaitAsync(cancellationToken);
            try
            {
                if (_app == null)
                    return;

                var app = _app;
                _app = null;
                try
                {
                    
                    await app.StopAsync(cancellationToken);
                }
                finally
                {
                    await app.DisposeAsync();
                }
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }
        private IResult GetFiles()
        {
            
            return Results.Json(new List<SharedFileInfo>(SharedFiles.Values));
        }
        private IResult DownloadFile(Guid fileId)
        {
            foreach (var entry in SharedFiles)
            {
                if (entry.Value.Id != fileId)
                    continue;

                try
                {
                    var stream = File.OpenRead(entry.Key);
                    
                    return Results.File(stream, "application/octet-stream", entry.Value.Name);
                }
                catch (FileNotFoundException)
                {
                    return Results.NotFound();
                }
                catch (DirectoryNotFoundException)
                {
                    return Results.NotFound();
                }
                catch (UnauthorizedAccessException)
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
                catch (IOException)
                {
                    return Results.Problem("파일을 읽을 수 없습니다.");
                }
            }

            return Results.NotFound();
        }
    }
}
