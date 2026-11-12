using System.Drawing.Drawing2D;
using System.Reflection;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>Settings pages: page layout/building and the scrollable page with its themed scroll bar.</summary>
internal sealed partial class SettingsForm
{
    private void BuildPages()
    {
        var general = AddPage("general", _text.General);
        general.Add(new ValueSettingRow(_text.Language, _text.LanguageHint, _locale, Scale(196), _theme, _scale));
        general.Add(new ValueSettingRow(_text.AutomaticRefresh, _text.AutomaticRefreshHint, _refreshMinutes, Scale(196), _theme, _scale));
        general.Add(new SectionNote(_text.IsChinese ? "拖动 Bar 靠近任务栏即可吸附，拖离则浮动；位置会自动保存。" : "Drag the Bar near the taskbar to dock it, or away to float; its position is saved.", _theme, _scale, lines: 1));
        general.Add(new ToggleSettingRow(_openAtLogin, _text.StartWithWindows, _text.StartWithWindowsHint, _theme, _scale));
        general.Add(new ToggleSettingRow(_animations, _text.AnimationsTitle, _text.Animations, _theme, _scale));
        general.Add(new ToggleSettingRow(_embedTaskbar, _text.EmbedTaskbarTitle, _text.EmbedTaskbarHint, _theme, _scale));

        var providers = AddPage("providers", _text.Modules);
        providers.Add(new ToggleSettingRow(
            _systemMetrics,
            _text.SystemUsageTitle,
            _text.SystemMetricsModuleHint,
            _theme,
            _scale));
        providers.Add(new ToggleSettingRow(_claude, "Claude", _text.ClaudeProviderHint, _theme, _scale));
        providers.Add(new ToggleSettingRow(_codex, "Codex", _text.CodexProviderHint, _theme, _scale));
        providers.Add(new ToggleSettingRow(_grok, "Grok",
            _text.IsChinese ? "读取本机 Grok 登录的套餐额度和预付余额" : "Reads the local Grok sign-in for plan quota and prepaid balance", _theme, _scale));
        providers.Add(new ToggleSettingRow(_antigravity, "Antigravity",
            _text.IsChinese ? "复用本机登录，读取模型剩余额度" : "Uses the local sign-in to read model quota", _theme, _scale));
        providers.Add(new ValueSettingRow(AntigravityGroupTitle, AntigravityGroupHint, _antigravityGroup, Scale(196), _theme, _scale));
        foreach (var (id, toggle) in _usageProviderToggles)
        {
            var name = UsageProviderRegistry.Find(id)?.Definition.DisplayName ?? id;
            providers.Add(new ToggleSettingRow(toggle, name, UsageProviderHint(name), _theme, _scale));
        }
        if (_dockItemToggles.Count > 0)
        {
            providers.Add(new SectionNote(_text.IsChinese
                ? "Bar 显示项目：逐个开关某个服务或账号是否显示在 Bar 上（只影响显示，不停止读取）。"
                : "Bar items: choose which services or accounts appear on the Bar (display only; reading continues).", _theme, _scale));
            foreach (var (entry, toggle) in _dockItemToggles)
            {
                var title = entry.Detail is { Length: > 0 } detail ? $"{entry.Title} · {detail}" : entry.Title;
                providers.Add(new ToggleSettingRow(toggle, title, DockItemHint, _theme, _scale));
            }
        }
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
        display.Add(new PaletteSettingRow(
            _text.BackgroundPalette,
            _text.BackgroundPaletteHint,
            _paletteGrid,
            _theme,
            _scale));

        var advanced = AddPage("advanced", _text.Advanced);
        advanced.Add(new ToggleSettingRow(
            _refreshClaudeOAuth,
            _text.AllowClaudeRefresh,
            _text.ClaudeRefreshHint,
            _theme,
            _scale));

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
}
