using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Transport.NamedPipe;

public static class ZgsPipeNaming
{
    public static string ForCurrentSession()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return ForSession(process.SessionId);
    }

    public static string ForSession(int sessionId) => $"ZGSTokenBar.v1.s{sessionId}";
}

internal static class PipeProtocol
{
    public static async ValueTask<T?> ReadAsync<T>(
        Stream stream,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        if (!await ReadExactOrEofAsync(stream, lengthBytes, cancellationToken)) return default;
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length is <= 0 or > ZgsHostApi.MaximumFrameBytes)
        {
            throw new InvalidDataException("Invalid frame length.");
        }
        var payload = new byte[length];
        if (!await ReadExactOrEofAsync(stream, payload, cancellationToken))
        {
            throw new EndOfStreamException();
        }
        return JsonSerializer.Deserialize(payload, typeInfo)
            ?? throw new JsonException("Frame payload is null.");
    }

    public static async ValueTask WriteAsync<T>(
        Stream stream,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        if (payload.Length is <= 0 or > ZgsHostApi.MaximumFrameBytes)
        {
            throw new InvalidDataException("Frame exceeds the protocol limit.");
        }
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async ValueTask<bool> ReadExactOrEofAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var current = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (current == 0) return read == 0;
            read += current;
        }
        return true;
    }
}
