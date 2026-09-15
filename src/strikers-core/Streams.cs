namespace Strikers.Core;

public static class Streams
{
    public static async Task<int> ReadWithinAsync(Stream body, Memory<byte> buffer, TimeSpan quiet,
                                                  CancellationToken cancel)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        window.CancelAfter(quiet);
        try
        {
            return await body.ReadAsync(buffer, window.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException($"nothing arrived for {quiet.TotalSeconds:0} s");
        }
    }

    public static async Task<byte[]?> ReadCappedAsync(Stream body, int cap, TimeSpan quiet, CancellationToken cancel)
    {
        using var kept = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await ReadWithinAsync(body, buffer, quiet, cancel);
            if (read == 0)
            {
                return kept.ToArray();
            }

            if (kept.Length + read > cap)
            {
                return null;
            }

            kept.Write(buffer, 0, read);
        }
    }
}
