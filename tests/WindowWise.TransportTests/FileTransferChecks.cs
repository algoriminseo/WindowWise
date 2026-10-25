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
            await using var filesA = new WebRtcFileTransferService(peerA);
            await using var filesB = new WebRtcFileTransferService(peerB);
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

    // Verify rejection and cancellation surface as the intended exception type.
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
