using System.Drawing.Drawing2D;
using System.Reflection;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

internal sealed class SettingsForm : Form
{
    private static readonly HashSet<string> BuiltinPluginIds =
    [
        "zgstokenbar.metrics.system",
        "zgstokenbar.provider.claude",
        "zgstokenbar.provider.codex",
        "zgstokenbar.usage.codex-local",
        "zgstokenbar.intelligence.radar",
    ];
    private const int LogicalClientWidth = 720;
    private const int LogicalClientHeight = 540;
    private const int LogicalNavigationWidth = 176;
    private const int LogicalCompactNavigationWidth = 72;
    private const int LogicalResponsiveBreakpoint = 640;
    private const int PhysicalSafeMargin = 16;
    private const int WmSetRedraw = 0x000B;
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
    private readonly ToggleSwitch _systemMetrics;
    private readonly ToggleSwitch _claude;
    private readonly ToggleSwitch _codex;
    private readonly ToggleSwitch _codexLocalUsage;
    private readonly ToggleSwitch _sub2ApiPool;
    private readonly ToggleSwitch _openAtLogin;
    private readonly ToggleSwitch _keepRunning;
    private readonly ToggleSwitch _usageAlerts;
    private readonly ToggleSwitch _animations;
    private readonly ToggleSwitch _radar;
    private readonly ToggleSwitch _radarAlerts;
    private readonly ToggleSwitch _codexEconomyBar;
    private readonly ToggleSwitch _refreshClaudeOAuth;
    private readonly SettingsComboBox _refreshMinutes;
    private readonly SettingsComboBox _locale;
    private readonly SettingsComboBox _codexDisplayMode;
    private readonly PaletteGrid _paletteGrid;
    private readonly Dictionary<string, PaletteChoiceButton> _backgroundPaletteButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToggleSwitch> _externalPluginToggles = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<PluginStatus> _plugins;
    private readonly RoundedButton _testRadar;
    private readonly CodexEconomySettingsPanel _codexEconomyPanel;
    private readonly RoundedButton _save;
    private readonly Label _dirtyLabel;
    private SettingsPage _activePage = null!;
    private Icon? _windowIcon;
    private string _backgroundPalette;
    private bool _radarAlertsBeforeDisable;
    private bool _lastRadarEnabled;
    private bool _updatingRadarDependency;
    private bool _codexLocalUsageBeforeDisable;
    private bool _sub2ApiPoolBeforeDisable;
    private bool _lastCodexEnabled;
    private bool _updatingCodexDependency;
    private bool _updatingStartupDependency;
    private bool _allowClose;
    private bool _applyingLayout;

    public AppSettings? ResultSettings { get; private set; }
    public event EventHandler? RadarTestNotificationRequested;
    public event EventHandler? CodexEconomyStatusChanged;

    public SettingsForm(
        AppSettings settings,
        int targetDpi,
        bool renderOnly = false,
        Rectangle? renderWorkingArea = null,
        IReadOnlyList<PluginStatus>? plugins = null,
        CodexEconomyStatus? codexEconomyStatus = null,
        IReadOnlyList<CodexEconomyProfile>? codexEconomyProfiles = null,
        Func<CodexEconomyProfile, CodexEconomyStatus>? inspectCodexEconomy = null,
        Func<CodexEconomyProfile, CodexEconomyMode, CodexEconomyStatus>? setCodexEconomyMode = null)
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
        _radarAlertsBeforeDisable = settings.EnableRadarAlerts;
        _lastRadarEnabled = settings.EnableRadar;
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

        _codexDisplayMode = CreateComboBox(_text.CodexDisplayMode, _text.CodexDisplayModeHint);
        _codexDisplayMode.Tag = "settings.codex.display-mode";
        _codexDisplayMode.Items.Add(new CodexMiniDisplayModeChoice(
            CodexMiniDisplayModes.Accounts,
            _text.CodexDisplayModeAccounts));
        _codexDisplayMode.Items.Add(new CodexMiniDisplayModeChoice(
            CodexMiniDisplayModes.Pool,
            _text.CodexDisplayModePool));
        var codexDisplayMode = CodexMiniDisplayModes.Normalize(settings.CodexMiniDisplayMode);
        _codexDisplayMode.SelectedItem = _codexDisplayMode.Items
            .Cast<CodexMiniDisplayModeChoice>()
            .First(choice => string.Equals(choice.Mode, codexDisplayMode, StringComparison.Ordinal));

