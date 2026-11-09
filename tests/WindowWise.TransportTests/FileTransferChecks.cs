using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using WindowWise.Services;

internal static class FileTransferChecks
{
    // Exercise the production file protocol over two real connected DataChannels.
    public static async Task RunAsync(WebRtcDataChannelService peerA, WebRtcDataChannelService peerB, CancellationToken token)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"WindowWise-transfer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var sharesA = new RemoteShareList();
            var sharesB = new RemoteShareList();
            await using var filesA = new WebRtcFileTransferService(peerA, sharesA);
            await using var filesB = new WebRtcFileTransferService(peerB, sharesB);
            var offersA = Channel.CreateUnbounded<RemoteFileOffer>();
            var offersB = Channel.CreateUnbounded<RemoteFileOffer>();
            var receivedA = Channel.CreateUnbounded<RemoteFileReceived>();
            var receivedB = Channel.CreateUnbounded<RemoteFileReceived>();
            var progressB = Channel.CreateUnbounded<RemoteTransferProgress>();
            filesA.FileOffered += (_, offer) => offersA.Writer.TryWrite(offer);
            filesB.FileOffered += (_, offer) => offersB.Writer.TryWrite(offer);
            filesA.FileReceived += (_, result) => receivedA.Writer.TryWrite(result);
            filesB.FileReceived += (_, result) => receivedB.Writer.TryWrite(result);
            filesB.ProgressChanged += (_, progress) => progressB.Writer.TryWrite(progress);

            foreach (int size in new[] { 0, 31, 1024 * 1024 + 19 })
            {
                byte[] data = new byte[size];
                Random.Shared.NextBytes(data);
                string source = Path.Combine(directory, $"한글 파일-{size}.bin");
                string destination = Path.Combine(directory, $"received-{size}.bin");
                await File.WriteAllBytesAsync(source, data, token);
                var sending = filesA.SendFileAsync(source, token);
                var offer = await offersB.Reader.ReadAsync(token);
                Check(!File.Exists(destination), "Receiver approval required before creating a file");
                await filesB.AcceptAsync(offer.Id, destination, token);
                await sending;
                var result = await receivedB.Reader.ReadAsync(token);
                Check(result.Size == size && (await File.ReadAllBytesAsync(destination, token)).SequenceEqual(data),
                    $"File transfer and SHA-256 verification: {size} bytes");
            }

            string reverseSource = Path.Combine(directory, "reverse.txt");
            string reverseDestination = Path.Combine(directory, "reverse-received.txt");
            await File.WriteAllTextAsync(reverseSource, "양방향 파일 전송 확인 👋", token);
            var reverseSending = filesB.SendFileAsync(reverseSource, token);
            var reverseOffer = await offersA.Reader.ReadAsync(token);
            await filesA.AcceptAsync(reverseOffer.Id, reverseDestination, token);
            await reverseSending;
            await receivedA.Reader.ReadAsync(token);
            Check(await File.ReadAllTextAsync(reverseDestination, token) == "양방향 파일 전송 확인 👋", "Reverse file transfer");

            var rejectedSending = filesA.SendFileAsync(reverseSource, token);
            var rejectedOffer = await offersB.Reader.ReadAsync(token);
            await filesB.CancelIncomingAsync(rejectedOffer.Id);
            await ExpectAsync<IOException>(() => rejectedSending);
            Check(true, "Rejected file is reported to sender");

            var overwriteSending = filesA.SendFileAsync(reverseSource, token);
            var overwriteOffer = await offersB.Reader.ReadAsync(token);
            await ExpectAsync<IOException>(() => filesB.AcceptAsync(overwriteOffer.Id, reverseDestination, token));
            await filesB.CancelIncomingAsync(overwriteOffer.Id);
            await ExpectAsync<IOException>(() => overwriteSending);
            Check(await File.ReadAllTextAsync(reverseDestination, token) == "양방향 파일 전송 확인 👋", "Existing destination is preserved");

            string canceledDestination = Path.Combine(directory, "canceled.bin");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            void CancelAfterChunk(object? _, RemoteTransferProgress progress)
            {
                if (progress.Bytes > 0 && progress.State == RemoteTransferState.Sending) cancellation.Cancel();
            }
            filesA.ProgressChanged += CancelAfterChunk;
            var canceledSending = filesA.SendFileAsync(Path.Combine(directory, "한글 파일-1048595.bin"), cancellation.Token);
            var canceledOffer = await offersB.Reader.ReadAsync(token);
            await filesB.AcceptAsync(canceledOffer.Id, canceledDestination, token);
            await ExpectAsync<OperationCanceledException>(() => canceledSending);
            filesA.ProgressChanged -= CancelAfterChunk;
            await WaitForStateAsync(progressB.Reader, canceledOffer.Id, RemoteTransferState.Canceled, token);
            Check(!File.Exists(canceledDestination) && Directory.GetFiles(directory, "*.partial").Length == 0,
                "Cancellation removes incomplete files");

