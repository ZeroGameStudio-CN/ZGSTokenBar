using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZGSTokenBar.Core;

// Derived parser state only. This file format never owns cumulative accounting or raw messages.
internal sealed class ConversationCheckpoint
{
    public string SourceKey { get; set; } = "";
    public string FileIdentity { get; set; } = "";
    public string SessionId { get; set; } = "";
    public long Offset { get; set; }
    public long Length { get; set; }
    public long WriteTicks { get; set; }
    public byte[] Prefix { get; set; } = [];
    public byte[] Boundary { get; set; } = [];
    public int SignatureLength { get; set; }
    public int CarryLength { get; set; }
    public byte[] CarryHash { get; set; } = [];
    public bool LineTooLong { get; set; }
    public CodexConversationUsageReader.TurnState? Active { get; set; }
    public CodexResponseSample[] Responses { get; set; } = [];
    public CodexTokenUsageEvent? PreviousUsage { get; set; }
    public DateTimeOffset? ResponseStart { get; set; }
    public DateTimeOffset LastActivity { get; set; }
    public bool Streaming { get; set; }
    public string Phase { get; set; } = "idle";
    public string? ProjectName { get; set; }
    public string? ServiceTier { get; set; }
}

[JsonSourceGenerationOptions(IncludeFields = true, GenerationMode = JsonSourceGenerationMode.Metadata, MaxDepth = 24)]
[JsonSerializable(typeof(ConversationCheckpoint))]
internal partial class ConversationCheckpointJsonContext : JsonSerializerContext;

internal sealed class ConversationCheckpointStore(string directory)
{
    private static ReadOnlySpan<byte> Magic => "ZGSCV01\n"u8;
    private const int HeaderBytes = 40;
    private const int MaximumCacheBytes = 512 * 1024;
    public int Failures { get; private set; }

