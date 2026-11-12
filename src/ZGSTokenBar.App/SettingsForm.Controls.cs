using System.Drawing.Drawing2D;
using System.Reflection;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>Settings theme and owner-drawn controls: combo box, toggle switch, palette grid and buttons.</summary>
internal sealed partial class SettingsForm
{
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
            // Shared kit tokens: neutral window chrome, the same text/accent/warning as every hover card.
            : new(
                UiTokens.WindowContent,
                UiTokens.WindowSidebar,
                UiTokens.WindowFooter,
                UiTokens.WindowSurface,
                UiTokens.WindowHover,
                UiTokens.WindowBorder,
                UiTokens.WindowBorder,
                UiTokens.WindowScrollThumb,
                UiTokens.TextStrong,
                UiTokens.Muted,
                UiTokens.AccentFill,
                Color.White,
                UiTokens.Warning);
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
