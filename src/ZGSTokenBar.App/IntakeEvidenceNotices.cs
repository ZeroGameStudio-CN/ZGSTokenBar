using System.Security.Cryptography;
using System.Text;

namespace ZGSTokenBar.App;

internal sealed class IntakeEvidenceNotices(string dataDirectory)
{
    internal const int MaximumEntries = 1024;
    private readonly string _path = Path.Combine(dataDirectory, "intake-evidence-notices.v1");
    private HashSet<string>? _sent;
    private List<string>? _order;

    internal IntakeEvidenceRequest? TakeNext(IEnumerable<IntakeEvidenceRequest> requests)
    {
        foreach (var request in requests)
        {
            EnsureLoaded();
            var question = request.Question.Trim().Normalize();
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{Guid.Parse(request.ItemId):D}\n{question}")));
            if (_sent!.Contains(key)) continue;

            // The tray application has one writer. Persist the marker before showing the balloon.
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            if (_order!.Count >= MaximumEntries)
            {
                while (_order.Count >= MaximumEntries)
                {
                    _sent.Remove(_order[0]);
                    _order.RemoveAt(0);
                }
                _order.Add(key);
                _sent.Add(key);
                RewriteFile();
            }
            else
            {
                File.AppendAllText(_path, key + "\n", Encoding.ASCII);
                _order.Add(key);
                _sent.Add(key);
            }
            return request;
        }
        return null;
    }

    private void EnsureLoaded()
    {
        if (_sent is not null && _order is not null) return;
        _sent = new HashSet<string>(StringComparer.Ordinal);
        _order = new List<string>();
        if (!File.Exists(_path)) return;
        foreach (var line in File.ReadLines(_path))
        {
            if (line.Length == 64 && line.All(Uri.IsHexDigit) && _sent.Add(line))
            {
                _order.Add(line);
            }
        }
    }

    private void RewriteFile()
    {
        var tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(tempPath, _order!, Encoding.ASCII);
            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { }
        }
    }
}