    private static string Key(string source) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToUpperInvariant())));
    private string CachePath(string source) => Path.Combine(directory, Key(source) + ".checkpoint");
    public bool Exists(string source) => File.Exists(CachePath(source));

    public ConversationCheckpoint? Load(string source, FileStream stream, long writeTicks)
    {
        try
        {
            var state = Read(CachePath(source), out _, out var corrupt);
            if (corrupt) Failures++;
            if (state is null) return null;
            if (state.SourceKey != Key(source) || state.SessionId != CodexConversationUsageReader.SessionId(source)
                || !ValidState(state)) { Failures++; return null; }
            return MatchesSource(state, stream, writeTicks) ? state : null;
        }
        catch (Exception error) when (CacheError(error)) { Failures++; return null; }
    }

    public bool Save(string source, ConversationCheckpoint state)
    {
        try
        {
            if (!ValidState(state)) return false;
            var path = CachePath(source);
            state.SourceKey = Key(source);
            Directory.CreateDirectory(directory);
            using var lease = CredentialSupport.AcquireWriteLockFor(path);
            if (lease is null) { Failures++; return false; }
            // Reopen the path: an older reader's still-open handle may refer to a replaced file.
            using var stream = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var ticks = File.GetLastWriteTimeUtc(source).Ticks;
            if (!MatchesSource(state, stream, ticks)) return false;
            var current = Read(path, out var unsupported, out var corrupt);
            if (corrupt) Failures++;
            if (unsupported) return false; // An older app must preserve a future cache format.
            if (current is not null && ValidState(current) && current.SourceKey == state.SourceKey
                && MatchesSource(current, stream, ticks) && current.Offset >= state.Offset) return false;
            var payload = JsonSerializer.SerializeToUtf8Bytes(state, ConversationCheckpointJsonContext.Default.ConversationCheckpoint);
            if (payload.Length + HeaderBytes > MaximumCacheBytes) { Failures++; return false; }
            var digest = SHA256.HashData(payload);
            CredentialSupport.AtomicWriteLockedStream(path, output =>
            {
                output.Write(Magic); output.Write(digest); output.Write(payload);
            });
            return true;
        }
        catch (Exception error) when (CacheError(error)) { Failures++; return false; }
    }

    private static ConversationCheckpoint? Read(string path, out bool unsupported, out bool corrupt)
    {
        unsupported = false;
        corrupt = false;
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length < 8) { corrupt = true; return null; }
        Span<byte> header = stackalloc byte[HeaderBytes];
        stream.ReadExactly(header[..8]);
        if (!header[..8].SequenceEqual(Magic))
        {
            // Preserve only recognizable newer versions, not arbitrary damaged headers.
            unsupported = header[..5].SequenceEqual("ZGSCV"u8) && header[7] == (byte)'\n'
                && header[5] is >= (byte)'0' and <= (byte)'9' && header[6] is >= (byte)'0' and <= (byte)'9'
                && (header[5] - (byte)'0') * 10 + header[6] - (byte)'0' > 1;
            corrupt = !unsupported;
            return null;
        }
        if (stream.Length < HeaderBytes || stream.Length > MaximumCacheBytes) { corrupt = true; return null; }
        stream.ReadExactly(header[8..]);
        var payload = new byte[(int)stream.Length - HeaderBytes];
        stream.ReadExactly(payload);
        if (!CryptographicOperations.FixedTimeEquals(header[8..], SHA256.HashData(payload))) { corrupt = true; return null; }
        try
        {
            var state = JsonSerializer.Deserialize(payload, ConversationCheckpointJsonContext.Default.ConversationCheckpoint);
            corrupt = state is null;
            return state;
        }
        catch (JsonException) { corrupt = true; return null; }
    }

    private static bool MatchesSource(ConversationCheckpoint state, FileStream stream, long writeTicks)
    {
        if (stream.Length < state.Length || state.Offset > stream.Length
            || stream.Length == state.Length && writeTicks != state.WriteTicks
            || ConversationSourceIdentity.Read(stream) is not { } identity || identity != state.FileIdentity) return false;
        return Signature(stream, 0, state.SignatureLength).SequenceEqual(state.Prefix)
            && Signature(stream, state.Offset - state.SignatureLength, state.SignatureLength).SequenceEqual(state.Boundary)
            && (state.CarryLength == 0 || Signature(stream, state.Offset - state.CarryLength, state.CarryLength).SequenceEqual(state.CarryHash));
    }

    internal static byte[] Signature(FileStream stream, long offset, int length)
    {
        stream.Position = offset;
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return SHA256.HashData(bytes);
    }

    private static bool ValidState(ConversationCheckpoint s) =>
        s.Offset >= 0 && s.Length >= s.Offset && s.WriteTicks > 0 && s.WriteTicks <= DateTime.MaxValue.Ticks
        && s.SignatureLength == Math.Min(256, s.Offset) && s.Prefix is { Length: 32 } && s.Boundary is { Length: 32 }
        && s.CarryLength >= 0 && s.CarryLength <= CodexTokenUsageReader.MaximumLineBytes && s.CarryLength <= s.Offset
        && (s.CarryLength == 0 || s.CarryHash is { Length: 32 }) && (!s.LineTooLong || s.CarryLength == 0)
        && s.FileIdentity is { Length: > 0 and <= 128 } && s.SessionId is { Length: > 0 and <= 256 }
        && s.Phase is "idle" or "generating" or "tools" && s.ProjectName?.Length is not > 64
        && s.ServiceTier?.Length is not > 32 && s.Responses is { Length: <= 256 }
        && s.Responses.All(r => r is not null && r.SessionId == s.SessionId && r.Model?.Length is not > 128 && r.Output >= 0 && r.Start <= r.End)
        && (s.Active is null || s.Active.Id is { Length: > 0 and <= 256 } && s.Active.Model?.Length is not > 128
            && s.Active.Effort?.Length is not > 64 && s.Active.ServiceTier?.Length is not > 32
            && s.Active.ResponseModels is { Count: <= 8 } && s.Active.ResponseModels.All(m => m is { Length: > 0 and <= 128 })
            && s.Active.RoutedFromModel?.Length is not > 128 && s.Active.RoutedToModel?.Length is not > 128
            && s.Active.RoutingReason?.Length is not > 128 && s.Active.Output >= 0 && s.Active.Input >= 0
            && s.Active.Cached >= 0 && s.Active.Cached <= s.Active.Input && s.Active.CacheWrite >= 0 && s.Active.Cost >= 0);

    private static bool CacheError(Exception error) => error is IOException or UnauthorizedAccessException
        or JsonException or ArgumentException or NotSupportedException or System.Security.SecurityException;
}
