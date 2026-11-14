using System.Drawing.Drawing2D;
using System.Reflection;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

internal sealed partial class SettingsForm : Form
{
    private static readonly HashSet<string> BuiltinPluginIds =
    [
        "zgstokenbar.metrics.system",
        "zgstokenbar.provider.claude",
        "zgstokenbar.provider.codex",
        "zgstokenbar.usage.codex-local",
    ];
    private const int LogicalClientWidth = 720;
    private const int LogicalClientHeight = 540;
    private const int LogicalNavigationWidth = 176;
    private const int LogicalCompactNavigationWidth = 72;
    private const int LogicalResponsiveBreakpoint = 640;
    private const int PhysicalSafeMargin = 16;
    private const int WmSetRedraw = 0x000B;
    private const int WmDpiChanged = 0x02E0;
    private const uint RedrawNowAllChildren = 0x0181;

    private readonly AppSettings _original;
    private readonly NativeText _text;
    private readonly SettingsTheme _theme;
    private readonly float _scale;
    private readonly bool _renderOnly;
    private readonly Size _desiredClientSize;
    private readonly string _baseTitle;
    private readonly TableLayoutPanel _bodyLayout;
    private readonly NavigationRail _navigation;
    private readonly Panel _pageHost;
    private readonly Dictionary<string, SettingsPage> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NavigationButton> _navigationButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<Control, Font> _designFonts = [];
    private readonly ToggleSwitch _systemMetrics;
    private readonly ToggleSwitch _claude;
    private readonly ToggleSwitch _codex;
    private readonly ToggleSwitch _grok;
    private readonly ToggleSwitch _antigravity;
    /// <summary>On/off for the other built-in usage providers (Cursor, Copilot, keyed APIs), by provider id.</summary>
    private readonly Dictionary<string, ToggleSwitch> _usageProviderToggles = new(StringComparer.Ordinal);
    /// <summary>Show-on-Bar switch per dock ring (provider or account), in dock order.</summary>
    private readonly List<(DockVisibilityEntry Entry, ToggleSwitch Toggle)> _dockItemToggles = [];
    private readonly ToggleSwitch _codexLocalUsage;
    private readonly ToggleSwitch _sub2ApiPool;
    private readonly ToggleSwitch _openAtLogin;
    private readonly ToggleSwitch _usageAlerts;
    private readonly ToggleSwitch _animations;
    private readonly ToggleSwitch _embedTaskbar;
    private readonly ToggleSwitch _refreshClaudeOAuth;
    private readonly SettingsComboBox _refreshMinutes;
    private readonly SettingsComboBox _locale;
    private readonly SettingsComboBox _antigravityGroup;
    private readonly PaletteGrid _paletteGrid;
    private readonly Dictionary<string, PaletteChoiceButton> _backgroundPaletteButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToggleSwitch> _externalPluginToggles = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<PluginStatus> _plugins;
    private readonly RoundedButton _save;
    private readonly Label _dirtyLabel;
    private SettingsPage _activePage = null!;
    private Icon? _windowIcon;
    private string _backgroundPalette;
    private bool _codexLocalUsageBeforeDisable;
    private bool _sub2ApiPoolBeforeDisable;
    private bool _lastCodexEnabled;
    private bool _updatingCodexDependency;
    private bool _allowClose;
    private bool _applyingLayout;

    public AppSettings? ResultSettings { get; private set; }

