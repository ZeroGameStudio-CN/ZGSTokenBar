using System.Diagnostics;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

internal sealed class OtherToolsModule : IDisposable
{
    public const string Id = "zgstokenbar.local.other-tools";
    private readonly BarForm _bar;
    private readonly Action _changed;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5_000 };
    private Form? _popup;
    private ToolState _grok;
    private ToolState _antigravity;
    private bool _chinese;

    public OtherToolsModule(BarForm bar, Action changed)
    {
        _bar = bar;
        _changed = changed;
        Refresh();
        _bar.PluginPopoverRequested += OnPopoverRequested;
        _timer.Tick += (_, _) =>
        {
            var previous = (_grok, _antigravity);
            Refresh();
            if (previous != (_grok, _antigravity))
            {
                UpdatePopup();
                _changed();
            }
        };
        _timer.Start();
    }

    public PluginMiniCardView? Card(bool chinese)
    {
        _chinese = chinese;
        if (!_grok.Installed && !_antigravity.Installed) return null;
        var title = chinese ? "其他工具" : "Other tools";
        var summary = chinese
            ? $"G {StateText(_grok, true)} · A {StateText(_antigravity, true)}"
            : $"G {StateText(_grok, false)} · A {StateText(_antigravity, false)}";
        var card = new MiniCardContribution("card.other-tools", Id, "other-tools",
            ContributionKind.Metric, 900, title, "other-tools.icon", "accent.other-tools",
            [new("other-tools.state", new("text", Text: summary))]);
        return new PluginMiniCardView(Id, title, card, new Dictionary<string, string>(), null);
    }

    private static string StateText(ToolState state, bool chinese) => state.Running
        ? chinese ? "运行" : "on"
        : state.Installed ? chinese ? "未运行" : "off" : chinese ? "未安装" : "absent";

    private void Refresh()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _grok = new(File.Exists(Path.Combine(user, ".grok", "bin", "grok.exe")), IsRunning("grok"));
        _antigravity = new(File.Exists(Path.Combine(local, "Programs", "antigravity", "Antigravity.exe")), IsRunning("Antigravity"));
    }

    private static bool IsRunning(string name)
    {
        try
        {
            var processes = Process.GetProcessesByName(name);
            try { return processes.Length > 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch (InvalidOperationException) { return false; }
    }

    private void OnPopoverRequested(object? sender, PluginPopoverRequest request)
    {
        if (request.PluginId != Id) return;
        Refresh();
        _popup?.Dispose();
        _popup = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            BackColor = Color.FromArgb(28, 33, 43),
            ForeColor = Color.FromArgb(230, 235, 244),
            ClientSize = new Size((int)(220 * request.Scale), (int)(92 * request.Scale)),
        };
        var lines = new[] { ("Grok", _grok), ("Antigravity", _antigravity) };
        for (var index = 0; index < lines.Length; index++)
        {
            var (name, state) = lines[index];
            _popup.Controls.Add(new Label
            {
                Text = $"{name}    {StateText(state, _chinese)}",
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point((int)(14 * request.Scale), (int)((8 + 38 * index) * request.Scale)),
                Size = new Size((int)(192 * request.Scale), (int)(34 * request.Scale)),
                Font = new Font("Segoe UI", 10f * request.Scale, FontStyle.Regular, GraphicsUnit.Pixel),
            });
        }
        var screen = Screen.FromRectangle(request.Anchor).WorkingArea;
        _popup.Location = new Point(
            Math.Clamp(request.Anchor.Left, screen.Left, Math.Max(screen.Left, screen.Right - _popup.Width)),
            request.Anchor.Top - _popup.Height >= screen.Top ? request.Anchor.Top - _popup.Height : request.Anchor.Bottom);
        _popup.Show(_bar);
        request.Popup = _popup;
    }

    private void UpdatePopup()
    {
        if (_popup is not { IsDisposed: false }) return;
        var states = new[] { ("Grok", _grok), ("Antigravity", _antigravity) };
        for (var index = 0; index < states.Length && index < _popup.Controls.Count; index++)
        {
            var (name, state) = states[index];
            _popup.Controls[index].Text = $"{name}    {StateText(state, _chinese)}";
        }
    }

    public void Dispose()
    {
        _bar.PluginPopoverRequested -= OnPopoverRequested;
        _timer.Dispose();
        _popup?.Dispose();
    }

    private readonly record struct ToolState(bool Installed, bool Running);
}
