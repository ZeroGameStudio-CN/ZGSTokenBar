using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed class CodexEconomySettingsPanel : Panel
{
    private const int LogicalHeight = 410;
    private readonly NativeText _text;
    private readonly float _scale;
    private readonly IReadOnlyList<CodexEconomyProfile> _profiles;
    private readonly Func<CodexEconomyProfile, CodexEconomyStatus> _inspect;
    private readonly Color _content;
    private readonly Color _surface;
    private readonly Color _textColor;
    private readonly Color _muted;
    private readonly Color _accent;
    private readonly Color _warning;
    private readonly Label _heading;
    private readonly Label _description;
    private readonly Label _profileLabel;
    private readonly ComboBox _profile;
    private readonly Label _statusLabel;
    private readonly Label _status;
    private readonly Label _policy;
    private readonly Label _configLabel;
    private readonly TextBox _configPath;
    private readonly Label _skillLabel;
    private readonly TextBox _skillPath;
    private readonly Button _refresh;

    internal CodexEconomySettingsPanel(
        NativeText text,
        int targetDpi,
        bool renderOnly = false,
        IReadOnlyList<CodexEconomyProfile>? profiles = null,
        Func<CodexEconomyProfile, CodexEconomyStatus>? inspect = null)
    {
        if (renderOnly && (profiles is null || inspect is null))
            throw new ArgumentException("Render-only assistant panels require injected profiles and inspection.");

        _text = text;
        _scale = Math.Max(1, targetDpi / 96f);
        var router = new CodexEconomyRouter();
        _profiles = (profiles ?? CodexEconomyRouter.DiscoverProfiles()).ToArray();
        _inspect = inspect ?? router.Inspect;
        if (SystemInformation.HighContrast)
        {
            _content = SystemColors.Window;
            _surface = SystemColors.Control;
            _textColor = SystemColors.WindowText;
            _muted = SystemColors.GrayText;
            _accent = SystemColors.Highlight;
            _warning = SystemColors.HotTrack;
        }
        else
        {
            _content = Color.FromArgb(24, 24, 28);
            _surface = Color.FromArgb(31, 31, 35);
            _textColor = Color.FromArgb(242, 243, 245);
            _muted = Color.FromArgb(160, 164, 173);
            _accent = Color.FromArgb(76, 141, 255);
            _warning = Color.FromArgb(229, 163, 59);
        }

        BackColor = _content;
        ForeColor = _textColor;
        Height = Scale(LogicalHeight);
        AccessibleName = _text.CodexEconomyDialogTitle;
        Tag = "settings.economy.panel";

        _heading = CreateLabel(_text.CodexEconomyDialogTitle, 15f, FontStyle.Bold, _textColor, "economy.heading");
        _description = CreateLabel(_text.CodexEconomyDialogDescription, 9f, FontStyle.Regular, _muted, "economy.description");
        _profileLabel = CreateLabel(_text.CodexEconomyProfileLabel, 9f, FontStyle.Bold, _textColor, "economy.profile.label");
        _profile = new ComboBox
        {
            BackColor = _surface,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Font = FontAt(9f),
            ForeColor = _textColor,
            IntegralHeight = false,
            Tag = "economy.profile",
            AccessibleName = _text.CodexEconomyProfileLabel,
            AccessibleDescription = _text.CodexEconomyProfileHint,
            TabIndex = 0,
        };
        _statusLabel = CreateLabel(_text.CodexEconomyCurrentStatus, 9f, FontStyle.Bold, _textColor, "economy.status.label");
        _status = CreateLabel(_text.CodexEconomyStatusSummary(null), 9f, FontStyle.Regular, _textColor, "economy.status");
        _status.AutoEllipsis = true;
        _policy = CreateLabel(_text.CodexEconomyTaskHint, 8.5f, FontStyle.Regular, _muted, "economy.policy");
        _configLabel = CreateLabel(_text.CodexEconomyConfigPath, 8.5f, FontStyle.Bold, _textColor, "economy.config.label");
        _configPath = CreateReadOnlyPath(_text.CodexEconomyConfigPath, 1, "economy.config.path");
        _skillLabel = CreateLabel(_text.CodexEconomySkillPath, 8.5f, FontStyle.Bold, _textColor, "economy.skill.label");
        _skillPath = CreateReadOnlyPath(_text.CodexEconomySkillPath, 2, "economy.skill.path");
        _refresh = new Button
        {
            BackColor = _accent,
            FlatStyle = FlatStyle.Flat,
            Font = FontAt(9f, FontStyle.Bold),
            ForeColor = Color.White,
            Text = _text.CodexEconomyRefresh,
            UseVisualStyleBackColor = false,
            Tag = "economy.refresh",
            AccessibleName = _text.CodexEconomyRefresh,
            AccessibleDescription = _text.CodexEconomyRefreshHint,
            TabIndex = 3,
        };
        Controls.AddRange([
            _heading, _description, _profileLabel, _profile, _statusLabel, _status,
            _policy, _configLabel, _configPath, _skillLabel, _skillPath, _refresh,
        ]);

        _profile.SelectedIndexChanged += (_, _) => LoadSelectedProfile();
        _refresh.Click += (_, _) => LoadSelectedProfile();
        foreach (var profile in _profiles)
            _profile.Items.Add(new ProfileChoice(profile, _text.CodexEconomyProfileChoice(profile)));
        if (_profile.Items.Count > 0)
        {
            var recommended = _profiles.Select((profile, index) => (profile, index)).FirstOrDefault(item => item.profile.Recommended);
            _profile.SelectedIndex = recommended.profile is null ? 0 : recommended.index;
        }
        else
        {
            ShowNoProfiles();
        }
        UpdateRefreshEnabled();
    }

    internal CodexEconomyStatus? CurrentStatus { get; private set; }
    internal IReadOnlyList<CodexEconomyProfile> AvailableProfiles => _profiles;
    internal CodexEconomyProfile? SelectedProfile => (_profile.SelectedItem as ProfileChoice)?.Profile;
    internal string CurrentStatusText => _status.Text;
    internal void RefreshStatus() => LoadSelectedProfile();

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_heading is null || _refresh is null) return;
        var width = Math.Max(1, ClientSize.Width);
        _heading.SetBounds(0, 0, width, Scale(34));
        _description.SetBounds(0, Scale(34), width, Scale(48));
        _profileLabel.SetBounds(0, Scale(90), width, Scale(22));
        _profile.SetBounds(0, Scale(114), width, Scale(36));
        var statusWidth = Math.Max(1, width - Scale(144));
        _statusLabel.SetBounds(0, Scale(168), statusWidth, Scale(22));
        _status.SetBounds(0, Scale(192), statusWidth, Scale(26));
        _refresh.SetBounds(Math.Max(0, width - Scale(128)), Scale(178), Scale(128), Scale(34));
        _policy.SetBounds(0, Scale(223), width, Scale(44));
        _configLabel.SetBounds(0, Scale(280), width, Scale(21));
        _configPath.SetBounds(0, Scale(304), width, Scale(34));
        _skillLabel.SetBounds(0, Scale(348), width, Scale(21));
        _skillPath.SetBounds(0, Scale(372), width, Scale(34));
    }

    private void LoadSelectedProfile()
    {
        var profile = SelectedProfile;
        if (profile is null) { ShowNoProfiles(); return; }
        _configPath.Text = profile.ConfigPath;
        _skillPath.Text = profile.SkillPath;
        _configPath.AccessibleDescription = profile.ConfigPath;
        _skillPath.AccessibleDescription = profile.SkillPath;
        try { ShowStatus(_inspect(profile)); }
        catch (Exception exception)
        {
            CurrentStatus = null;
            _status.Text = _text.CodexEconomyReadFailed(exception.Message);
            _status.AccessibleName = _status.Text;
            _status.ForeColor = _warning;
        }
        UpdateRefreshEnabled();
    }

    private void ShowStatus(CodexEconomyStatus status)
    {
        CurrentStatus = status;
        _status.Text = _text.CodexEconomyStatusSummary(status);
        _status.AccessibleName = _status.Text;
        _status.ForeColor = status.Ready ? _textColor : _warning;
    }

    private void ShowNoProfiles()
    {
        CurrentStatus = null;
        _profile.Enabled = false;
        _configPath.Text = string.Empty;
        _skillPath.Text = string.Empty;
        _status.Text = _text.CodexEconomyNoProfiles;
        _status.AccessibleName = _status.Text;
        _status.ForeColor = _warning;
        UpdateRefreshEnabled();
    }

    private void UpdateRefreshEnabled() => _refresh.Enabled = SelectedProfile is not null;

    private TextBox CreateReadOnlyPath(string name, int tabIndex, string tag) => new()
    {
        BackColor = _surface, BorderStyle = BorderStyle.FixedSingle, Font = FontAt(8.5f),
        ForeColor = _textColor, ReadOnly = true, ShortcutsEnabled = true,
        Tag = tag, AccessibleName = name, TabIndex = tabIndex, TabStop = true,
    };

    private Label CreateLabel(string text, float points, FontStyle style, Color color, string tag) => new()
    {
        AutoSize = false, BackColor = _content, Font = FontAt(points, style), ForeColor = color,
        Text = text, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false,
        AccessibleName = text, Tag = tag,
    };

    private Font FontAt(float points, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", Math.Max(1, points * 96f / 72f * _scale), style, GraphicsUnit.Pixel);
    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * _scale));
    private sealed record ProfileChoice(CodexEconomyProfile Profile, string Label)
    {
        public override string ToString() => Label;
    }
}
