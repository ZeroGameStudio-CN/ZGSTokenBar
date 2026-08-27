using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed class CodexEconomySettingsPanel : Panel
{
    private const int LogicalHeight = 580;

    private readonly NativeText _text;
    private readonly float _scale;
    private readonly IReadOnlyList<CodexEconomyProfile> _profiles;
    private readonly Func<CodexEconomyProfile, CodexEconomyStatus> _inspect;
    private readonly Func<CodexEconomyProfile, CodexEconomyMode, CodexEconomyStatus>? _setMode;
    private readonly Color _content;
    private readonly Color _surface;
    private readonly Color _border;
    private readonly Color _textColor;
    private readonly Color _muted;
    private readonly Color _accent;
    private readonly Color _warning;
    private readonly Label _heading;
    private readonly Label _description;
    private readonly Label _profileLabel;
    private readonly ComboBox _profile;
    private readonly Label _modeLabel;
    private readonly Label _modeHint;
    private readonly RadioButton _off;
    private readonly RadioButton _ask;
    private readonly RadioButton _on;
    private readonly Label _statusLabel;
    private readonly Label _status;
    private readonly Label _namedLayers;
    private readonly Label _configLabel;
    private readonly TextBox _configPath;
    private readonly Label _skillLabel;
    private readonly TextBox _skillPath;
    private readonly Panel _footerPanel;
    private readonly Button _apply;
    private bool _loadingProfile;
    private bool _applying;

    internal CodexEconomySettingsPanel(
        NativeText text,
        int targetDpi,
        bool renderOnly = false,
        IReadOnlyList<CodexEconomyProfile>? profiles = null,
        Func<CodexEconomyProfile, CodexEconomyStatus>? inspect = null,
        Func<CodexEconomyProfile, CodexEconomyMode, CodexEconomyStatus>? setMode = null)
    {
        if (renderOnly && (profiles is null || inspect is null))
        {
            throw new ArgumentException("Render-only economy forms require injected profiles and an inspect function.");
        }

        _text = text;
        _scale = Math.Max(1, targetDpi / 96f);
        var router = new CodexEconomyRouter();
        _profiles = (profiles ?? CodexEconomyRouter.DiscoverProfiles()).ToArray();
        _inspect = inspect ?? router.Inspect;
        _setMode = renderOnly ? setMode : setMode ?? router.SetMode;

        if (SystemInformation.HighContrast)
        {
            _content = SystemColors.Window;
            _surface = SystemColors.Control;
            _border = SystemColors.WindowText;
            _textColor = SystemColors.WindowText;
            _muted = SystemColors.GrayText;
            _accent = SystemColors.Highlight;
            _warning = SystemColors.HotTrack;
        }
        else
        {
            _content = Color.FromArgb(24, 24, 28);
            _surface = Color.FromArgb(31, 31, 35);
            _border = Color.FromArgb(44, 44, 49);
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

        _heading = CreateLabel(_text.CodexEconomyDialogTitle, 15f, FontStyle.Bold, _textColor);
        _heading.Tag = "economy.heading";
        _description = CreateLabel(_text.CodexEconomyDialogDescription, 9f, FontStyle.Regular, _muted);
        _description.Tag = "economy.description";

        _profileLabel = CreateLabel(_text.CodexEconomyProfileLabel, 9f, FontStyle.Bold, _textColor);
        _profileLabel.Tag = "economy.profile.label";
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

        _modeLabel = CreateLabel(_text.CodexEconomyModeLabel, 9f, FontStyle.Bold, _textColor);
        _modeLabel.Tag = "economy.mode.label";
        _modeHint = CreateLabel(_text.CodexEconomyModeHint, 8.5f, FontStyle.Regular, _muted);
        _modeHint.Tag = "economy.mode.hint";
        _off = CreateModeOption(CodexEconomyMode.Off, 1);
        _ask = CreateModeOption(CodexEconomyMode.Ask, 2);
        _on = CreateModeOption(CodexEconomyMode.On, 3);

        _statusLabel = CreateLabel(_text.CodexEconomyCurrentStatus, 9f, FontStyle.Bold, _textColor);
        _statusLabel.Tag = "economy.status.label";
        _status = CreateLabel(_text.CodexEconomyStatusSummary(null), 9f, FontStyle.Regular, _textColor);
        _status.AutoEllipsis = true;
        _status.Tag = "economy.status";
        _namedLayers = CreateLabel(_text.CodexEconomyNamedLayersUnknown, 8.5f, FontStyle.Regular, _muted);
        _namedLayers.AutoEllipsis = true;
        _namedLayers.Tag = "economy.named-layers";

        _configLabel = CreateLabel(_text.CodexEconomyConfigPath, 8.5f, FontStyle.Bold, _textColor);
        _configLabel.Tag = "economy.config.label";
        _configPath = CreateReadOnlyPath(_text.CodexEconomyConfigPath, 4, "economy.config.path");
        _skillLabel = CreateLabel(_text.CodexEconomySkillPath, 8.5f, FontStyle.Bold, _textColor);
        _skillLabel.Tag = "economy.skill.label";
        _skillPath = CreateReadOnlyPath(_text.CodexEconomySkillPath, 5, "economy.skill.path");

        _footerPanel = new Panel
        {
            BackColor = _content,
            Tag = "economy.footer",
        };
        _apply = CreateButton(
            _text.CodexEconomyApply,
            _text.CodexEconomyApplyHint,
            primary: true,
            tabIndex: 6,
            tag: "economy.apply");
        _footerPanel.Controls.Add(_apply);

        Controls.Add(_heading);
        Controls.Add(_description);
        Controls.Add(_profileLabel);
        Controls.Add(_profile);
        Controls.Add(_modeLabel);
        Controls.Add(_modeHint);
        Controls.Add(_off);
        Controls.Add(_ask);
        Controls.Add(_on);
        Controls.Add(_statusLabel);
        Controls.Add(_status);
        Controls.Add(_namedLayers);
        Controls.Add(_configLabel);
        Controls.Add(_configPath);
        Controls.Add(_skillLabel);
        Controls.Add(_skillPath);
        Controls.Add(_footerPanel);

        _profile.SelectedIndexChanged += (_, _) => LoadSelectedProfile();
        _off.CheckedChanged += (_, _) => ModeSelectionChanged();
        _ask.CheckedChanged += (_, _) => ModeSelectionChanged();
        _on.CheckedChanged += (_, _) => ModeSelectionChanged();
        _apply.Click += (_, _) => ApplySelectedMode();

        foreach (var profile in _profiles)
        {
            _profile.Items.Add(new ProfileChoice(profile, _text.CodexEconomyProfileChoice(profile)));
        }
        if (_profile.Items.Count > 0)
        {
            var recommended = _profiles
                .Select((profile, index) => (profile, index))
                .FirstOrDefault(item => item.profile.Recommended);
            _profile.SelectedIndex = recommended.profile is null ? 0 : recommended.index;
        }
        else
        {
            ShowNoProfiles();
        }
        UpdateApplyEnabled();
    }

    internal CodexEconomyStatus? CurrentStatus { get; private set; }
    internal CodexEconomyStatus? AppliedStatus { get; private set; }
    internal event EventHandler? StatusChanged;
    internal IReadOnlyList<CodexEconomyProfile> AvailableProfiles => _profiles;
    internal CodexEconomyProfile? SelectedProfile => (_profile.SelectedItem as ProfileChoice)?.Profile;
    internal CodexEconomyMode? SelectedMode => _off.Checked
        ? CodexEconomyMode.Off
        : _ask.Checked
            ? CodexEconomyMode.Ask
            : _on.Checked
                ? CodexEconomyMode.On
                : null;
    internal string CurrentStatusText => _status.Text;
    internal string NamedLayersText => _namedLayers.Text;
    internal void RefreshStatus() => LoadSelectedProfile();

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_heading is null || _footerPanel is null) return;

        var inset = 0;
        var width = Math.Max(1, ClientSize.Width - inset * 2);
        _heading.SetBounds(inset, 0, width, Scale(34));
        _description.SetBounds(inset, Scale(34), width, Scale(42));
        _profileLabel.SetBounds(inset, Scale(84), width, Scale(22));
        _profile.SetBounds(inset, Scale(108), width, Scale(36));
        _modeLabel.SetBounds(inset, Scale(158), width, Scale(22));
        _modeHint.SetBounds(inset, Scale(180), width, Scale(24));
        var optionWidth = Scale(112);
        _off.SetBounds(inset, Scale(208), optionWidth, Scale(34));
        _ask.SetBounds(_off.Right + Scale(20), Scale(208), optionWidth, Scale(34));
        _on.SetBounds(_ask.Right + Scale(20), Scale(208), optionWidth, Scale(34));
        _statusLabel.SetBounds(inset, Scale(256), width, Scale(22));
        _status.SetBounds(inset, Scale(280), width, Scale(27));
        _namedLayers.SetBounds(inset, Scale(310), width, Scale(44));
        _configLabel.SetBounds(inset, Scale(364), width, Scale(21));
        _configPath.SetBounds(inset, Scale(388), width, Scale(34));
        _skillLabel.SetBounds(inset, Scale(432), width, Scale(21));
        _skillPath.SetBounds(inset, Scale(456), width, Scale(34));

        var footerHeight = Scale(64);
        _footerPanel.SetBounds(0, ClientSize.Height - footerHeight, ClientSize.Width, footerHeight);
        var buttonWidth = Scale(112);
        var buttonHeight = Scale(34);
        _apply.SetBounds(
            _footerPanel.ClientSize.Width - buttonWidth,
            Math.Max(0, (_footerPanel.ClientSize.Height - buttonHeight) / 2),
            buttonWidth,
            buttonHeight);
    }

    private void LoadSelectedProfile()
    {
        var profile = SelectedProfile;
        if (profile is null)
        {
            ShowNoProfiles();
            return;
        }

        _configPath.Text = profile.ConfigPath;
        _skillPath.Text = profile.SkillPath;
        _configPath.AccessibleDescription = profile.ConfigPath;
        _skillPath.AccessibleDescription = profile.SkillPath;
        try
        {
            ShowStatus(_inspect(profile));
        }
        catch (Exception exception)
        {
            CurrentStatus = null;
            SetModeSelection(null);
            _status.Text = _text.CodexEconomyReadFailed(exception.Message);
            _status.AccessibleName = _status.Text;
            _status.ForeColor = _warning;
            _namedLayers.Text = _text.CodexEconomyNamedLayersUnknown;
            _namedLayers.AccessibleName = _namedLayers.Text;
            _namedLayers.ForeColor = _warning;
        }
        UpdateApplyEnabled();
    }

    private void ShowStatus(CodexEconomyStatus status)
    {
        CurrentStatus = status;
        _status.Text = _text.CodexEconomyStatusSummary(status);
        _status.AccessibleName = _status.Text;
        _status.ForeColor = status.Mode == CodexEconomyMode.Inconsistent ? _warning : _textColor;
        _namedLayers.Text = _text.CodexEconomyNamedLayersDetail(status.HasNamedConfigLayers);
        _namedLayers.AccessibleName = _namedLayers.Text;
        _namedLayers.ForeColor = status.HasNamedConfigLayers ? _warning : _muted;
        SetModeSelection(status.Mode is CodexEconomyMode.Off or CodexEconomyMode.Ask or CodexEconomyMode.On
            ? status.Mode
            : null);
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
        _namedLayers.Text = _text.CodexEconomyNamedLayersUnknown;
        _namedLayers.AccessibleName = _namedLayers.Text;
        _namedLayers.ForeColor = _muted;
        SetModeSelection(null);
        UpdateApplyEnabled();
    }

    private void SetModeSelection(CodexEconomyMode? mode)
    {
        _loadingProfile = true;
        try
        {
            _off.Checked = mode == CodexEconomyMode.Off;
            _ask.Checked = mode == CodexEconomyMode.Ask;
            _on.Checked = mode == CodexEconomyMode.On;
        }
        finally
        {
            _loadingProfile = false;
        }
        UpdateApplyEnabled();
    }

    private void ModeSelectionChanged()
    {
        if (_loadingProfile) return;
        UpdateApplyEnabled();
    }

    private void UpdateApplyEnabled()
    {
        var hasProfile = SelectedProfile is not null;
        _off.Enabled = hasProfile && !_applying;
        _ask.Enabled = hasProfile && !_applying;
        _on.Enabled = hasProfile && !_applying;
        _apply.Enabled = hasProfile && SelectedMode is not null && _setMode is not null && !_applying;
    }

    private void ApplySelectedMode()
    {
        var profile = SelectedProfile;
        var mode = SelectedMode;
        var setMode = _setMode;
        if (profile is null || mode is null || setMode is null) return;

        SetApplying(true);
        try
        {
            setMode(profile, mode.Value);
            var readBack = _inspect(profile);
            if (readBack.Mode != mode.Value)
            {
                throw new CodexEconomyException(_text.CodexEconomyReadBackMismatch(mode.Value, readBack.Mode));
            }

            ShowStatus(readBack);
            AppliedStatus = readBack;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            LoadSelectedProfile();
            MessageBox.Show(
                this,
                _text.CodexEconomyApplyFailed(exception.Message),
                _text.CodexEconomyApplyFailedTitle,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsDisposed) SetApplying(false);
        }
    }

    private void SetApplying(bool applying)
    {
        _applying = applying;
        UseWaitCursor = applying;
        _profile.Enabled = !applying && _profiles.Count > 0;
        UpdateApplyEnabled();
    }

    private RadioButton CreateModeOption(CodexEconomyMode mode, int tabIndex) => new()
    {
        Appearance = Appearance.Button,
        AutoCheck = true,
        BackColor = _surface,
        FlatStyle = FlatStyle.Flat,
        Font = FontAt(9f, FontStyle.Bold),
        ForeColor = _textColor,
        Text = _text.CodexEconomyModeName(mode),
        TextAlign = ContentAlignment.MiddleCenter,
        UseVisualStyleBackColor = false,
        Tag = $"economy.mode.{mode.ToString().ToLowerInvariant()}",
        AccessibleName = _text.CodexEconomyModeName(mode),
        AccessibleDescription = _text.CodexEconomyModeDescription(mode),
        TabIndex = tabIndex,
        TabStop = true,
    };

    private TextBox CreateReadOnlyPath(string accessibleName, int tabIndex, string tag) => new()
    {
        BackColor = _surface,
        BorderStyle = BorderStyle.FixedSingle,
        Font = FontAt(8.5f),
        ForeColor = _textColor,
        ReadOnly = true,
        ShortcutsEnabled = true,
        Tag = tag,
        AccessibleName = accessibleName,
        TabIndex = tabIndex,
        TabStop = true,
    };

    private Button CreateButton(
        string text,
        string description,
        bool primary,
        int tabIndex,
        string tag) => new()
    {
        BackColor = primary ? _accent : _surface,
        FlatStyle = FlatStyle.Flat,
        Font = FontAt(9f, FontStyle.Bold),
        ForeColor = primary ? Color.White : _textColor,
        Text = text,
        UseVisualStyleBackColor = false,
        Tag = tag,
        AccessibleName = text,
        AccessibleDescription = description,
        TabIndex = tabIndex,
        TabStop = true,
    };

    private Label CreateLabel(string text, float points, FontStyle style, Color color) => new()
    {
        AutoSize = false,
        BackColor = _content,
        Font = FontAt(points, style),
        ForeColor = color,
        Text = text,
        TextAlign = ContentAlignment.MiddleLeft,
        UseMnemonic = false,
        AccessibleName = text,
    };

    private Font FontAt(float points, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", Math.Max(1, points * 96f / 72f * _scale), style, GraphicsUnit.Pixel);

    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * _scale));

    private sealed record ProfileChoice(CodexEconomyProfile Profile, string Label)
    {
        public override string ToString() => Label;
    }
}
