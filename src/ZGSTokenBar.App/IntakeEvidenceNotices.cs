using System.Security.Cryptography;
using System.Text;

namespace ZGSTokenBar.App;

internal sealed class IntakeEvidenceNotices(string dataDirectory)
{
    private readonly string _path = Path.Combine(dataDirectory, "intake-evidence-notices.v1");
    private HashSet<string>? _sent;

    internal IntakeEvidenceRequest? TakeNext(IEnumerable<IntakeEvidenceRequest> requests)
    {
        foreach (var request in requests)
        {
            _sent ??= File.Exists(_path)
                ? File.ReadLines(_path).Where(line => line.Length == 64 && line.All(Uri.IsHexDigit))
                    .ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            var question = request.Question.Trim().Normalize();
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{Guid.Parse(request.ItemId):D}\n{question}")));
            if (_sent.Contains(key)) continue;

            // The tray application has one writer. Persist the marker before showing the balloon.
            File.AppendAllText(_path, key + "\n", Encoding.ASCII);
            _sent.Add(key);
            return request;
        }
        return null;
    }
}
