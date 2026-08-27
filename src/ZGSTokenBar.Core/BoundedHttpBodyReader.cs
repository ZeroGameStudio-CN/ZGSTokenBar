namespace ZGSTokenBar.Core;

internal static class BoundedHttpBodyReader
{
    internal const int MaximumBytes = 32 * 1024;

    public static async Task<byte[]> ReadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumBytes)
        {
            throw new InvalidDataException("Response too large.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var memory = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (memory.Length + read > MaximumBytes)
            {
                throw new InvalidDataException("Response too large.");
            }
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }
}
