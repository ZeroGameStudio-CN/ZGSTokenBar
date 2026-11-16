using System.Security.Cryptography;

namespace ZGSTokenBar.Core;

public sealed partial class CodexConversationUsageReader
{
    private sealed partial class FileState
    {
        public ConversationCheckpoint Capture(string identity) => new()
        {
            FileIdentity = identity, SessionId = sessionId, Offset = Offset, Length = Length, WriteTicks = WriteTicks,
            Prefix = _prefix, Boundary = _checkpoint, SignatureLength = _signatureLength,
            CarryLength = LineLength, CarryHash = LineLength == 0 ? [] : SHA256.HashData(LineMemory.Span), LineTooLong = LineTooLong,
            Active = Active, Responses = Responses.ToArray(), PreviousUsage = _previousUsage,
            ResponseStart = _responseStart, LastActivity = _lastActivity, Streaming = _streaming, Phase = _phase,
            ProjectName = _projectName, ServiceTier = _serviceTier,
        };
        public static FileState Restore(ConversationCheckpoint saved, FileStream stream)
        {
            var state = new FileState(saved.SessionId)
            {
                Offset = saved.Offset, Length = saved.Length, WriteTicks = saved.WriteTicks,
                CaughtUp = saved.Offset >= stream.Length, Active = saved.Active, Responses = [.. saved.Responses],
                _prefix = saved.Prefix, _checkpoint = saved.Boundary, _signatureLength = saved.SignatureLength,
                _previousUsage = saved.PreviousUsage, _responseStart = saved.ResponseStart, _lastActivity = saved.LastActivity,
                _streaming = saved.Streaming, _phase = saved.Phase, _projectName = saved.ProjectName, _serviceTier = saved.ServiceTier,
            };
            if (saved.CarryLength > 0)
            {
                var carry = new byte[saved.CarryLength];
                stream.Position = saved.Offset - carry.Length;
                stream.ReadExactly(carry);
                state.AppendLine(carry);
            }
            state.LineTooLong = saved.LineTooLong;
            return state;
        }
    }

    private FileState? RestoreCheckpoint(string path, FileStream stream, long writeTicks)
    {
        if (_checkpoints?.Load(path, stream, writeTicks) is not { } saved) return null;
        var restored = FileState.Restore(saved, stream);
        RestoredSessions++;
        RestoredBytes += saved.Offset;
        return restored;
    }

    private void PersistCheckpoint(string path, FileState state, FileStream stream)
    {
        if (_checkpoints is null || ConversationSourceIdentity.Read(stream) is not { } identity) return;
        _checkpoints.Save(path, state.Capture(identity));
    }
}
