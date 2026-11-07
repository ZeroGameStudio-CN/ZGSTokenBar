using System.Diagnostics;
using System.Text.Json;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

internal sealed class OtherToolsModule : IDisposable
{
    public const string GrokId = "zgstokenbar.provider.grok-local";
    public const string AntigravityId = "zgstokenbar.provider.antigravity-local";
    private readonly BarForm _bar;
    private readonly Action _changed;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1_000 };
    private readonly string _grokPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "bin", "grok.exe");
    private readonly string _antigravityPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe");
    private Form? _popup;
    private string? _popupId;
    private string _grokText = "额度不可读";
    private string _antigravityText = "读取中";
    private string[] _antigravityRows = [];
    private bool _chinese;
    private bool _grokEnabled;
    private bool _antigravityEnabled;
    private bool _refreshing;
    private bool _disposed;

    public OtherToolsModule(BarForm bar, Action changed)
    {
        _bar = bar;
        _changed = changed;
        _bar.PluginPopoverRequested += OnPopoverRequested;
        _timer.Tick += (_, _) => { _timer.Interval = 120_000; _ = RefreshAsync(); };
        _timer.Start();
    }

    public IReadOnlyList<PluginMiniCardView> Cards(bool chinese, bool grokEnabled, bool antigravityEnabled)
    {
        _chinese = chinese;
        if ((!_grokEnabled && grokEnabled) || (!_antigravityEnabled && antigravityEnabled))
            _timer.Interval = 1_000;
        _grokEnabled = grokEnabled;
        _antigravityEnabled = antigravityEnabled;
        var cards = new List<PluginMiniCardView>(2);
        if (grokEnabled && File.Exists(_grokPath)) cards.Add(Card(GrokId, "Grok", chinese ? _grokText : "Quota unavailable", 900));
        if (antigravityEnabled && File.Exists(_antigravityPath))
            cards.Add(Card(AntigravityId, "Antigravity", chinese ? _antigravityText : EnglishAntigravityText(), 910));
        return cards;
    }

    private static PluginMiniCardView Card(string id, string title, string value, int order) => new(
        id, title,
        new MiniCardContribution($"card.{id}", id, id, ContributionKind.Quota, order, title,
            $"{id}.icon", $"accent.{id}", [new("quota", new("text", Text: value))]),
        new Dictionary<string, string>(StringComparer.Ordinal));

    private string EnglishAntigravityText() => _antigravityText switch
    {
        "读取中" => "Loading",
        "额度不可读" => "Quota unavailable",
        _ => _antigravityText,
    };

    private async Task RefreshAsync()
    {
        if (_refreshing || _disposed) return;
        _refreshing = true;
        try
        {
            var grok = _grokEnabled && File.Exists(_grokPath) ? Run(_grokPath, "usage", "--json") : Task.FromResult<string?>(null);
            var antigravity = _antigravityEnabled && File.Exists(_antigravityPath)
                ? Run(_antigravityPath, "-p", "/usage", "--print-timeout", "20s")
                : Task.FromResult<string?>(null);
            var grokOutput = await grok;
            var antigravityOutput = await antigravity;
            if (_disposed) return;
            _grokText = ParseGrok(grokOutput) ?? "额度不可读";
            _antigravityRows = ParseAntigravity(antigravityOutput);
            _antigravityText = _antigravityRows.Length == 0 ? "额度不可读" : _antigravityRows[0];
            _changed();
            UpdatePopup();
        }
        finally { _refreshing = false; }
    }

    private static async Task<string?> Run(string executable, params string[] args)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            } };
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start()) return null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); return null; }
            if (process.ExitCode != 0) return null;
            var output = await process.StandardOutput.ReadToEndAsync();
            return output.Length <= 16_384 ? output : null;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    internal static string? ParseGrok(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("remainingPercent", out var percent)
                && percent.TryGetDouble(out var value) && value is >= 0 and <= 100)
                return $"{value:0.#}%";
        }
        catch (JsonException) { }
        return null;
    }

    internal static string[] ParseAntigravity(string? output) =>
        (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length >= 3 && parts[1].Contains("Weekly Limit Remaining", StringComparison.Ordinal))
            .Select(parts => $"{parts[0]} {parts[2]}")
            .Take(2)
            .ToArray();

    private void OnPopoverRequested(object? sender, PluginPopoverRequest request)
    {
        if (request.PluginId is not (GrokId or AntigravityId)) return;
        _popup?.Dispose();
        _popupId = request.PluginId;
        _popup = new Form
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            BackColor = Color.FromArgb(28, 33, 43), ForeColor = Color.FromArgb(230, 235, 244),
            ClientSize = new Size((int)(285 * request.Scale), (int)(92 * request.Scale)),
        };
        var title = request.PluginId == GrokId ? "Grok" : "Antigravity";
        _popup.Controls.Add(new Label
        {
            Text = title, Location = new Point(14, 7), Size = new Size(_popup.Width - 28, 24),
            Font = new Font("Segoe UI", 11f * request.Scale, FontStyle.Bold, GraphicsUnit.Pixel),
        });
        _popup.Controls.Add(new Label
        {
            Location = new Point(14, 34), Size = new Size(_popup.Width - 28, _popup.Height - 39),
            Font = new Font("Segoe UI", 10f * request.Scale, FontStyle.Regular, GraphicsUnit.Pixel),
        });
        UpdatePopup();
        var screen = Screen.FromRectangle(request.Anchor).WorkingArea;
        _popup.Location = new Point(Math.Clamp(request.Anchor.Left, screen.Left, Math.Max(screen.Left, screen.Right - _popup.Width)),
            request.Anchor.Top - _popup.Height >= screen.Top ? request.Anchor.Top - _popup.Height : request.Anchor.Bottom);
        _popup.Show(_bar);
        request.Popup = _popup;
    }

    private void UpdatePopup()
    {
        if (_popup is not { IsDisposed: false } || _popup.Controls.Count < 2) return;
        _popup.Controls[1].Text = _popupId == GrokId
            ? _chinese ? _grokText : "Quota unavailable in this Grok CLI"
            : _antigravityRows.Length == 0
                ? _chinese ? _antigravityText : EnglishAntigravityText()
                : string.Join(Environment.NewLine, _antigravityRows);
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        _bar.PluginPopoverRequested -= OnPopoverRequested;
        _popup?.Dispose();
    }
}