        _systemMetrics = CreateToggle(
            _text.SystemUsageTitle,
            _text.SystemMetricsModuleHint,
            settings.IsPluginEnabled("zgstokenbar.metrics.system", true));
        _claude = CreateToggle("Claude", _text.ClaudeProviderHint, settings.IsEnabled(ProviderKind.Claude));
        _codex = CreateToggle("Codex", _text.CodexProviderHint, settings.IsEnabled(ProviderKind.Codex));
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
            settings.OpenAtLogin || settings.KeepRunning);
        _keepRunning = CreateToggle(_text.KeepRunning, _text.KeepRunningHint, settings.KeepRunning);
        _usageAlerts = CreateToggle(_text.UsageAlertsTitle, _text.UsageAlerts, settings.EnableAlerts);
        _animations = CreateToggle(_text.AnimationsTitle, _text.Animations, settings.EnableAnimations);
        _radar = CreateToggle(_text.ShowRadarTitle, _text.ShowRadar, settings.EnableRadar);
        _radarAlerts = CreateToggle(_text.RadarAlertsTitle, _text.RadarAlerts, settings.EnableRadarAlerts);
        _codexEconomyBar = CreateToggle(
            _text.CodexEconomyBarTitle,
            _text.CodexEconomyBarHint,
            settings.EnableCodexEconomyBar);
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

        _testRadar = new RoundedButton(_theme, _scale, primary: false)
        {
            Text = _text.TestRadarNotification,
            Width = Scale(164),
            Height = Scale(32),
            Tag = "settings.radar.test",
            AccessibleName = _text.TestRadarNotification,
            AccessibleDescription = _text.RadarTestNotificationHint,
        };
        _testRadar.Click += (_, _) => RadarTestNotificationRequested?.Invoke(this, EventArgs.Empty);

        var economyProfiles = codexEconomyProfiles
            ?? (codexEconomyStatus is null ? [] : [codexEconomyStatus.Profile]);
        CodexEconomyStatus InspectEconomy(CodexEconomyProfile profile) =>
            inspectCodexEconomy?.Invoke(profile)
            ?? codexEconomyStatus
            ?? new CodexEconomyStatus(CodexEconomyMode.Unconfigured, profile, false, false, null);
        _codexEconomyPanel = new CodexEconomySettingsPanel(
            _text,
            targetDpi,
            renderOnly,
            economyProfiles,
            InspectEconomy,
            setCodexEconomyMode);
        _codexEconomyPanel.StatusChanged += (_, _) => CodexEconomyStatusChanged?.Invoke(this, EventArgs.Empty);

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
        _radar.CheckedChanged += (_, _) => UpdateRadarDependency();
        _codexEconomyBar.CheckedChanged += (_, _) => RefreshDirtyState();
        _keepRunning.CheckedChanged += (_, _) => UpdateStartupDependencies(keepRunningChanged: true);
        _openAtLogin.CheckedChanged += (_, _) => UpdateStartupDependencies(keepRunningChanged: false);
        _radarAlerts.CheckedChanged += (_, _) =>
        {
            if (!_updatingRadarDependency && _radar.Checked)
            {
                _radarAlertsBeforeDisable = _radarAlerts.Checked;
            }
            RefreshDirtyState();
        };
        foreach (var toggle in new[]
                 {
                     _usageAlerts,
                     _animations,
                     _refreshClaudeOAuth,
                 })
        {
            toggle.CheckedChanged += (_, _) => RefreshDirtyState();
        }
        _locale.SelectedIndexChanged += (_, _) => RefreshDirtyState();
        _refreshMinutes.SelectedIndexChanged += (_, _) => RefreshDirtyState();
        _codexDisplayMode.SelectedIndexChanged += (_, _) => RefreshDirtyState();

        UpdateCodexDependencies(initializing: true);
        UpdateRadarDependency(initializing: true);
        RefreshDirtyState();
        ApplyResponsiveLayout();

        Resize += (_, _) => ApplyResponsiveLayout();
        Shown += (_, _) => ApplyProductionWorkingArea();
        FormClosing += OnFormClosing;
    }

    internal Panel ScrollViewport => _activePage;

    internal (int Offset, int Maximum, bool ThemedBarVisible) ScrollStateForAcceptance =>
        _activePage.ScrollState;

    internal void SetScrollOffsetForAcceptance(int value) => _activePage.ScrollTo(value);

    internal void SelectPageForRendering(string key) => SelectPage(key, focusPage: false);

    internal CodexEconomyStatus? CurrentCodexEconomyStatus => _codexEconomyPanel.CurrentStatus;
    internal void RefreshCodexEconomyStatus() => _codexEconomyPanel.RefreshStatus();

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

    private void BuildPages()
    {
        var general = AddPage("general", _text.General);
        general.Add(new ValueSettingRow(_text.Language, _text.LanguageHint, _locale, Scale(196), _theme, _scale));
        general.Add(new ValueSettingRow(_text.AutomaticRefresh, _text.AutomaticRefreshHint, _refreshMinutes, Scale(196), _theme, _scale));
        general.Add(new ToggleSettingRow(_openAtLogin, _text.StartWithWindows, _text.StartWithWindowsHint, _theme, _scale));
        general.Add(new ToggleSettingRow(_keepRunning, _text.KeepRunning, _text.KeepRunningHint, _theme, _scale));
        general.Add(new ToggleSettingRow(_animations, _text.AnimationsTitle, _text.Animations, _theme, _scale));

        var providers = AddPage("providers", _text.Modules);
        providers.Add(new ToggleSettingRow(
            _systemMetrics,
            _text.SystemUsageTitle,
            _text.SystemMetricsModuleHint,
            _theme,
            _scale));
        providers.Add(new ToggleSettingRow(_claude, "Claude", _text.ClaudeProviderHint, _theme, _scale));
        providers.Add(new ToggleSettingRow(_codex, "Codex", _text.CodexProviderHint, _theme, _scale));
        providers.Add(new ToggleSettingRow(
            _codexLocalUsage,
            _text.CodexLocalUsageModule,
            _text.CodexLocalUsageModuleHint,
            _theme,
            _scale));
        providers.Add(new ToggleSettingRow(
            _sub2ApiPool,
            _text.Sub2ApiPool,
            _text.Sub2ApiModuleHint,
            _theme,
            _scale));
        providers.Add(new ToggleSettingRow(_radar, _text.ShowRadarTitle, _text.ShowRadar, _theme, _scale));
        providers.Add(new ToggleSettingRow(
            _codexEconomyBar,
            _text.CodexEconomyBarTitle,
            _text.CodexEconomyBarHint,
            _theme,
            _scale));
        foreach (var plugin in _plugins
                     .Where(plugin => _externalPluginToggles.ContainsKey(plugin.Manifest.Id))
                     .OrderBy(plugin => plugin.Manifest.Order)
                     .ThenBy(plugin => plugin.Manifest.Id, StringComparer.Ordinal))
        {
            providers.Add(new ToggleSettingRow(
                _externalPluginToggles[plugin.Manifest.Id],
                plugin.Manifest.DisplayName ?? plugin.Manifest.Id,
                PluginDescription(plugin.Manifest),
                _theme,
                _scale));
        }
        var notifications = AddPage("notifications", _text.Notifications);
        notifications.Add(new ToggleSettingRow(_usageAlerts, _text.UsageAlertsTitle, _text.UsageAlerts, _theme, _scale));

        var display = AddPage("display", _text.Display);
        display.Add(new ValueSettingRow(
            _text.CodexDisplayMode,
            _text.CodexDisplayModeHint,
            _codexDisplayMode,
            Scale(196),
            _theme,
            _scale));
        display.Add(new PaletteSettingRow(
            _text.BackgroundPalette,
            _text.BackgroundPaletteHint,
            _paletteGrid,
            _theme,
            _scale));

        var radar = AddPage("radar", _text.RadarSectionTitle);
        radar.Add(new SectionNote(_text.RadarNetworkHint, _theme, _scale));
        radar.Add(new ToggleSettingRow(_radarAlerts, _text.RadarAlertsTitle, _text.RadarAlerts, _theme, _scale));
        radar.Add(new ActionSettingRow(
            _text.TestRadarNotification,
            _text.RadarTestNotificationHint,
            _text.RadarRunsWhileOpen,
            _testRadar,
            _theme,
            _scale));

        var advanced = AddPage("advanced", _text.Advanced);
        advanced.Add(new ToggleSettingRow(
            _refreshClaudeOAuth,
            _text.AllowClaudeRefresh,
            _text.ClaudeRefreshHint,
            _theme,
            _scale));
        advanced.Add(_codexEconomyPanel);

        var about = AddPage("about", _text.About);
        about.Add(new AboutRow(
            ReadSemanticVersion() is { } version ? $"v{version}" : _text.VersionUnknown,
            _text.AboutDescription,
            _text.LocalFirstPrivacy,
            _theme,
            _scale));
    }

    private SettingsPage AddPage(string key, string title)
    {
        var page = new SettingsPage(title, _theme, _scale)
        {
            Dock = DockStyle.Fill,
            Visible = false,
            Tag = $"settings.page.{key}",
            AccessibleName = title,
        };
        _pages.Add(key, page);
        _pageHost.Controls.Add(page);
        return page;
    }

    private void BuildNavigation()
    {
        AddNavigation("general", "\uE713", _text.General);
        AddNavigation("providers", "\uE77B", _text.Modules);
        AddNavigation("notifications", "\uEA8F", _text.Notifications);
        AddNavigation("display", "\uE790", _text.Display);
        AddNavigation("radar", "\uF272", _text.RadarNavigation);
        AddNavigation("advanced", "\uE9F9", _text.Advanced);
        AddNavigation("about", "\uE946", _text.About);
    }

    private void AddNavigation(string key, string glyph, string label)
    {
        var button = new NavigationButton(glyph, label, _theme, _scale)
        {
            Tag = $"settings.navigation.{key}",
            AccessibleName = label,
        };
        button.Click += (_, _) => SelectPage(key, focusPage: false);
        button.KeyDown += (_, eventArgs) => MoveNavigationFocus(button, eventArgs);
        _navigationButtons.Add(key, button);
        _navigation.Add(button);
    }

    private void MoveNavigationFocus(NavigationButton current, KeyEventArgs eventArgs)
    {
        var buttons = _navigationButtons.Values.ToArray();
        var index = Array.IndexOf(buttons, current);
        if (index < 0) return;
        var next = eventArgs.KeyCode switch
        {
            Keys.Up => Math.Max(0, index - 1),
            Keys.Down => Math.Min(buttons.Length - 1, index + 1),
            _ => index,
        };
        if (next == index) return;
        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;
        buttons[next].Focus();
        buttons[next].PerformClick();
    }

    private void SelectPage(string key, bool focusPage)
    {
        if (!_pages.TryGetValue(key, out var page)) return;
        foreach (var (candidateKey, button) in _navigationButtons)
        {
            button.SetSelected(string.Equals(candidateKey, key, StringComparison.Ordinal));
        }

        var suspendPainting = _pageHost.IsHandleCreated;
        if (suspendPainting) SendMessage(_pageHost.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
        _pageHost.SuspendLayout();
        try
        {
            if (_activePage is not null && !ReferenceEquals(_activePage, page))
            {
                _activePage.Visible = false;
            }
            _activePage = page;
            page.BringToFront();
            page.ScrollToTop();
            page.Visible = true;
        }
        finally
        {
            _pageHost.ResumeLayout(true);
            if (suspendPainting)
            {
                SendMessage(_pageHost.Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
                RedrawWindow(_pageHost.Handle, IntPtr.Zero, IntPtr.Zero, RedrawNowAllChildren);
            }
            else
            {
                _pageHost.Invalidate(true);
            }
        }
        if (focusPage) page.Focus();
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

    private void UpdateStartupDependencies(bool keepRunningChanged)
    {
        if (_updatingStartupDependency) return;
        _updatingStartupDependency = true;
        try
        {
            if (keepRunningChanged && _keepRunning.Checked)
            {
                _openAtLogin.Checked = true;
            }
            else if (!keepRunningChanged && !_openAtLogin.Checked)
            {
                _keepRunning.Checked = false;
            }
        }
        finally
        {
            _updatingStartupDependency = false;
        }
        RefreshDirtyState();
    }

    private void UpdateRadarDependency(bool initializing = false)
    {
        if (_updatingRadarDependency) return;
        _updatingRadarDependency = true;
        try
        {
            if (_radar.Checked != _lastRadarEnabled || initializing)
            {
                if (_radar.Checked)
                {
                    if (!initializing) _radarAlerts.Checked = _radarAlertsBeforeDisable;
                }
                else
                {
                    if (!initializing) _radarAlertsBeforeDisable = _radarAlerts.Checked;
                    _radarAlerts.Checked = false;
                }
                _lastRadarEnabled = _radar.Checked;
            }
            _radarAlerts.Enabled = _radar.Checked;
            _testRadar.Enabled = _radar.Checked;
        }
        finally
        {
            _updatingRadarDependency = false;
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

    private AppSettings BuildSettings()
    {
        var providers = new List<string>(2);
        if (_claude.Checked) providers.Add("claude");
        if (_codex.Checked) providers.Add("codex");
        var refresh = _refreshMinutes.SelectedItem as RefreshChoice;
        var settings = new AppSettings
        {
            EnabledProviders = providers.ToArray(),
            RefreshMinutes = refresh?.Minutes ?? _original.RefreshMinutes,
            AutoRefreshClaudeOAuth = _refreshClaudeOAuth.Checked,
            OpenAtLogin = _openAtLogin.Checked,
            KeepRunning = _keepRunning.Checked,
            EnableAlerts = _usageAlerts.Checked,
            UseTaskbarRings = true,
            EnableAnimations = _animations.Checked,
            EnableRadar = _radar.Checked,
            EnableRadarAlerts = _radar.Checked && _radarAlerts.Checked,
            EnableCodexEconomyBar = _codexEconomyBar.Checked,
            EnableAiGatewayBalance = _externalPluginToggles.TryGetValue(
                MiniAreaIds.AiGateway,
                out var aiGatewayToggle)
                ? aiGatewayToggle.Checked
                : _original.EnableAiGatewayBalance,
            EnableSub2ApiPool = _codex.Checked && _sub2ApiPool.Checked,
            MiniAreaLayouts = AppSettings.CopyMiniAreaLayouts(_original.MiniAreaLayouts),
            MiniAreaOrder = AppSettings.CopyMiniAreaOrder(_original.MiniAreaOrder),
            CodexMiniDisplayMode = (_codexDisplayMode.SelectedItem as CodexMiniDisplayModeChoice)?.Mode
                ?? _original.CodexMiniDisplayMode,
            BackgroundPalette = _backgroundPalette,
            Locale = (_locale.SelectedItem as LocaleChoice)?.Locale ?? _original.Locale,
            PluginEnabled = new Dictionary<string, bool>(_original.PluginEnabled, StringComparer.Ordinal),
            PluginEnablementDecisions = [.. _original.PluginEnablementDecisions],
            AutoEnabledPlugins = [.. _original.AutoEnabledPlugins],
        };
        ApplyPluginToggle(settings, "zgstokenbar.metrics.system", _systemMetrics.Checked, true);
        ApplyPluginToggle(settings, "zgstokenbar.provider.claude", _claude.Checked, false);
        ApplyPluginToggle(settings, "zgstokenbar.provider.codex", _codex.Checked, false);
        ApplyPluginToggle(
            settings,
            "zgstokenbar.usage.codex-local",
            _codex.Checked && _codexLocalUsage.Checked,
            _original.IsEnabled(ProviderKind.Codex));
        ApplyPluginToggle(settings, "zgstokenbar.intelligence.radar", _radar.Checked, false);
        foreach (var pair in _externalPluginToggles)
        {
            ApplyPluginToggle(
                settings,
                pair.Key,
                pair.Value.Checked,
                _plugins.FirstOrDefault(plugin => plugin.Manifest.Id == pair.Key)?.Manifest.DefaultEnabled == true);
        }
        settings.CopyPlacementStateFrom(_original);
        return settings;
    }

    private void ApplyPluginToggle(
        AppSettings settings,
        string pluginId,
        bool enabled,
        bool fallback)
    {
        var changed = _original.IsPluginEnabled(pluginId, fallback) != enabled;
        settings.SetPluginEnabled(pluginId, enabled, explicitChoice: changed);
    }

    private static bool EditableEquals(AppSettings left, AppSettings right) =>
        left.EnabledProviders.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(right.EnabledProviders)
        && left.RefreshMinutes == right.RefreshMinutes
        && left.AutoRefreshClaudeOAuth == right.AutoRefreshClaudeOAuth
        && left.OpenAtLogin == right.OpenAtLogin
        && left.KeepRunning == right.KeepRunning
        && left.EnableAlerts == right.EnableAlerts
        && left.EnableAnimations == right.EnableAnimations
        && left.EnableRadar == right.EnableRadar
        && left.EnableRadarAlerts == right.EnableRadarAlerts
        && left.EnableCodexEconomyBar == right.EnableCodexEconomyBar
        && left.EnableAiGatewayBalance == right.EnableAiGatewayBalance
        && left.EnableSub2ApiPool == right.EnableSub2ApiPool
        && left.IsPluginEnabled("zgstokenbar.metrics.system", true)
            == right.IsPluginEnabled("zgstokenbar.metrics.system", true)
        && left.IsPluginEnabled("zgstokenbar.usage.codex-local", left.IsEnabled(ProviderKind.Codex))
            == right.IsPluginEnabled("zgstokenbar.usage.codex-local", right.IsEnabled(ProviderKind.Codex))
        && MiniAreaLayoutsEqual(left.MiniAreaLayouts, right.MiniAreaLayouts)
        && left.MiniAreaOrder.SequenceEqual(right.MiniAreaOrder, StringComparer.Ordinal)
        && DictionaryEqual(left.PluginEnabled, right.PluginEnabled)
        && string.Equals(left.CodexMiniDisplayMode, right.CodexMiniDisplayMode, StringComparison.Ordinal)
        && string.Equals(left.BackgroundPalette, right.BackgroundPalette, StringComparison.Ordinal)
        && string.Equals(left.Locale, right.Locale, StringComparison.Ordinal);

    private static AppSettings Copy(AppSettings settings)
    {
        var copy = new AppSettings
        {
            EnabledProviders = [.. settings.EnabledProviders],
            RefreshMinutes = settings.RefreshMinutes,
            AutoRefreshClaudeOAuth = settings.AutoRefreshClaudeOAuth,
            OpenAtLogin = settings.OpenAtLogin,
            KeepRunning = settings.KeepRunning,
            EnableAlerts = settings.EnableAlerts,
            UseTaskbarRings = true,
            EnableAnimations = settings.EnableAnimations,
            EnableRadar = settings.EnableRadar,
            EnableRadarAlerts = settings.EnableRadarAlerts,
            EnableCodexEconomyBar = settings.EnableCodexEconomyBar,
            EnableAiGatewayBalance = settings.EnableAiGatewayBalance,
            EnableSub2ApiPool = settings.EnableSub2ApiPool,
            MiniAreaLayouts = AppSettings.CopyMiniAreaLayouts(settings.MiniAreaLayouts),
            MiniAreaOrder = AppSettings.CopyMiniAreaOrder(settings.MiniAreaOrder),
            CodexMiniDisplayMode = CodexMiniDisplayModes.Normalize(settings.CodexMiniDisplayMode),
            BackgroundPalette = AppSettings.NormalizeBackgroundPalette(settings.BackgroundPalette),
            Locale = settings.Locale,
            PluginEnabled = new Dictionary<string, bool>(settings.PluginEnabled, StringComparer.Ordinal),
            PluginEnablementDecisions = [.. settings.PluginEnablementDecisions],
            AutoEnabledPlugins = [.. settings.AutoEnabledPlugins],
        };
        copy.CopyPlacementStateFrom(settings);
        return copy;
    }

    private string PluginDescription(PluginManifest manifest) =>
        string.Equals(_original.Locale, "zh-CN", StringComparison.OrdinalIgnoreCase)
            ? $"本地插件 · {manifest.Id} · v{manifest.Version}"
            : $"Local plugin · {manifest.Id} · v{manifest.Version}";

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, bool> left,
        IReadOnlyDictionary<string, bool> right)
    {
        var leftExternal = left.Where(pair => !BuiltinPluginIds.Contains(pair.Key)).ToArray();
        var rightExternal = right.Where(pair => !BuiltinPluginIds.Contains(pair.Key)).ToArray();
        if (leftExternal.Length != rightExternal.Length) return false;
        return leftExternal.All(pair =>
            right.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    private static bool MiniAreaLayoutsEqual(
        IReadOnlyDictionary<string, MiniAreaLayout> left,
        IReadOnlyDictionary<string, MiniAreaLayout> right) =>
        left.Count == right.Count
        && left.All(entry => right.TryGetValue(entry.Key, out var value) && value == entry.Value);

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
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = Math.Max(2, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed record RefreshChoice(int Minutes, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record LocaleChoice(string Locale, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record CodexMiniDisplayModeChoice(string Mode, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record SettingsTheme(
        Color Content,
        Color Sidebar,
        Color Footer,
        Color Surface,
        Color Hover,
        Color Border,
        Color ScrollTrack,
        Color ScrollThumb,
        Color Text,
        Color Muted,
        Color Accent,
        Color AccentText,
        Color Warning)
    {
        public static SettingsTheme Create() => SystemInformation.HighContrast
            ? new(
                SystemColors.Window,
                SystemColors.Control,
                SystemColors.Control,
                SystemColors.Control,
                SystemColors.Highlight,
                SystemColors.WindowText,
                SystemColors.ScrollBar,
                SystemColors.WindowText,
                SystemColors.WindowText,
                SystemColors.GrayText,
                SystemColors.Highlight,
                SystemColors.HighlightText,
                SystemColors.HotTrack)
            : new(
                Color.FromArgb(24, 24, 28),
                Color.FromArgb(18, 18, 20),
                Color.FromArgb(21, 21, 24),
                Color.FromArgb(31, 31, 35),
                Color.FromArgb(39, 39, 45),
                Color.FromArgb(44, 44, 49),
                Color.FromArgb(44, 44, 49),
                Color.FromArgb(104, 108, 118),
                Color.FromArgb(242, 243, 245),
                Color.FromArgb(160, 164, 173),
                Color.FromArgb(76, 141, 255),
                Color.White,
                Color.FromArgb(229, 163, 59));
    }

    private sealed class BorderPanel : Panel
    {
        private readonly Color _fill;
        private readonly Color _border;
        private readonly bool _drawTopBorder;

        public BorderPanel(Color fill, Color border, bool drawTopBorder)
        {
            _fill = fill;
            _border = border;
            _drawTopBorder = drawTopBorder;
            BackColor = fill;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(_fill);
            if (!_drawTopBorder) return;
            using var pen = new Pen(_border);
            e.Graphics.DrawLine(pen, 0, 0, Width, 0);
        }
    }

    private sealed class NavigationRail : Panel
    {
        private readonly FlowLayoutPanel _stack;
        private readonly float _scale;

        public NavigationRail(SettingsTheme theme, float scale)
        {
            _scale = scale;
            BackColor = theme.Sidebar;
            Padding = Padding.Empty;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw,
                true);
            _stack = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = theme.Sidebar,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = false,
                Padding = new Padding(Scale(14), Scale(20), Scale(14), Scale(12)),
                Margin = Padding.Empty,
                TabStop = false,
            };
            Controls.Add(_stack);
        }

        public void Add(NavigationButton button)
        {
            button.Width = Math.Max(1, Width - _stack.Padding.Horizontal);
            _stack.Controls.Add(button);
        }

        public void SetCompact(bool compact)
        {
            foreach (var button in _stack.Controls.OfType<NavigationButton>()) button.SetCompact(compact);
            PerformLayout();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            foreach (Control control in _stack.Controls)
            {
                control.Width = Math.Max(1, _stack.ClientSize.Width - _stack.Padding.Horizontal);
            }
        }

        private int Scale(int value) => Math.Max(1, (int)Math.Round(value * _scale));
    }

    private sealed class NavigationButton : Button
    {
        private readonly string _glyph;
        private readonly SettingsTheme _theme;
        private readonly Font _iconFont;
        private readonly int _radius;
        private readonly int _accentWidth;
        private readonly int _padding;
        private bool _selected;
        private bool _hover;
        private bool _compact;

        public NavigationButton(string glyph, string label, SettingsTheme theme, float scale)
        {
            _glyph = glyph;
            _theme = theme;
            _iconFont = ScaledFont("Segoe Fluent Icons", 13f, FontStyle.Regular, scale);
            _radius = Math.Max(1, (int)Math.Round(4 * scale));
            _accentWidth = Math.Max(2, (int)Math.Round(3 * scale));
            _padding = Math.Max(1, (int)Math.Round(12 * scale));
            Text = label;
            Height = Math.Max(1, (int)Math.Round(48 * scale));
            Margin = new Padding(0, 0, 0, Math.Max(1, (int)Math.Round(4 * scale)));
            Font = ScaledFont("Segoe UI", 10f, FontStyle.Regular, scale);
            ForeColor = theme.Muted;
            BackColor = theme.Sidebar;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            TabStop = true;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        public void SetSelected(bool selected)
        {
            _selected = selected;
            ForeColor = selected ? _theme.Text : _theme.Muted;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        }

        public void SetCompact(bool compact)
        {
            _compact = compact;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(_theme.Sidebar);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            if (_selected || _hover)
            {
                using var path = RoundedRectangle(bounds, _radius);
                using var fill = new SolidBrush(_selected ? _theme.Hover : ControlPaint.Light(_theme.Sidebar, .03f));
                e.Graphics.FillPath(fill, path);
            }
            if (_selected)
            {
                using var accent = new SolidBrush(_theme.Accent);
                e.Graphics.FillRectangle(accent, 0, _padding / 2, _accentWidth, Math.Max(1, Height - _padding));
            }

            var iconBounds = _compact
                ? bounds
                : new Rectangle(_padding + _accentWidth, 0, Math.Max(1, Height - _padding), Height);
            TextRenderer.DrawText(
                e.Graphics,
                _glyph,
                _iconFont,
                iconBounds,
                _selected ? _theme.Text : _theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (!_compact)
            {
                var textX = iconBounds.Right + Math.Max(1, _padding / 2);
                var textBounds = new Rectangle(textX, 0, Math.Max(1, Width - textX - _padding), Height);
                TextRenderer.DrawText(
                    e.Graphics,
                    Text,
                    Font,
                    textBounds,
                    _selected ? _theme.Text : _theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            if (Focused && ShowFocusCues)
            {
                using var focus = new Pen(_theme.Accent, Math.Max(1, _accentWidth / 2f));
                e.Graphics.DrawRectangle(focus, Rectangle.Inflate(bounds, -3, -3));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _iconFont.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class SettingsPage : Panel
    {
        private readonly FlowLayoutPanel _stack;
        private readonly SettingsScrollBar _scrollBar;
        private readonly float _scale;
        private int _wheelRemainder;
        private bool _layingOut;

        public SettingsPage(string title, SettingsTheme theme, float scale)
        {
            _scale = scale;
            BackColor = theme.Content;
            AutoScroll = false;
            TabStop = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw,
                true);
            _stack = new FlowLayoutPanel
            {
                AutoScroll = false,
                BackColor = theme.Content,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                TabStop = false,
            };
            _scrollBar = new SettingsScrollBar(theme, scale)
            {
                Visible = false,
                Tag = "settings.scrollbar",
                AccessibleName = title,
                AccessibleRole = AccessibleRole.ScrollBar,
            };
            _scrollBar.ValueChanged += (_, _) => ApplyScrollPosition();
            _scrollBar.MouseWheel += OnChildMouseWheel;
            Controls.Add(_stack);
            Controls.Add(_scrollBar);
            _scrollBar.BringToFront();
            _stack.Controls.Add(new Label
            {
                AutoSize = false,
                BackColor = theme.Content,
                Height = Scale(64),
                Font = ScaledFont("Segoe UI Semibold", 14f, FontStyle.Regular, scale),
                ForeColor = theme.Text,
                Text = title,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty,
                UseMnemonic = false,
                Tag = "settings.page.title",
            });
            HookScrollInput(_stack);
        }

        public void Add(Control control)
        {
            control.Margin = Padding.Empty;
            _stack.Controls.Add(control);
        }

        public (int Offset, int Maximum, bool ThemedBarVisible) ScrollState =>
            (_scrollBar.Value, _scrollBar.Maximum, _scrollBar.Visible);

        public void ScrollToTop() => ScrollTo(0);

        public void ScrollTo(int value)
        {
            _scrollBar.Value = value;
            ApplyScrollPosition();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_stack is null || _layingOut) return;
            _layingOut = true;
            try
            {
                var inset = Scale(24);
                var width = Math.Max(Scale(260), ClientSize.Width - inset * 2);
                _stack.SuspendLayout();
                _stack.Width = width;
                var height = 0;
                foreach (Control control in _stack.Controls)
                {
                    control.Width = width;
                    control.PerformLayout();
                    height += control.Height + control.Margin.Vertical;
                }
                _stack.Height = Math.Max(1, height + Scale(20));
                _stack.ResumeLayout(true);

                var barTop = Scale(10);
                var barRight = Scale(6);
                var barWidth = Scale(12);
                _scrollBar.SetBounds(
                    Math.Max(0, ClientSize.Width - barRight - barWidth),
                    barTop,
                    barWidth,
                    Math.Max(1, ClientSize.Height - barTop * 2));
                _scrollBar.SetRange(
                    Math.Max(0, _stack.Height - ClientSize.Height),
                    ClientSize.Height,
                    _stack.Height);
                _scrollBar.Visible = _scrollBar.Maximum > 0;
                ApplyScrollPosition(inset);
                _scrollBar.BringToFront();
            }
            finally
            {
                _layingOut = false;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollFromWheel(e);
            base.OnMouseWheel(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_scrollBar.Maximum <= 0 || (keyData & Keys.Modifiers) != Keys.None)
            {
                return base.ProcessCmdKey(ref msg, keyData);
            }

            var handled = true;
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up:
                    ScrollTo(_scrollBar.Value - Scale(32));
                    break;
                case Keys.Down:
                    ScrollTo(_scrollBar.Value + Scale(32));
                    break;
                case Keys.PageUp:
                    ScrollTo(_scrollBar.Value - Math.Max(Scale(64), ClientSize.Height - Scale(48)));
                    break;
                case Keys.PageDown:
                    ScrollTo(_scrollBar.Value + Math.Max(Scale(64), ClientSize.Height - Scale(48)));
                    break;
                case Keys.Home:
                    ScrollTo(0);
                    break;
                case Keys.End:
                    ScrollTo(_scrollBar.Maximum);
                    break;
                default:
                    handled = false;
                    break;
            }
            return handled || base.ProcessCmdKey(ref msg, keyData);
        }

        private void ApplyScrollPosition(int? inset = null)
        {
            var left = inset ?? Scale(24);
            _stack.Location = new Point(left, -_scrollBar.Value);
            _stack.Invalidate();
        }

        private void HookScrollInput(Control control)
        {
            control.MouseWheel += OnChildMouseWheel;
            control.Enter += OnDescendantEnter;
            control.ControlAdded += OnDescendantAdded;
            foreach (Control child in control.Controls) HookScrollInput(child);
        }

        private void OnDescendantAdded(object? sender, ControlEventArgs eventArgs)
        {
            if (eventArgs.Control is { } control) HookScrollInput(control);
        }

        private void OnChildMouseWheel(object? sender, MouseEventArgs eventArgs) =>
            ScrollFromWheel(eventArgs);

        private void ScrollFromWheel(MouseEventArgs eventArgs)
        {
            if (_scrollBar.Maximum <= 0) return;
            _wheelRemainder += eventArgs.Delta;
            var notches = _wheelRemainder / SystemInformation.MouseWheelScrollDelta;
            if (notches == 0) return;
            _wheelRemainder -= notches * SystemInformation.MouseWheelScrollDelta;
            var lines = SystemInformation.MouseWheelScrollLines;
            var distance = lines < 0
                ? Math.Max(Scale(64), ClientSize.Height - Scale(48))
                : Scale(Math.Max(1, lines) * 16);
            ScrollTo(_scrollBar.Value - notches * distance);
            if (eventArgs is HandledMouseEventArgs handled) handled.Handled = true;
        }

        private void OnDescendantEnter(object? sender, EventArgs eventArgs)
        {
            if (sender is not Control control || !IsHandleCreated || !control.IsHandleCreated) return;
            var bounds = RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
            var margin = Scale(12);
            if (bounds.Top < margin)
            {
                ScrollTo(_scrollBar.Value + bounds.Top - margin);
            }
            else if (bounds.Bottom > ClientSize.Height - margin)
            {
                ScrollTo(_scrollBar.Value + bounds.Bottom - ClientSize.Height + margin);
            }
        }

        private int Scale(int value) => Math.Max(1, (int)Math.Round(value * _scale));
    }

    private sealed class SettingsScrollBar : Control
    {
        private readonly SettingsTheme _theme;
        private readonly float _scale;
        private int _value;
        private int _viewportHeight;
        private int _contentHeight;
        private int _dragOffset;
        private bool _dragging;
        private bool _hover;

        public SettingsScrollBar(SettingsTheme theme, float scale)
        {
            _theme = theme;
            _scale = scale;
            BackColor = theme.Content;
            Cursor = Cursors.Hand;
            TabStop = false;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
        }

        public event EventHandler? ValueChanged;

        public int Maximum { get; private set; }

        [DefaultValue(0)]
        public int Value
        {
            get => _value;
            set
            {
                var next = Math.Clamp(value, 0, Maximum);
                if (next == _value) return;
                _value = next;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void SetRange(int maximum, int viewportHeight, int contentHeight)
        {
            Maximum = Math.Max(0, maximum);
            _viewportHeight = Math.Max(0, viewportHeight);
            _contentHeight = Math.Max(0, contentHeight);
            Value = Math.Min(Value, Maximum);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.Clear(_theme.Content);
            if (Maximum <= 0) return;
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var track = TrackBounds();
            var thumb = ThumbBounds(track);
            using var trackPath = RoundedRectangle(track, Math.Max(1, track.Width / 2));
            using var trackBrush = new SolidBrush(_theme.ScrollTrack);
            eventArgs.Graphics.FillPath(trackBrush, trackPath);
            using var thumbPath = RoundedRectangle(thumb, Math.Max(1, thumb.Width / 2));
            using var thumbBrush = new SolidBrush(
                _dragging ? _theme.Accent : _hover ? _theme.Muted : _theme.ScrollThumb);
            eventArgs.Graphics.FillPath(thumbBrush, thumbPath);
        }

        protected override void OnMouseEnter(EventArgs eventArgs)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(eventArgs);
        }

        protected override void OnMouseLeave(EventArgs eventArgs)
        {
            _hover = false;
            if (!_dragging) Invalidate();
            base.OnMouseLeave(eventArgs);
        }

        protected override void OnMouseDown(MouseEventArgs eventArgs)
        {
            base.OnMouseDown(eventArgs);
            if (eventArgs.Button != MouseButtons.Left || Maximum <= 0) return;
            var track = TrackBounds();
            var thumb = ThumbBounds(track);
            if (thumb.Contains(eventArgs.Location))
            {
                _dragging = true;
                _dragOffset = eventArgs.Y - thumb.Top;
                Capture = true;
                Invalidate();
                return;
            }

            var page = Math.Max(Scale(64), _viewportHeight - Scale(48));
            Value += eventArgs.Y < thumb.Top ? -page : page;
        }

        protected override void OnMouseMove(MouseEventArgs eventArgs)
        {
            base.OnMouseMove(eventArgs);
            if (!_dragging || Maximum <= 0) return;
            var track = TrackBounds();
            var thumb = ThumbBounds(track);
            var travel = Math.Max(1, track.Height - thumb.Height);
            var thumbTop = Math.Clamp(eventArgs.Y - _dragOffset, track.Top, track.Bottom - thumb.Height);
            Value = (int)Math.Round((thumbTop - track.Top) * (double)Maximum / travel);
        }

        protected override void OnMouseUp(MouseEventArgs eventArgs)
        {
            if (eventArgs.Button == MouseButtons.Left) EndDrag();
            base.OnMouseUp(eventArgs);
        }

        protected override void OnMouseCaptureChanged(EventArgs eventArgs)
        {
            if (!Capture) EndDrag();
            base.OnMouseCaptureChanged(eventArgs);
        }

        private void EndDrag()
        {
            if (!_dragging) return;
            _dragging = false;
            Capture = false;
            Invalidate();
        }

        private Rectangle TrackBounds()
        {
            var width = Math.Min(Width, Scale(4));
            var top = Scale(4);
            return new Rectangle(
                Math.Max(0, (Width - width) / 2),
                top,
                Math.Max(1, width),
                Math.Max(1, Height - top * 2));
        }

        private Rectangle ThumbBounds(Rectangle track)
        {
            var width = Math.Min(Width, Scale(7));
            var proportionalHeight = _contentHeight <= 0
                ? track.Height
                : (int)Math.Round(track.Height * Math.Min(1d, _viewportHeight / (double)_contentHeight));
            var height = Math.Clamp(proportionalHeight, Math.Min(track.Height, Scale(32)), track.Height);
            var travel = Math.Max(0, track.Height - height);
            var top = Maximum <= 0
                ? track.Top
                : track.Top + (int)Math.Round(travel * (_value / (double)Maximum));
            return new Rectangle(
                Math.Max(0, (Width - width) / 2),
                top,
                Math.Max(1, width),
                Math.Max(1, height));
        }

        private int Scale(int value) => Math.Max(1, (int)Math.Round(value * _scale));
    }

    private abstract class SettingsRow : Panel
    {
        protected readonly SettingsTheme Theme;
        protected readonly float ScaleFactor;

        protected SettingsRow(SettingsTheme theme, float scale, int logicalHeight)
        {
            Theme = theme;
            ScaleFactor = scale;
            BackColor = theme.Content;
            Height = Scale(logicalHeight);
            Margin = Padding.Empty;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        protected int Scale(int value) => Math.Max(1, (int)Math.Round(value * ScaleFactor));

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Content);
            using var divider = new Pen(Theme.Border);
            e.Graphics.DrawLine(divider, 0, Height - 1, Width, Height - 1);
        }

        protected static Label CreateTitle(string text, SettingsTheme theme, float scale) => new()
        {
            AutoSize = false,
            BackColor = theme.Content,
            Font = ScaledFont("Segoe UI Semibold", 10f, FontStyle.Regular, scale),
            ForeColor = theme.Text,
            Text = text,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
        };

        protected static Label CreateDescription(string text, SettingsTheme theme, float scale) => new()
        {
            AutoSize = false,
            BackColor = theme.Content,
            Font = ScaledFont("Segoe UI", 8.75f, FontStyle.Regular, scale),
            ForeColor = theme.Muted,
            Text = text,
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true,
            UseMnemonic = false,
        };
    }

    private sealed class ValueSettingRow : SettingsRow
    {
        private readonly Label _title;
        private readonly Label _description;
        private readonly SettingsComboBox _field;
        private readonly int _fieldWidth;

        public ValueSettingRow(
            string title,
            string description,
            SettingsComboBox field,
            int fieldWidth,
            SettingsTheme theme,
            float scale)
            : base(theme, scale, 76)
        {
            _title = CreateTitle(title, theme, scale);
            _description = CreateDescription(description, theme, scale);
            _field = field;
            _fieldWidth = fieldWidth;
            Controls.Add(_title);
            Controls.Add(_description);
            Controls.Add(_field);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_title is null || _description is null || _field is null) return;
            var gap = Scale(20);
            var fieldHeight = Scale(36);
            var fieldWidth = Math.Min(_fieldWidth, Math.Max(Scale(132), Width * 2 / 5));
            var textWidth = Math.Max(1, Width - fieldWidth - gap);
            _title.SetBounds(0, Scale(11), textWidth, Scale(24));
            _description.SetBounds(0, _title.Bottom - Scale(1), textWidth, Scale(26));
            _field.SetBounds(Width - fieldWidth, Math.Max(0, (Height - fieldHeight) / 2), fieldWidth, fieldHeight);
        }
    }

    private sealed class ToggleSettingRow : SettingsRow
    {
        private readonly Label _title;
        private readonly Label _description;
        private readonly ToggleSwitch _toggle;

        public ToggleSettingRow(
            ToggleSwitch toggle,
            string title,
            string description,
            SettingsTheme theme,
            float scale)
            : base(theme, scale, 76)
        {
            _toggle = toggle;
            _title = CreateTitle(title, theme, scale);
            _description = CreateDescription(description, theme, scale);
            Controls.Add(_title);
            Controls.Add(_description);
            Controls.Add(_toggle);
            _title.Cursor = Cursors.Hand;
            _title.Click += (_, _) => _toggle.Activate();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_title is null || _description is null || _toggle is null) return;
            var gap = Scale(20);
            var textWidth = Math.Max(1, Width - _toggle.Width - gap);
            _title.SetBounds(0, Scale(11), textWidth, Scale(24));
            _description.SetBounds(0, _title.Bottom - Scale(1), textWidth, Scale(26));
            _toggle.Location = new Point(Width - _toggle.Width, Math.Max(0, (Height - _toggle.Height) / 2));
        }
    }

    private sealed class SectionNote : SettingsRow
    {
        private readonly Label _label;

        public SectionNote(string text, SettingsTheme theme, float scale)
            : base(theme, scale, 56)
        {
            _label = CreateDescription(text, theme, scale);
            Controls.Add(_label);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_label is null) return;
            _label.SetBounds(0, Scale(12), Width, Math.Max(1, Height - Scale(20)));
        }
    }

    private sealed class PaletteSettingRow : SettingsRow
    {
        private readonly Label _title;
        private readonly Label _description;
        private readonly PaletteGrid _grid;

        public PaletteSettingRow(
            string title,
            string description,
            PaletteGrid grid,
            SettingsTheme theme,
            float scale)
            : base(theme, scale, 112)
        {
            _title = CreateTitle(title, theme, scale);
            _description = CreateDescription(description, theme, scale);
            _grid = grid;
            Controls.Add(_title);
            Controls.Add(_description);
            Controls.Add(_grid);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_title is null || _description is null || _grid is null) return;
            var gap = Scale(20);
            if (Width < Scale(440))
            {
                Height = Scale(164);
                _title.SetBounds(0, Scale(10), Width, Scale(24));
                _description.SetBounds(0, _title.Bottom - Scale(1), Width, Scale(24));
                _grid.SetBounds(0, Scale(68), Width, Scale(82));
            }
            else
            {
                Height = Scale(112);
                var labelWidth = Math.Max(Scale(160), Width * 2 / 5);
                _title.SetBounds(0, Scale(18), labelWidth, Scale(24));
                _description.SetBounds(0, _title.Bottom - Scale(1), labelWidth, Scale(40));
                _grid.SetBounds(labelWidth + gap, Scale(16), Math.Max(1, Width - labelWidth - gap), Scale(80));
            }
        }
    }

    private sealed class ActionSettingRow : SettingsRow
    {
        private readonly Label _title;
        private readonly Label _description;
        private readonly Label _runtime;
        private readonly RoundedButton _button;

        public ActionSettingRow(
            string title,
            string description,
            string runtime,
            RoundedButton button,
            SettingsTheme theme,
            float scale)
            : base(theme, scale, 88)
        {
            _title = CreateTitle(title, theme, scale);
            _description = CreateDescription(description, theme, scale);
            _runtime = CreateDescription(runtime, theme, scale);
            _button = button;
            _title.AccessibleName = title;
            _description.AccessibleName = description;
            _runtime.AccessibleName = runtime;
            Controls.Add(_title);
            Controls.Add(_description);
            Controls.Add(_runtime);
            Controls.Add(_button);
        }

        public void SetRuntime(string runtime)
        {
            _runtime.Text = runtime;
            _runtime.AccessibleName = runtime;
            AccessibleDescription = runtime;
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_title is null || _description is null || _runtime is null || _button is null) return;
            var gap = Scale(20);
            var textWidth = Math.Max(1, Width - _button.Width - gap);
            _title.SetBounds(0, Scale(8), textWidth, Scale(23));
            _description.SetBounds(0, _title.Bottom - Scale(1), textWidth, Scale(25));
            _runtime.SetBounds(0, _description.Bottom - Scale(2), textWidth, Scale(23));
            _button.Location = new Point(Width - _button.Width, Math.Max(0, (Height - _button.Height) / 2));
        }
    }

    private sealed class AboutRow : SettingsRow
    {
        private readonly Label _product;
        private readonly Label _version;
        private readonly Label _description;
        private readonly Label _privacy;

        public AboutRow(
            string version,
            string description,
            string privacy,
            SettingsTheme theme,
            float scale)
            : base(theme, scale, 154)
        {
            _product = new Label
            {
                AutoSize = false,
                BackColor = theme.Content,
                Font = ScaledFont("Segoe UI Semibold", 12f, FontStyle.Regular, scale),
                ForeColor = theme.Text,
                Text = "ZGSTokenBar",
                TextAlign = ContentAlignment.MiddleLeft,
            };
            _version = CreateDescription(version, theme, scale);
            _version.ForeColor = theme.Accent;
            _description = CreateDescription(description, theme, scale);
            _privacy = CreateDescription(privacy, theme, scale);
            Controls.Add(_product);
            Controls.Add(_version);
            Controls.Add(_description);
            Controls.Add(_privacy);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_product is null || _version is null || _description is null || _privacy is null) return;
            var versionWidth = Math.Min(Scale(130), Width / 3);
            _product.SetBounds(0, Scale(14), Math.Max(1, Width - versionWidth), Scale(30));
            _version.SetBounds(Width - versionWidth, Scale(15), versionWidth, Scale(28));
            _description.SetBounds(0, Scale(54), Width, Scale(34));
            _privacy.SetBounds(0, Scale(96), Width, Scale(42));
        }
    }

    private sealed class SettingsComboBox : Button
    {
        private readonly SettingsTheme _theme;
        private readonly List<object> _items = [];
        private readonly Font _iconFont;
        private readonly int _radius;
        private ContextMenuStrip? _menu;
        private object? _selectedItem;
        private bool _hover;

        public SettingsComboBox(SettingsTheme theme, float scale)
        {
            _theme = theme;
            _iconFont = ScaledFont("Segoe Fluent Icons", 9f, FontStyle.Regular, scale);
            _radius = Math.Max(1, (int)Math.Round(4 * scale));
            BackColor = theme.Content;
            ForeColor = theme.Text;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = ScaledFont("Segoe UI", 9.5f, FontStyle.Regular, scale);
            TabStop = true;
            AccessibleRole = AccessibleRole.ComboBox;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        public List<object> Items => _items;

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public object? SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (ReferenceEquals(_selectedItem, value) || Equals(_selectedItem, value)) return;
                _selectedItem = value;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
                AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            }
        }

        public event EventHandler? SelectedIndexChanged;

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            ShowMenu();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Up or Keys.Left or Keys.Down or Keys.Right)
            {
                var current = Math.Max(0, _items.IndexOf(_selectedItem!));
                var next = e.KeyCode is Keys.Up or Keys.Left
                    ? Math.Max(0, current - 1)
                    : Math.Min(_items.Count - 1, current + 1);
                if (_items.Count > 0) SelectedItem = _items[next];
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(_theme.Content);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using var path = RoundedRectangle(bounds, _radius);
            using var fill = new SolidBrush(_hover && Enabled ? _theme.Hover : _theme.Surface);
            using var border = new Pen(Focused ? _theme.Accent : _theme.Border, Focused ? 2 : 1);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
            var arrowWidth = Math.Max(24, Height);
            var textBounds = new Rectangle(Math.Max(8, Height / 3), 0, Math.Max(1, Width - arrowWidth - Height / 3), Height);
            TextRenderer.DrawText(
                e.Graphics,
                _selectedItem?.ToString() ?? string.Empty,
                Font,
                textBounds,
                Enabled ? _theme.Text : _theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            var arrowBounds = new Rectangle(Width - arrowWidth, 0, arrowWidth, Height);
            TextRenderer.DrawText(
                e.Graphics,
                "\uE70D",
                _iconFont,
                arrowBounds,
                _theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void ShowMenu()
        {
            if (_items.Count == 0) return;
            _menu?.Dispose();
            var menu = new ContextMenuStrip
            {
                BackColor = _theme.Surface,
                ForeColor = _theme.Text,
                Font = Font,
                ShowImageMargin = false,
                ShowCheckMargin = false,
                Renderer = new DarkContextMenuRenderer(
                    _theme.Surface,
                    _theme.Hover,
                    _theme.Border,
                    _theme.Text,
                    _theme.Muted),
                AutoSize = false,
                Width = Width,
            };
            foreach (var item in _items)
            {
                var menuItem = new ToolStripMenuItem(item.ToString())
                {
                    BackColor = _theme.Surface,
                    ForeColor = _theme.Text,
                    AutoSize = false,
                    Width = Width,
                    Height = Math.Max(28, Height),
                    Tag = item,
                };
                menuItem.Click += (_, _) => SelectedItem = menuItem.Tag;
                menu.Items.Add(menuItem);
            }
            menu.Height = menu.Items.Cast<ToolStripItem>().Sum(item => item.Height) + 4;
            _menu = menu;
            menu.Show(this, new Point(0, Height));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _menu?.Dispose();
                _iconFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class ToggleSwitch : CheckBox
    {
        private readonly SettingsTheme _theme;
        private readonly int _radius;
        private bool _hover;

        public ToggleSwitch(SettingsTheme theme, float scale)
        {
            _theme = theme;
            _radius = Math.Max(1, (int)Math.Round(10 * scale));
            AutoSize = false;
            Appearance = Appearance.Button;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = theme.Content;
            Size = new Size(
                Math.Max(1, (int)Math.Round(40 * scale)),
                Math.Max(1, (int)Math.Round(20 * scale)));
            Text = string.Empty;
            TabStop = true;
            AccessibleRole = AccessibleRole.CheckButton;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        public void Activate() => OnClick(EventArgs.Empty);

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(_theme.Content);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            var trackColor = Checked
                ? _theme.Accent
                : _hover && Enabled
                    ? ControlPaint.Light(_theme.Border, .14f)
                    : _theme.Border;
            if (!Enabled) trackColor = ControlPaint.Dark(_theme.Surface, .08f);
            using var path = RoundedRectangle(bounds, _radius);
            using var track = new SolidBrush(trackColor);
            e.Graphics.FillPath(track, path);
            var inset = Math.Max(2, Height / 7);
            var knobSize = Math.Max(1, Height - inset * 2 - 1);
            var knobX = Checked ? Width - knobSize - inset - 1 : inset;
            using var knob = new SolidBrush(Enabled ? _theme.AccentText : _theme.Muted);
            e.Graphics.FillEllipse(knob, knobX, inset, knobSize, knobSize);
            if (Focused && ShowFocusCues)
            {
                using var focus = new Pen(_theme.Accent, Math.Max(1f, Height / 10f));
                e.Graphics.DrawPath(focus, path);
            }
        }
    }

    private sealed class PaletteGrid : Panel
    {
        private readonly float _scale;

        public PaletteGrid(float scale)
        {
            _scale = scale;
            TabStop = false;
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            var gap = Scale(6);
            var count = Math.Max(1, Controls.Count);
            var width = Math.Max(1, (ClientSize.Width - gap * (count - 1)) / count);
            for (var index = 0; index < Controls.Count; index++)
            {
                Controls[index].Bounds = new Rectangle(index * (width + gap), 0, width, ClientSize.Height);
            }
        }

        private int Scale(int value) => Math.Max(1, (int)Math.Round(value * _scale));
    }

    private sealed class PaletteChoiceButton : Button
    {
        private readonly SettingsTheme _theme;
        private readonly Color _paletteColor;
        private readonly int _radius;
        private readonly Font _iconFont;
        private bool _hover;
        private bool _selected;

        public PaletteChoiceButton(SettingsTheme theme, float scale, Color paletteColor, string label)
        {
            _theme = theme;
            _scale = scale;
            _paletteColor = paletteColor;
            _radius = Math.Max(1, (int)Math.Round(4 * scale));
            _iconFont = ScaledFont("Segoe Fluent Icons", 8f, FontStyle.Regular, scale);
            Text = label;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = theme.Content;
            ForeColor = theme.Text;
            Font = ScaledFont("Segoe UI", 8f, FontStyle.Regular, scale);
            TabStop = true;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        public void SetSelected(bool selected)
        {
            _selected = selected;
            FlatAppearance.BorderSize = selected ? 2 : 1;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        }

        protected override AccessibleObject CreateAccessibilityInstance() => new PaletteAccessibleObject(this);

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(_theme.Content);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using var path = RoundedRectangle(bounds, _radius);
            using var fill = new SolidBrush(_hover ? _theme.Hover : _theme.Surface);
            using var border = new Pen(_selected ? _theme.Accent : _theme.Border, _selected ? 2 : 1);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
            var swatch = new Rectangle(Scale(5), Scale(5), Math.Max(1, Width - Scale(10)), Math.Max(1, Height / 2));
            using var swatchPath = RoundedRectangle(swatch, Math.Max(2, _radius / 2));
            using var swatchFill = new SolidBrush(_paletteColor);
            e.Graphics.FillPath(swatchFill, swatchPath);
            if (_selected)
            {
                TextRenderer.DrawText(
                    e.Graphics,
                    "\uE73E",
                    _iconFont,
                    swatch,
                    _theme.AccentText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            var textBounds = new Rectangle(Scale(3), swatch.Bottom, Math.Max(1, Width - Scale(6)), Math.Max(1, Height - swatch.Bottom));
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                textBounds,
                _theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues)
            {
                using var focus = new Pen(_theme.Accent);
                e.Graphics.DrawRectangle(focus, Rectangle.Inflate(bounds, -3, -3));
            }
        }

        private int Scale(int value) => Math.Max(1, (int)Math.Round(value * _scale));

        private readonly float _scale;

        private sealed class PaletteAccessibleObject(PaletteChoiceButton owner) : ControlAccessibleObject(owner)
        {
            public override AccessibleRole Role => AccessibleRole.CheckButton;
            public override AccessibleStates State =>
                base.State | (owner._selected ? AccessibleStates.Checked : AccessibleStates.None);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _iconFont.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class RoundedButton : Button
    {
        private readonly SettingsTheme _theme;
        private readonly bool _primary;
        private readonly int _radius;
        private bool _hover;

        public RoundedButton(SettingsTheme theme, float scale, bool primary)
        {
            _theme = theme;
            _primary = primary;
            _radius = Math.Max(1, (int)Math.Round(4 * scale));
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = theme.Content;
            Font = ScaledFont("Segoe UI Semibold", 9f, FontStyle.Regular, scale);
            ForeColor = primary ? theme.AccentText : theme.Text;
            TabStop = true;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? _theme.Content);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using var path = RoundedRectangle(bounds, _radius);
            var fillColor = !Enabled
                ? ControlPaint.Dark(_theme.Footer, .06f)
                : _primary
                    ? _hover ? ControlPaint.Light(_theme.Accent, .08f) : _theme.Accent
                    : _hover ? _theme.Hover : _theme.Footer;
            using var fill = new SolidBrush(fillColor);
            using var border = new Pen(_primary ? fillColor : _theme.Border);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                bounds,
                Enabled ? (_primary ? _theme.AccentText : _theme.Text) : _theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues)
            {
                using var focus = new Pen(_theme.Accent, 2);
                e.Graphics.DrawRectangle(focus, Rectangle.Inflate(bounds, -3, -3));
            }
        }
    }
}
