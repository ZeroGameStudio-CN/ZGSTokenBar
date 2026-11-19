using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

/// <summary>One native panel host for Router and work intake: DPI, anchor, tail, pinning and fonts.</summary>
internal abstract partial class MonitorPanelForm : Form, IPluginPopoverContent
{
    protected static readonly Size PreferredBodySize = new(760, 796);
    protected float _popoverScale = 1;
    protected bool _pinned;
    private float _appliedScale = -1;
    private QuotaBackgroundTheme? _appliedTheme;
    private Size _bodySize = PreferredBodySize;
    private PopoverTailSide _tailSide = PopoverTailSide.Bottom;
    private float _tailOffset;
    private Size _appliedWindowSize;
    private PopoverTailSide _appliedTailSide = PopoverTailSide.Bottom;
    private int _appliedTailOffset = -1;
    private readonly Dictionary<Control, (string Family, float Pixels, FontStyle Style)> _logicalFonts = [];
    private readonly Dictionary<(string Family, float Pixels, FontStyle Style), Font> _scaledFonts = [];
    internal bool Pinned => _pinned;
    public abstract bool InteractionOpen { get; }
    public event EventHandler<bool>? PinChanged;
    public event EventHandler? CloseRequested;
    protected void RequestPinChange(bool pinned) => PinChanged?.Invoke(this, pinned);
    protected void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);
    internal bool HandlePanelShortcut(Keys key)
    {
        if (key != Keys.Escape) return false;
        RequestClose();
        return true;
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData) =>
        HandlePanelShortcut(keyData) || base.ProcessCmdKey(ref message, keyData);

    protected MonitorPanelForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ClientSize = PreferredBodySize;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        ShowIcon = false;
        TopMost = true;
        DoubleBuffered = true;
        _hoverMotionTimer.Tick += (_, _) => AdvanceHoverMotion();
        BackColor = UiTokens.CardSurface;
        SetLogicalFont(this, "Microsoft YaHei UI", RouterVisualGeometry.LogicalFontPixels);
    }

    protected override bool ShowWithoutActivation => !_pinned;
    protected override CreateParams CreateParams
    {
        get { var value = base.CreateParams; value.ExStyle |= 0x80; if (!_pinned) value.ExStyle |= 0x08000000; return value; }
    }
    public virtual void SetPinned(bool pinned)
    {
        var changed = _pinned != pinned;
        _pinned = pinned;
        if (changed && IsHandleCreated)
        {
            var style = GetWindowLongPtr(Handle, -20).ToInt64();
            style = pinned ? style & ~0x08000000L : style | 0x08000000L;
            SetWindowLongPtr(Handle, -20, (nint)style);
        }
        if (pinned)
        {
            FinishHoverMotion();
            if (changed && Visible) Activate();
        }
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    protected abstract void ApplyPalette(QuotaBackgroundTheme theme);
    protected static IEnumerable<Control> Children(Control parent) => parent.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Children(child)));

    protected void ApplyMonitorPalette(RouterVisualPalette palette, params Label[] primaryLabels)
    {
        BackColor = palette.Background;
        foreach (var control in Children(this))
        {
            // Layout panels carry their nearest card's fill down to labels and buttons.
            var background = control.Parent?.BackColor ?? BackColor;
            switch (control)
            {
                case RouterSurface surface:
                    surface.Palette = palette;
                    surface.BackColor = surface.Fill;
                    surface.Invalidate();
                    break;
                case RouterButton button:
                    button.Palette = palette;
                    button.Invalidate();
                    break;
                case RouterStatusBadge badge:
                    badge.Palette = palette;
                    badge.Invalidate();
                    break;
                case RouterCombo combo:
                    combo.Palette = palette;
                    combo.BackColor = palette.Input;
                    combo.ForeColor = palette.Text;
                    combo.Invalidate();
                    break;
                case RouterGrid grid:
                    grid.ApplyPalette(palette, background, _popoverScale);
                    break;
                case Label label:
                    label.BackColor = background;
                    label.ForeColor = primaryLabels.Contains(label) || label.Font.Bold ? UiTokens.TextStrong : palette.Muted;
                    break;
                case CheckBox check:
                    check.BackColor = background;
                    check.ForeColor = palette.Text;
                    break;
                case Panel panel:
                    panel.BackColor = background;
                    break;
                case TextBox box:
                    box.BackColor = palette.Input;
                    box.ForeColor = palette.Text;
                    break;
            }
        }
    }

    internal static TableLayoutPanel CreatePanelLayout(int currentHeight = 110, int footerHeight = 24)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(12), ColumnCount = 1, RowCount = 5 };
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var row in new[] { new RowStyle(SizeType.Absolute, 64), new RowStyle(SizeType.Absolute, currentHeight),
            new RowStyle(SizeType.Percent, 50), new RowStyle(SizeType.Percent, 50), new RowStyle(SizeType.Absolute, footerHeight) })
            root.RowStyles.Add(row);
        return root;
    }

    protected void SetEmptyTopTable(TableLayoutPanel root, RouterGrid grid, bool empty)
    {
        grid.ColumnHeadersVisible = !empty;
        var row = root.RowStyles[2];
        var sizeType = empty ? SizeType.Absolute : SizeType.Percent;
        if (row.SizeType == sizeType) return;
        row.SizeType = sizeType;
        row.Height = empty ? (int)Math.Round(82 * _popoverScale) : 50;
    }

    internal static TableLayoutPanel CreateHeader(Control left, Control right, int rightWidth)
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1 };
        header.ColumnStyles.Add(new(SizeType.Percent, 100));
        header.ColumnStyles.Add(new(SizeType.Absolute, rightWidth));
        header.RowStyles.Add(new(SizeType.Percent, 100));
        header.Controls.Add(left, 0, 0); header.Controls.Add(right, 1, 0);
        return header;
    }

    internal static TableLayoutPanel CreateNavigation(Control header, Control projects, string title)
    {
        var navigation = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 1, RowCount = 2 };
        navigation.ColumnStyles.Add(new(SizeType.Percent, 100));
        navigation.RowStyles.Add(new(SizeType.Absolute, 30));
        navigation.RowStyles.Add(new(SizeType.Percent, 100));
        navigation.Controls.Add(header, 0, 0);
        var surface = new RouterSurface { Dock = DockStyle.Fill, Input = true, Padding = new(8, 2, 8, 2),
            Margin = new(0, 0, 0, 4), AccessibleName = title };
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1 };
        row.ColumnStyles.Add(new(SizeType.Absolute, 56)); row.ColumnStyles.Add(new(SizeType.Percent, 100));
        row.RowStyles.Add(new(SizeType.Percent, 100));
        row.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true, UseCompatibleTextRendering = true }, 0, 0);
        row.Controls.Add(projects, 1, 0); surface.Controls.Add(row); navigation.Controls.Add(surface, 0, 1);
        return navigation;
    }
    internal void Prewarm(float scale, QuotaBackgroundTheme theme)
    {
        if (IsDisposed || Visible) return;
        using var trace = PerfTrace.Measure("router.panel.prewarm");
        SetPinned(false);
        var nextScale = Math.Max(1, scale);
        SuspendLayout();
        if (Math.Abs(nextScale - _popoverScale) > .001) Scale(new SizeF(nextScale / _popoverScale, nextScale / _popoverScale));
        _popoverScale = nextScale;
        _bodySize = new Size((int)(PreferredBodySize.Width * nextScale), (int)(PreferredBodySize.Height * nextScale));
        var tail = (int)(8 * nextScale);
        _tailSide = PopoverTailSide.Bottom;
        _tailOffset = _bodySize.Width / 2f;
        var windowSize = new Size(_bodySize.Width, _bodySize.Height + tail);
        Padding = new Padding(2, 2, 2, 2 + tail);
        ClientSize = windowSize;
        using (var path = SurfacePath()) { var old = Region; Region = new Region(path); old?.Dispose(); }
        _appliedWindowSize = windowSize;
        _appliedTailSide = _tailSide;
        _appliedTailOffset = (int)Math.Round(_tailOffset);
        ResumeLayout(false);
        _ = Handle;
        foreach (var control in Children(this)) _ = control.Handle;
        ApplyLogicalFonts();
        ApplyPalette(theme);
        _appliedScale = _popoverScale; _appliedTheme = theme;
        PerformLayout();
        // Prepare layered-window interop and the timer's native window off the hover path.
        var opacity = Opacity;
        Opacity = .99;
        Opacity = opacity;
        _hoverMotionTimer.Start();
        _hoverMotionTimer.Stop();
    }

    internal void ShowFor(BarForm owner, Rectangle anchor, bool pinned, float scale, QuotaBackgroundTheme theme,
        bool animate = false, bool immediate = false)
    {
        ConfigureFor(anchor, pinned, scale, theme, Screen.FromRectangle(anchor).WorkingArea);
        ShowPrepared(owner, animate, immediate);
    }

    internal void ShowPrepared(BarForm owner, bool animate, bool immediate = false)
    {
        _animateHover = animate;
        var firstShow = !IsHandleCreated;
        var opening = !Visible;
        if (opening)
        {
            var transitioning = _hoverMotionTimer.Enabled;
            _hoverMotionTimer.Stop();
            _hoverExiting = false;
            // Keep the outgoing sibling visible through native child creation and first paint.
            // Showing an opaque replacement here exposes the child HWNDs' unpainted backgrounds.
            if (immediate) Opacity = 0;
            else if (animate && !_pinned) Opacity = .01;
            else if (transitioning) Opacity = 1;
            this.ShowPopover(BarForm.PopoverOwnerFor(owner));
        }
        // Creating the native window can rescale fonts after ConfigureFor ran; only then re-apply.
        if (firstShow || FontsDrifted())
        {
            ApplyLogicalFonts();
            ApplyPalette(_appliedTheme ?? owner.UiTheme);
        }
        if (opening && immediate)
        {
            // Update() paints only this HWND; Refresh() also completes every native child frame.
            Refresh();
            Opacity = 1;
        }
        if (opening && animate && !_pinned && !immediate) StartHoverMotion(exiting: false);
        if (_pinned) { FinishHoverMotion(); Activate(); }
    }
    internal void ConfigureFor(Rectangle anchor, bool pinned, float scale, QuotaBackgroundTheme theme, Rectangle area)
    {
        _hoverAnchor = anchor;
        SetPinned(pinned);
        var nextScale = Math.Max(1, scale);
        if (Math.Abs(nextScale - _popoverScale) > .001)
        {
            SuspendLayout();
            Scale(new SizeF(nextScale / _popoverScale, nextScale / _popoverScale));
            ResumeLayout(true);
        }
        _popoverScale = nextScale;
        // Fonts and palette walk every child control; skip it when nothing changed since the last show.
        // ...unless native DPI handling rewrote the fonts while the panel was hidden (same scale).
        if (Math.Abs(_appliedScale - _popoverScale) > .001 || !Equals(_appliedTheme, theme) || FontsDrifted())
        {
            ApplyLogicalFonts();
            ApplyPalette(theme);
            _appliedScale = _popoverScale;
            _appliedTheme = theme;
        }
        _bodySize = new(Math.Min((int)(PreferredBodySize.Width * _popoverScale), area.Width - (int)(16 * _popoverScale)),
            Math.Min((int)(PreferredBodySize.Height * _popoverScale), area.Height - (int)(28 * _popoverScale)));
        var tail = (int)(8 * _popoverScale);
        var placement = TaskbarMiniPopoverMath.Place(anchor, _bodySize, tail, (int)(3 * _popoverScale), area);
        _tailSide = placement.TailSide; _tailOffset = placement.TailOffset;
        var shapeChanged = _appliedWindowSize != placement.WindowSize || _appliedTailSide != _tailSide;
        if (shapeChanged)
        {
            SuspendLayout();
            Padding = new Padding(2 + (_tailSide == PopoverTailSide.Left ? tail : 0), 2 + (_tailSide == PopoverTailSide.Top ? tail : 0),
                2 + (_tailSide == PopoverTailSide.Right ? tail : 0), 2 + (_tailSide == PopoverTailSide.Bottom ? tail : 0));
            ClientSize = placement.WindowSize;
            ResumeLayout(true);
            _appliedWindowSize = placement.WindowSize;
            _appliedTailSide = _tailSide;
        }
        Location = placement.Location;
        var tailOffsetRounded = (int)Math.Round(_tailOffset);
        if (shapeChanged || _appliedTailOffset != tailOffsetRounded || Region is null)
        {
            using var path = SurfacePath();
            var old = Region;
            Region = new Region(path);
            old?.Dispose();
            _appliedTailOffset = tailOffsetRounded;
        }
    }
    protected void SetLogicalFont(Control control, string family, float pixels, FontStyle style = FontStyle.Regular)
    {
        _logicalFonts[control] = (family, pixels, style);
        control.Font = (Font)ScaledFont((family, pixels, style)).Clone();
    }
    private Font ScaledFont((string Family, float Pixels, FontStyle Style) logical)
    {
        var key = (logical.Family, logical.Pixels * _popoverScale, logical.Style);
        if (!_scaledFonts.TryGetValue(key, out var font))
        { font = new Font(key.Family, key.Item2, key.Style, GraphicsUnit.Pixel); _scaledFonts[key] = font; }
        return font;
    }
    /// <summary>Cheap check (no font allocation): does the form's font still match its logical size?</summary>
    private bool FontsDrifted() =>
        _logicalFonts.TryGetValue(this, out var logical)
        && (!IsFontUsable(Font) || Math.Abs(Font.Size - logical.Pixels * _popoverScale) > .1);

    private static bool IsFontUsable(Font font)
    {
        try { _ = font.Height; return true; }
        catch (ArgumentException) { return false; }
    }

    protected void ApplyLogicalFonts()
    {
        SuspendLayout();
        // Include project chips created after construction, without retaining disposed chips.
        // Give each control its own Font clone so WinForms' WM_DPICHANGED_BEFOREPARENT scaling
        // on one child never disposes the Font still bound to sibling ComboBoxes.
        foreach (var control in new[] { this }.Concat(Children(this)))
        {
            var expected = ScaledFont(_logicalFonts.GetValueOrDefault(control, _logicalFonts[this]));
            if (!IsFontUsable(control.Font)) control.Font = Control.DefaultFont;
            if (!control.Font.Equals(expected)) control.Font = (Font)expected.Clone();
        }
        ResumeLayout(true);
    }
    private GraphicsPath SurfacePath()
    {
        var tail = 8 * _popoverScale;
        var body = TaskbarPopoverMath.BodyBounds(_tailSide, _bodySize.Width, _bodySize.Height, tail);
        var path = new GraphicsPath { FillMode = FillMode.Winding };
        var d = UiTokens.RadiusPanel * 2 * _popoverScale;
        path.AddArc(body.Left, body.Top, d, d, 180, 90); path.AddArc(body.Right - d, body.Top, d, d, 270, 90);
        path.AddArc(body.Right - d, body.Bottom - d, d, d, 0, 90); path.AddArc(body.Left, body.Bottom - d, d, d, 90, 90); path.CloseFigure();
        path.AddPolygon(TaskbarPopoverMath.TailPoints(_tailSide, body, _tailOffset, tail));
        return path;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = SurfacePath();
        using var border = new Pen(UiTokens.Hairline);
        e.Graphics.DrawPath(border, path);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _hoverMotionTimer.Stop(); _hoverMotionTimer.Dispose(); }
        base.Dispose(disposing);
        if (disposing) foreach (var font in _scaledFonts.Values) font.Dispose();
    }
}
