using System.IO;

namespace WindowWise.Services
{

    /// <summary>
    /// Represents a session for sharing data or resources within the application.
    /// </summary>
    public sealed class ShareSession
    {

        public Guid Id { get; init; } = Guid.NewGuid();

        public required string Token { get; init; }

        public required string FilePath { get; init; }

        public string FileName => Path.GetFileName(FilePath);

        public string ContentType { get; init; } = "application/octet-stream";

        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

        public DateTimeOffset ExpirseAt { get; init; } = DateTimeOffset.Now.AddMinutes(30);
        public int DownloadCount { get; private set; } 

        public int MaxDownloads { get; init; } = 10;

        public bool IsStopped { get; private set; }

        public bool IsExpired => DateTimeOffset.Now >= ExpirseAt;

        public bool HasReachedDownloadLimit => DownloadCount >= MaxDownloads;

        public bool CanDownload => !IsStopped && !IsExpired && !HasReachedDownloadLimit && File.Exists(FilePath);

        public void RegisterDownload()
        {
            if (CanDownload)
            {
                DownloadCount++;
            }
        }

        public void Stop()
        {
            IsStopped = true;
        }

    }

}
