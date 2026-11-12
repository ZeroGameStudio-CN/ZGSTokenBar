using System.Drawing.Drawing2D;
using System.Reflection;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>Settings navigation rail: page buttons, keyboard focus and page selection.</summary>
internal sealed partial class SettingsForm
{
    private void BuildNavigation()
    {
        AddNavigation("general", "\uE713", _text.General);
        AddNavigation("providers", "\uE77B", _text.Modules);
        AddNavigation("notifications", "\uEA8F", _text.Notifications);
        AddNavigation("display", "\uE790", _text.Display);
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
}