    public SettingsForm(
        AppSettings settings,
        int targetDpi,
        bool renderOnly = false,
        Rectangle? renderWorkingArea = null,
        IReadOnlyList<PluginStatus>? plugins = null,
        IReadOnlyList<DockVisibilityEntry>? dockItems = null,
        IReadOnlyList<string>? antigravityGroups = null)
    {
        if (!renderOnly && renderWorkingArea is not null)
        {
            throw new ArgumentException("A synthetic working area is only valid for render-only settings forms.", nameof(renderWorkingArea));
        }

        _original = Copy(settings);
        _plugins = plugins ?? [];
        foreach (var plugin in _plugins.Where(plugin => !BuiltinPluginIds.Contains(plugin.Manifest.Id)))
        {
            _original.SetPluginEnabled(plugin.Manifest.Id, plugin.Enabled, explicitChoice: false);
        }
        _text = NativeText.For(settings.Locale);
        _theme = SettingsTheme.Create();
        _scale = Math.Max(1, targetDpi / 96f);
        _renderOnly = renderOnly;
        _desiredClientSize = new Size(Scale(LogicalClientWidth), Scale(LogicalClientHeight));
        _backgroundPalette = AppSettings.NormalizeBackgroundPalette(settings.BackgroundPalette);
        _baseTitle = _text.SettingsTitle;

        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        BackColor = _theme.Content;
        ForeColor = _theme.Text;
        ClientSize = _desiredClientSize;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = false;
        StartPosition = FormStartPosition.Manual;
        Text = _baseTitle;
        KeyPreview = true;
        _windowIcon = TryLoadWindowIcon();
        if (_windowIcon is not null)
        {
            Icon = _windowIcon;
            ShowIcon = true;
        }
        else
        {
            ShowIcon = false;
        }

        if (renderWorkingArea is { } syntheticArea)
        {
            Size = ConstrainOuterSize(SizeFromClientSize(_desiredClientSize), syntheticArea, PhysicalSafeMargin);
        }
        if (_renderOnly) Location = new Point(-20_000, -20_000);

        _locale = CreateComboBox(_text.Language, _text.LanguageHint);
        _locale.Items.Add(new LocaleChoice("zh-CN", _text.ChineseLanguage));
        _locale.Items.Add(new LocaleChoice("en", _text.EnglishLanguage));
        _locale.SelectedItem = _locale.Items
            .Cast<LocaleChoice>()
            .First(choice => string.Equals(choice.Locale, settings.Locale, StringComparison.Ordinal));

        _refreshMinutes = CreateComboBox(_text.AutomaticRefresh, _text.AutomaticRefreshHint);
        foreach (var minutes in new[] { 5, 10, 30, 60 })
        {
            _refreshMinutes.Items.Add(new RefreshChoice(minutes, _text.RefreshChoice(minutes)));
        }
        _refreshMinutes.SelectedItem = _refreshMinutes.Items
            .Cast<RefreshChoice>()
            .FirstOrDefault(choice => choice.Minutes == settings.RefreshMinutes)
            ?? _refreshMinutes.Items[0];

        _antigravityGroup = CreateComboBox(AntigravityGroupTitle, AntigravityGroupHint);
        _antigravityGroup.Tag = "settings.antigravity.primary-group";
        {
            var current = string.IsNullOrWhiteSpace(settings.AntigravityPrimaryGroup)
                ? AntigravityGroups.DefaultPrimary
                : settings.AntigravityPrimaryGroup.Trim();
            var auto = new AntigravityGroupChoice(AntigravityGroups.Auto, _text.IsChinese ? "自动（剩余最多的组）" : "Auto (most remaining)");
            _antigravityGroup.Items.Add(auto);
            AntigravityGroupChoice? selected = AntigravityGroups.IsAuto(current) ? auto : null;
            foreach (var group in antigravityGroups ?? [])
            {
                // The group that matches the saved value keeps that value ("argon"), so opening
                // Settings and saving never rewrites it.
                var matches = selected is null && group.Contains(current, StringComparison.OrdinalIgnoreCase);
                var choice = new AntigravityGroupChoice(matches ? current : group, group);
                _antigravityGroup.Items.Add(choice);
                if (matches) selected = choice;
            }
            if (selected is null)
            {
                // Not detected yet (Antigravity off or not read): keep the saved value selectable.
                selected = new AntigravityGroupChoice(current, string.Equals(current, AntigravityGroups.DefaultPrimary, StringComparison.OrdinalIgnoreCase)
                    ? (_text.IsChinese ? "Argon（默认，未检测到）" : "Argon (default, not detected)")
                    : current);
                _antigravityGroup.Items.Add(selected);
            }
            _antigravityGroup.SelectedItem = selected;
        }

        _systemMetrics = CreateToggle(
            _text.SystemUsageTitle,
            _text.SystemMetricsModuleHint,
            settings.IsPluginEnabled("zgstokenbar.metrics.system", true));
        _claude = CreateToggle("Claude", _text.ClaudeProviderHint, settings.IsEnabled(ProviderKind.Claude));
        _codex = CreateToggle("Codex", _text.CodexProviderHint, settings.IsEnabled(ProviderKind.Codex));
        _grok = CreateToggle("Grok", _text.IsChinese ? "读取本机 Grok 配额" : "Read local Grok quota",
            settings.IsPluginEnabled(OtherToolsModule.GrokId, true));
        _antigravity = CreateToggle("Antigravity", _text.IsChinese ? "读取本机 Antigravity 配额" : "Read local Antigravity quota",
            settings.IsPluginEnabled(OtherToolsModule.AntigravityId, true));
        foreach (var definition in UsageProviderRegistry.All
                     .Select(provider => provider.Definition)
                     .Where(definition => definition.Surface == UsageSurface.Mini
                         && definition.Id is not (UsageProviderIds.Grok or UsageProviderIds.Antigravity)))
        {
            _usageProviderToggles.Add(definition.Id, CreateToggle(
                definition.DisplayName,
                UsageProviderHint(definition.DisplayName),
                settings.IsPluginEnabled(definition.Id, true)));
        }
        foreach (var entry in dockItems ?? [])
        {
            var title = entry.Detail is { Length: > 0 } detail ? $"{entry.Title} · {detail}" : entry.Title;
            _dockItemToggles.Add((entry, CreateToggle(title, DockItemHint, !settings.HiddenDockItems.Contains(entry.Id, StringComparer.Ordinal))));
        }
        _codexLocalUsage = CreateToggle(
            _text.CodexLocalUsageModule,
            _text.CodexLocalUsageModuleHint,
            settings.IsPluginEnabled("zgstokenbar.usage.codex-local", settings.IsEnabled(ProviderKind.Codex)));
        _sub2ApiPool = CreateToggle(
            _text.Sub2ApiPool,
            _text.Sub2ApiModuleHint,
            settings.EnableSub2ApiPool);
        _openAtLogin = CreateToggle(
            _text.StartWithWindows,
            _text.StartWithWindowsHint,
            settings.OpenAtLogin);
        _usageAlerts = CreateToggle(_text.UsageAlertsTitle, _text.UsageAlerts, settings.EnableAlerts);
        _animations = CreateToggle(_text.AnimationsTitle, _text.Animations, settings.EnableAnimations);
        _embedTaskbar = CreateToggle(_text.EmbedTaskbarTitle, _text.EmbedTaskbarHint, settings.EmbedInTaskbar);
        _refreshClaudeOAuth = CreateToggle(_text.AllowClaudeRefresh, _text.ClaudeRefreshHint, settings.AutoRefreshClaudeOAuth);
        foreach (var plugin in _plugins
                     .Where(plugin => !BuiltinPluginIds.Contains(plugin.Manifest.Id))
                     .OrderBy(plugin => plugin.Manifest.Order)
                     .ThenBy(plugin => plugin.Manifest.Id, StringComparer.Ordinal))
        {
            var toggle = CreateToggle(
                plugin.Manifest.DisplayName ?? plugin.Manifest.Id,
                PluginDescription(plugin.Manifest),
                plugin.Enabled);
            toggle.Enabled = !plugin.Manifest.Required;
            _externalPluginToggles.Add(plugin.Manifest.Id, toggle);
        }

        _paletteGrid = new PaletteGrid(_scale);
        foreach (var palette in QuotaBackgroundPalette.All)
        {
            var choice = new PaletteChoiceButton(
                _theme,
                _scale,
                palette.QuotaGroup,
                _text.BackgroundPaletteName(palette.Id))
            {
                Tag = palette.Id,
                AccessibleName = _text.BackgroundPaletteName(palette.Id),
                AccessibleDescription = _text.BackgroundPaletteHint,
            };
            choice.Click += (_, _) =>
            {
                _backgroundPalette = palette.Id;
                UpdateBackgroundPaletteSelection();
                RefreshDirtyState();
            };
            _backgroundPaletteButtons.Add(palette.Id, choice);
            _paletteGrid.Controls.Add(choice);
        }
        UpdateBackgroundPaletteSelection();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = _theme.Content,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, Scale(64)));
        Controls.Add(root);

        _bodyLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = _theme.Content,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        _bodyLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Scale(LogicalNavigationWidth)));
        _bodyLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _bodyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(_bodyLayout, 0, 0);

        _navigation = new NavigationRail(_theme, _scale)
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Tag = "settings.navigation",
        };
        _bodyLayout.Controls.Add(_navigation, 0, 0);

        _pageHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = _theme.Content,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            Tag = "settings.content",
            TabStop = false,
        };
        _bodyLayout.Controls.Add(_pageHost, 1, 0);

        BuildPages();
        BuildNavigation();
        SelectPage("general", focusPage: false);

        var footer = CreateFooter(out _dirtyLabel, out _save);
        root.Controls.Add(footer, 0, 1);

        _codexLocalUsageBeforeDisable = _codexLocalUsage.Checked;
        _sub2ApiPoolBeforeDisable = _sub2ApiPool.Checked;
        _lastCodexEnabled = _codex.Checked;
        _systemMetrics.CheckedChanged += (_, _) => RefreshDirtyState();
        _claude.CheckedChanged += (_, _) => RefreshDirtyState();
        _codex.CheckedChanged += (_, _) => UpdateCodexDependencies();
        _grok.CheckedChanged += (_, _) => RefreshDirtyState();
        _antigravity.CheckedChanged += (_, _) => RefreshDirtyState();
        foreach (var toggle in _usageProviderToggles.Values) toggle.CheckedChanged += (_, _) => RefreshDirtyState();
        foreach (var (_, toggle) in _dockItemToggles) toggle.CheckedChanged += (_, _) => RefreshDirtyState();
        _codexLocalUsage.CheckedChanged += (_, _) =>
        {
            if (!_updatingCodexDependency && _codex.Checked)
            {
                _codexLocalUsageBeforeDisable = _codexLocalUsage.Checked;
            }
            RefreshDirtyState();
        };
        _sub2ApiPool.CheckedChanged += (_, _) =>
        {
            if (!_updatingCodexDependency && _codex.Checked)
            {
                _sub2ApiPoolBeforeDisable = _sub2ApiPool.Checked;
            }
            RefreshDirtyState();
        };
        foreach (var toggle in _externalPluginToggles.Values)
        {
            toggle.CheckedChanged += (_, _) => RefreshDirtyState();
        }
        _openAtLogin.CheckedChanged += (_, _) => RefreshDirtyState();
        foreach (var toggle in new[]
                 {
                     _usageAlerts,
                     _animations,
                     _embedTaskbar,
                     _refreshClaudeOAuth,
                 })
        {
            toggle.CheckedChanged += (_, _) => RefreshDirtyState();
        }
        _locale.SelectedIndexChanged += (_, _) => RefreshDirtyState();
        _refreshMinutes.SelectedIndexChanged += (_, _) => RefreshDirtyState();
        _antigravityGroup.SelectedIndexChanged += (_, _) => RefreshDirtyState();

        UpdateCodexDependencies(initializing: true);
        RefreshDirtyState();
        ApplyResponsiveLayout();

        Resize += (_, _) => ApplyResponsiveLayout();
        // Size and centre the window before it is first shown, so it opens at its final position.
        Load += (_, _) => { ApplyProductionWorkingArea(); LoadedBoundsForProbe = Bounds; };
        FormClosing += OnFormClosing;
        CaptureDesignFonts(this);
    }

    private void CaptureDesignFonts(Control control)
    {
        _designFonts[control] = (Font)control.Font.Clone();
        foreach (Control child in control.Controls) CaptureDesignFonts(child);
    }

    private void RestoreDesignFonts()
    {
        // Native DPI initialization can rewrite fonts even with AutoScaleMode.None.
        // These fonts already include the same DPI factor as the manual row geometry.
        // Clone on assignment so WinForms' DPI scaling never disposes the cached _designFonts instance.
        foreach (var (control, font) in _designFonts)
        {
            if (control.IsDisposed) continue;
            if (!IsFontUsable(control.Font)) control.Font = Control.DefaultFont;
            if (!control.Font.Equals(font)) control.Font = (Font)font.Clone();
        }
    }

    private static bool IsFontUsable(Font font)
    {
        try
        {
            _ = font.Height;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) RestoreDesignFonts();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WmDpiChanged) RestoreDesignFonts();
    }

    internal Panel ScrollViewport => _activePage;

    internal bool FontsMatchLayout => _designFonts.All(pair =>
        !pair.Key.IsDisposed && IsFontUsable(pair.Key.Font) && pair.Key.Font.Equals(pair.Value));

    internal (int Offset, int Maximum, bool ThemedBarVisible) ScrollStateForAcceptance =>
        _activePage.ScrollState;

    internal void SetScrollOffsetForAcceptance(int value) => _activePage.ScrollTo(value);

    internal void SelectPageForRendering(string key) => SelectPage(key, focusPage: false);

    internal void ShowDirtyStateForRendering()
    {
        if (!_renderOnly) throw new InvalidOperationException("Synthetic dirty state is render-only.");
        _backgroundPalette = QuotaBackgroundPalette.All
            .Select(palette => palette.Id)
            .First(id => !string.Equals(id, _original.BackgroundPalette, StringComparison.Ordinal));
        UpdateBackgroundPaletteSelection();
        RefreshDirtyState();
    }

    internal static Size ConstrainOuterSize(Size desiredOuterSize, Rectangle workingArea, int safeMarginPixels)
    {
        var safeMargin = Math.Max(0, safeMarginPixels);
        var availableWidth = Math.Max(1, workingArea.Width - safeMargin * 2);
        var availableHeight = Math.Max(1, workingArea.Height - safeMargin * 2);
        return new Size(
            Math.Min(Math.Max(1, desiredOuterSize.Width), availableWidth),
            Math.Min(Math.Max(1, desiredOuterSize.Height), availableHeight));
    }

    internal static string? ReadSemanticVersion()
    {
        var assembly = typeof(SettingsForm).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Trim();
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var semVer = informational.Split('+', 2)[0].Trim();
            if (!string.IsNullOrWhiteSpace(semVer)) return semVer;
        }

        var fileVersion = assembly
            .GetCustomAttribute<AssemblyFileVersionAttribute>()?
            .Version;
        var formattedFileVersion = FormatNumericVersion(fileVersion);
        if (formattedFileVersion is not null) return formattedFileVersion;

        var nameVersion = assembly.GetName().Version;
        return nameVersion is null
            ? null
            : $"{nameVersion.Major}.{nameVersion.Minor}.{Math.Max(0, nameVersion.Build)}";
    }

    private static string? FormatNumericVersion(string? value)
    {
        return Version.TryParse(value, out var parsed)
            ? $"{parsed.Major}.{parsed.Minor}.{Math.Max(0, parsed.Build)}"
            : null;
    }

    private Control CreateFooter(out Label dirtyLabel, out RoundedButton saveButton)
    {
        var footer = new BorderPanel(_theme.Footer, _theme.Border, drawTopBorder: true)
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(Scale(24), Scale(14), Scale(24), Scale(14)),
            Tag = "settings.footer",
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = _theme.Footer,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(layout);

        dirtyLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            BackColor = _theme.Footer,
            Font = FontAt(9),
            ForeColor = _theme.Warning,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
            Tag = "settings.dirty",
            AccessibleName = _text.UnsavedChanges,
        };
        layout.Controls.Add(dirtyLabel, 0, 0);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            BackColor = _theme.Footer,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        var cancel = new RoundedButton(_theme, _scale, primary: false)
        {
            Text = _text.Cancel,
            Width = Scale(92),
            Height = Scale(36),
            Margin = Padding.Empty,
            Tag = "settings.cancel",
            AccessibleName = _text.Cancel,
        };
        cancel.Click += (_, _) => CancelAndClose();
        saveButton = new RoundedButton(_theme, _scale, primary: true)
        {
            Text = _text.Save,
            Width = Scale(104),
            Height = Scale(36),
            Margin = new Padding(Scale(8), 0, 0, 0),
            Tag = "settings.save",
            AccessibleName = _text.Save,
        };
        saveButton.Click += (_, _) => Save();
        actions.Controls.Add(cancel);
        actions.Controls.Add(saveButton);
        layout.Controls.Add(actions, 1, 0);
        AcceptButton = saveButton;
        CancelButton = cancel;
        return footer;
    }

    private SettingsComboBox CreateComboBox(string accessibleName, string accessibleDescription) => new(_theme, _scale)
    {
        AccessibleName = accessibleName,
        AccessibleDescription = accessibleDescription,
    };

    private string UsageProviderHint(string name) => _text.IsChinese
        ? $"读取本机 {name} 登录或密钥的额度；关闭后不再读取，也不显示在 Bar 上"
        : $"Reads the local {name} sign-in or key; off stops reading it and hides it from the Bar";

    private string DockItemHint => _text.IsChinese ? "在 Bar 上显示" : "Show on the Bar";

    private ToggleSwitch CreateToggle(string name, string description, bool value) => new(_theme, _scale)
    {
        Checked = value,
        AccessibleName = name,
        AccessibleDescription = description,
    };

    private void ApplyProductionWorkingArea()
    {
        if (_renderOnly) return;
        var area = Owner is null
            ? Screen.FromControl(this).WorkingArea
            : Screen.FromControl(Owner).WorkingArea;
        Size = ConstrainOuterSize(SizeFromClientSize(_desiredClientSize), area, PhysicalSafeMargin);
        Location = new Point(
            area.Left + Math.Max(0, (area.Width - Width) / 2),
            area.Top + Math.Max(0, (area.Height - Height) / 2));
        ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        if (_applyingLayout || _bodyLayout is null || _navigation is null) return;
        _applyingLayout = true;
        try
        {
            var compact = ClientSize.Width < Scale(LogicalResponsiveBreakpoint);
            _bodyLayout.ColumnStyles[0].Width = Scale(compact
                ? LogicalCompactNavigationWidth
                : LogicalNavigationWidth);
            _navigation.SetCompact(compact);
        }
        finally
        {
            _applyingLayout = false;
        }
    }

    private void UpdateCodexDependencies(bool initializing = false)
    {
        if (_updatingCodexDependency) return;
        _updatingCodexDependency = true;
        try
        {
            if (_codex.Checked != _lastCodexEnabled || initializing)
            {
                if (_codex.Checked)
                {
                    if (!initializing)
                    {
                        _codexLocalUsage.Checked = _codexLocalUsageBeforeDisable;
                        _sub2ApiPool.Checked = _sub2ApiPoolBeforeDisable;
                    }
                }
                else
                {
                    if (!initializing)
                    {
                        _codexLocalUsageBeforeDisable = _codexLocalUsage.Checked;
                        _sub2ApiPoolBeforeDisable = _sub2ApiPool.Checked;
                    }
                    _codexLocalUsage.Checked = false;
                    _sub2ApiPool.Checked = false;
                }
                _lastCodexEnabled = _codex.Checked;
            }
            _codexLocalUsage.Enabled = _codex.Checked;
            _sub2ApiPool.Enabled = _codex.Checked;
        }
        finally
        {
            _updatingCodexDependency = false;
        }
        RefreshDirtyState();
    }

    private void UpdateBackgroundPaletteSelection()
    {
        foreach (var (id, button) in _backgroundPaletteButtons)
        {
            button.SetSelected(string.Equals(id, _backgroundPalette, StringComparison.Ordinal));
        }
    }

    private void RefreshDirtyState()
    {
        if (_save is null || _dirtyLabel is null) return;
        var dirty = !EditableEquals(_original, BuildSettings());
        _save.Enabled = dirty;
        _dirtyLabel.Text = dirty ? $"●  {_text.UnsavedChanges}" : string.Empty;
        _dirtyLabel.AccessibleDescription = dirty ? _text.UnsavedChanges : string.Empty;
        Text = dirty ? $"{_baseTitle} *" : _baseTitle;
    }

    private bool IsDirty => !EditableEquals(_original, BuildSettings());

    internal Rectangle LoadedBoundsForProbe { get; private set; }

    internal void SaveForProbe() => Save();

    private void Save()
    {
        var next = BuildSettings();
        ResultSettings = next;
        _allowClose = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void CancelAndClose()
    {
        if (!ConfirmDiscard()) return;
        _allowClose = true;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose || !IsDirty) return;
        if (!ConfirmDiscard())
        {
            e.Cancel = true;
            DialogResult = DialogResult.None;
            return;
        }
        _allowClose = true;
        DialogResult = DialogResult.Cancel;
    }

    private bool ConfirmDiscard()
    {
        if (!IsDirty) return true;
        return MessageBox.Show(
            this,
            _text.DiscardChangesMessage,
            _text.DiscardChangesTitle,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private Font FontAt(float points, FontStyle style = FontStyle.Regular) =>
        ScaledFont(
            style == FontStyle.Bold ? "Segoe UI Semibold" : "Segoe UI",
            points,
            style == FontStyle.Bold ? FontStyle.Regular : style,
            _scale);

    private static Font ScaledFont(string family, float points, FontStyle style, float scale) =>
        new(family, Math.Max(1, points * (96f / 72f) * scale), style, GraphicsUnit.Pixel);

    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * _scale));

    private static Icon? TryLoadWindowIcon()
    {
        foreach (var candidate in new[] { Application.ExecutablePath, Path.Combine(AppContext.BaseDirectory, "ZGSTokenBar.exe") }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var icon = Icon.ExtractAssociatedIcon(candidate);
                if (icon is not null) return icon;
            }
            catch
            {
                // The native title bar remains valid without an icon in unusual hosts.
            }
        }
        return null;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (SystemInformation.HighContrast) return;
        var enabled = 1;
        if (DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr parameter, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr window, IntPtr updateRectangle, IntPtr updateRegion, uint flags);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _windowIcon?.Dispose();
        base.Dispose(disposing);
        if (disposing)
        {
            foreach (var font in _designFonts.Values) font.Dispose();
            _designFonts.Clear();
        }
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius) => UiPaint.RoundedPath(bounds, radius);

    private sealed record RefreshChoice(int Minutes, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record LocaleChoice(string Locale, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record AntigravityGroupChoice(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private string AntigravityGroupTitle => _text.IsChinese ? "Antigravity 主分组" : "Antigravity primary group";

    private string AntigravityGroupHint => _text.IsChinese
        ? "圆环跟随此组；其他组快用完时角标变色"
        : "Ring follows this group; chip warns if another runs low";
}