            // Simulate a sender that completes a file with an incorrect hash.
            Guid invalidId = Guid.NewGuid();
            string invalidDestination = Path.Combine(directory, "invalid.bin");
            peerA.SendText(JsonSerializer.Serialize(new { Protocol = "windowwise-file-v1", Type = "offer", Id = invalidId, Name = "invalid.bin", Size = 1 }));
            var invalidOffer = await offersB.Reader.ReadAsync(token);
            await filesB.AcceptAsync(invalidOffer.Id, invalidDestination, token);
            byte[] chunk = new byte[25];
            invalidId.TryWriteBytes(chunk);
            BinaryPrimitives.WriteInt64LittleEndian(chunk.AsSpan(16), 0);
            chunk[24] = 42;
            peerA.SendBinary(chunk);
            peerA.SendText(JsonSerializer.Serialize(new { Protocol = "windowwise-file-v1", Type = "finish", Id = invalidId, Hash = "incorrect" }));
            await WaitForStateAsync(progressB.Reader, invalidId, RemoteTransferState.Failed, token);
            Check(!File.Exists(invalidDestination) && Directory.GetFiles(directory, "*.partial").Length == 0,
                "Hash mismatch prevents publishing a corrupt file");

            // Share list: the peer lists A's files and downloads one without an approval prompt.
            string shareDirectory = Path.Combine(directory, "shared");
            Directory.CreateDirectory(shareDirectory);
            byte[] sharedData = new byte[300 * 1024 + 7];
            Random.Shared.NextBytes(sharedData);
            string sharedPath = Path.Combine(shareDirectory, "공유 파일.bin");
            await File.WriteAllBytesAsync(sharedPath, sharedData, token);
            var shared = sharesA.Add(sharedPath);
            Check(sharesA.Add(sharedPath).Id == shared.Id, "Sharing the same file twice keeps one entry");
            // Long Korean names make the list larger than one 16 KiB DataChannel message.
            for (int i = 0; i < 40; i++)
            {
                string path = Path.Combine(shareDirectory, $"{new string('가', 200)}-{i}.txt");
                await File.WriteAllTextAsync(path, i.ToString(), token);
                sharesA.Add(path);
            }
            Check((await filesB.RequestFilesAsync(token)).Count == 0, "Files stay hidden until sharing starts");
            await ExpectCodeAsync("not-shared", () => filesB.DownloadAsync(shared, Path.Combine(directory, "early.bin"), token));
            var listChanged = Channel.CreateUnbounded<bool>();
            filesB.RemoteFilesChanged += (_, _) => listChanged.Writer.TryWrite(true);
            sharesA.IsSharing = true;
            await listChanged.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), token);
            Check(true, "The peer is told when sharing starts");
            Check(Throws<InvalidOperationException>(() => sharesA.Add(sharedPath)) && Throws<InvalidOperationException>(() => sharesA.Remove(shared.Id)),
                "The share list cannot change while sharing");
            var listed = await filesB.RequestFilesAsync(token);
            Check(listed.Count == 41 && listed.Count(file => file.Id == shared.Id && file.Name == "공유 파일.bin" &&
                file.Size == sharedData.Length) == 1, "The share list arrives across several pages with names and sizes");
            Check((await filesA.RequestFilesAsync(token)).Count == 0, "An empty share list is listed as empty");

            string downloadPath = Path.Combine(directory, "downloaded.bin");
            int offersBefore = offersB.Reader.Count;
            var downloaded = await filesB.DownloadAsync(shared, downloadPath, token);
            Check(downloaded.Size == sharedData.Length && (await File.ReadAllBytesAsync(downloadPath, token)).SequenceEqual(sharedData) &&
                offersB.Reader.Count == offersBefore, "A requested download completes without an approval prompt");
            await ExpectCodeAsync("destination-exists", () => filesB.DownloadAsync(shared, downloadPath, token));
            await ExpectCodeAsync("not-shared", () => filesB.DownloadAsync(new WindowWise.Models.SharedFileInfo(Guid.NewGuid(), "unknown.bin", 1),
                Path.Combine(directory, "unknown.bin"), token));
            sharesA.IsSharing = false;
            sharesA.Remove(shared.Id);
            sharesA.IsSharing = true;
            string removedPath = Path.Combine(directory, "removed.bin");
            await ExpectCodeAsync("not-shared", () => filesB.DownloadAsync(shared, removedPath, token));
            Check(!File.Exists(removedPath) && Directory.GetFiles(directory, "*.partial").Length == 0,
                "Unknown or removed files are refused without leaving files behind");
            var second = listed.First(file => file.Id != shared.Id);
            var secondDownload = await filesB.DownloadAsync(second, Path.Combine(directory, "second.txt"), token);
            Check(secondDownload.Size == second.Size, "Downloads work again after a refused request");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // Ignore progress from earlier transfers until the expected terminal phase arrives.
    private static async Task WaitForStateAsync(ChannelReader<RemoteTransferProgress> reader, Guid id, RemoteTransferState state, CancellationToken token)
    {
        while (true)
        {
            var progress = await reader.ReadAsync(token);
            if (progress.Id == id && progress.State == state) return;
        }
    }

    // Fail the runner immediately when a file-protocol assertion is not satisfied.
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {description}");
        Console.WriteLine($"PASS: {description}");
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return true; }
        return false;
    }

    // Verify a refused request surfaces its specific error code.
    private static async Task ExpectCodeAsync(string code, Func<Task> action)
    {
        try { await action(); }
        catch (RemoteFileTransferException exception) when (exception.Code == code)
        {
            Check(true, $"Download refused with {code}");
            return;
        }
        throw new InvalidOperationException($"Expected {code}.");
    }

    // Verify rejection and cancellation surface as the intended exception type.
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
