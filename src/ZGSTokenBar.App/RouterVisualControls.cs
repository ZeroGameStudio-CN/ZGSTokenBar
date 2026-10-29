using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace ZGSTokenBar.App;

// Presentation primitives for the Router popup. No execution or transport state lives here.
internal sealed record RouterVisualPalette(Color Background)
{
    internal Color Surface => Mix(Background, Color.White, .035f);
    internal Color Input => Mix(Background, Color.White, .065f);
    internal Color Border => Mix(Background, Color.White, .10f);
    internal Color Text => Color.FromArgb(224, 232, 242);
    internal Color Muted => Color.FromArgb(142, 155, 174);
    internal Color Accent => Color.FromArgb(139, 187, 255);
    internal static Color Mix(Color first, Color second, float amount) => Color.FromArgb(
        (int)(first.R + (second.R - first.R) * amount),
        (int)(first.G + (second.G - first.G) * amount),
        (int)(first.B + (second.B - first.B) * amount));
    internal static Color State(string? tone) => tone switch
    {
        "positive" or "running" or "completed" => Color.FromArgb(111, 213, 173),
        "warning" or "cleanup" or "cancelled" => Color.FromArgb(232, 191, 113),
        "negative" or "attention" or "failed" or "danger" => Color.FromArgb(241, 145, 151),
        "idle" or "pending" or "offline" => Color.FromArgb(155, 168, 185),
        _ => Color.FromArgb(139, 187, 255),
    };
}

internal static class RouterVisualGeometry
{
    internal static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
    internal static float Scale(Control control) => Math.Max(1, control.Font.Unit == GraphicsUnit.Pixel
        ? control.Font.Size / 12.5f : control.Font.SizeInPoints * 96 / 72 / 12.5f);
}

internal enum RouterButtonKind { Quiet, Primary }
internal enum RouterButtonIcon { None, Refresh, Pin, Close, Previous, Next, Top, Up, Down, More, Filter, Export }

internal sealed class RouterButton : Button
{
    private bool _hovered, _selected;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal RouterVisualPalette Palette { get; set; } = new(Color.FromArgb(9, 11, 15));
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal RouterButtonKind Kind { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal RouterButtonIcon Icon { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Selected { get => _selected; set { _selected = value; Invalidate(); } }
    internal bool IconOnly => Icon != RouterButtonIcon.None;
    internal RouterButton()
    {
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        AutoSize = true; Height = 26; Margin = new Padding(2, 0, 2, 0); Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    public override Size GetPreferredSize(Size proposedSize)
    {
        var s = RouterVisualGeometry.Scale(this);
        var width = IconOnly ? 26 * s : TextRenderer.MeasureText(Text, Font).Width + 16 * s;
        return new((int)Math.Ceiling(width), (int)Math.Ceiling(26 * s));
    }
    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var s = RouterVisualGeometry.Scale(this);
        g.Clear(Parent?.BackColor ?? Palette.Background); g.SmoothingMode = SmoothingMode.AntiAlias;
        var accent = Palette.Accent;
        var emphasized = _selected || Kind == RouterButtonKind.Primary;
        var fill = _hovered && Enabled ? RouterVisualPalette.Mix(Palette.Background, accent, .14f)
            : emphasized ? RouterVisualPalette.Mix(Palette.Background, accent, .11f) : Parent?.BackColor ?? Palette.Background;
        var ink = !Enabled ? Color.FromArgb(104, 117, 135) : emphasized ? accent : Palette.Text;
        using var shape = RouterVisualGeometry.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), 6 * s);
        using var background = new SolidBrush(fill); g.FillPath(background, shape);
        if ((_hovered || Focused && ShowFocusCues || _selected) && Enabled)
        { using var border = new Pen(RouterVisualPalette.Mix(fill, accent, .32f), s); g.DrawPath(border, shape); }
        if (!IconOnly)
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        else DrawIcon(g, ink, s);
    }
    private void DrawIcon(Graphics g, Color color, float s)
    {
        var state = g.Save(); g.TranslateTransform(Width / 2f, Height / 2f); g.ScaleTransform(s, s);
        using var pen = new Pen(color, 1.45f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (Icon)
        {
            case RouterButtonIcon.Refresh:
                g.DrawArc(pen, -5, -5, 10, 10, 40, 280); g.DrawLines(pen, [new PointF(1, -6), new PointF(5, -5), new PointF(5, -1)]); break;
            case RouterButtonIcon.Close:
                g.DrawLine(pen, -4, -4, 4, 4); g.DrawLine(pen, -4, 4, 4, -4); break;
            case RouterButtonIcon.Pin:
                g.DrawLines(pen, [new PointF(-3, -6), new PointF(3, -6), new PointF(3, -1), new PointF(5, 2), new PointF(-5, 2), new PointF(-3, -1), new PointF(-3, -6)]);
                g.DrawLine(pen, 0, 2, 0, 7); break;
            case RouterButtonIcon.Previous: g.DrawLines(pen, [new PointF(2, -4), new PointF(-2, 0), new PointF(2, 4)]); break;
            case RouterButtonIcon.Next: g.DrawLines(pen, [new PointF(-2, -4), new PointF(2, 0), new PointF(-2, 4)]); break;
            case RouterButtonIcon.More:
                using (var dot = new SolidBrush(color)) for (var i = -1; i <= 1; i++) g.FillEllipse(dot, i * 5 - 1, -1, 2, 2); break;
            case RouterButtonIcon.Filter:
                g.DrawLine(pen, -6, -4, 6, -4); g.DrawLine(pen, -4, 0, 4, 0); g.DrawLine(pen, -1, 4, 1, 4); break;
            case RouterButtonIcon.Export:
                g.DrawLine(pen, 0, -6, 0, 2); g.DrawLines(pen, [new PointF(-3, -1), new PointF(0, 2), new PointF(3, -1)]);
                g.DrawLines(pen, [new PointF(-5, 2), new PointF(-5, 6), new PointF(5, 6), new PointF(5, 2)]); break;
            default:
                var sign = Icon == RouterButtonIcon.Down ? -1 : 1;
                g.DrawLine(pen, 0, -4 * sign, 0, 5 * sign);
                g.DrawLines(pen, [new PointF(-4, 0), new PointF(0, -4 * sign), new PointF(4, 0)]);
                if (Icon == RouterButtonIcon.Top) g.DrawLine(pen, -5, -7, 5, -7);
                break;
        }
        g.Restore(state);
    }
}

internal sealed class RouterStatusBadge : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color Accent { get; set; } = RouterVisualPalette.State(null);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal RouterVisualPalette Palette { get; set; } = new(Color.FromArgb(9, 11, 15));
    internal RouterStatusBadge() { AutoSize = true; Height = 25; SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
    public override Size GetPreferredSize(Size proposedSize)
    { var s = RouterVisualGeometry.Scale(this); return new(TextRenderer.MeasureText(Text, Font).Width + (int)(26 * s), (int)(25 * s)); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Size = GetPreferredSize(Size.Empty); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var s = RouterVisualGeometry.Scale(this); var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Palette.Background); g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RouterVisualGeometry.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), 6 * s);
        using var fill = new SolidBrush(RouterVisualPalette.Mix(Palette.Background, Accent, .10f)); g.FillPath(fill, path);
        using var dot = new SolidBrush(Accent); g.FillEllipse(dot, 8 * s, Height / 2f - 2.2f * s, 4.4f * s, 4.4f * s);
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(18 * s), 0, Width - (int)(20 * s), Height), Accent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

internal sealed class RouterSurface : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal RouterVisualPalette Palette { get; set; } = new(Color.FromArgb(9, 11, 15));
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Input { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool EmphasizedBorder { get; set; }
    internal RouterSurface() { DoubleBuffered = true; }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Palette.Background); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RouterVisualGeometry.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), 8 * RouterVisualGeometry.Scale(this));
        using var fill = new SolidBrush(Input ? Palette.Input : Palette.Surface); using var border = new Pen(EmphasizedBorder ? RouterVisualPalette.Mix(Palette.Background, Color.White, .18f) : Palette.Border);
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(border, path);
    }
}

internal sealed class RouterGrid : DataGridView
{
    private int _wheelRemainder;
    internal event EventHandler<int>? PageRequested;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string EmptyTitle { get; set; } = "队列为空";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string EmptyDetail { get; set; } = "新的任务会显示在这里";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal RouterVisualPalette Palette { get; set; } = new(Color.FromArgb(9, 11, 15));
    internal RouterGrid() { DoubleBuffered = true; }
    internal void PageFromWheel(int delta)
    {
        _wheelRemainder += delta;
        var pages = _wheelRemainder / SystemInformation.MouseWheelScrollDelta;
        _wheelRemainder -= pages * SystemInformation.MouseWheelScrollDelta;
        if (pages != 0) PageRequested?.Invoke(this, -pages);
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        PageFromWheel(e.Delta);
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Rows.Count != 0) return;
        var s = RouterVisualGeometry.Scale(this);
        var top = ColumnHeadersVisible ? ColumnHeadersHeight : 0;
        var y = top + Math.Max(0, Height - top - 55 * s) / 2;
        using var title = new SolidBrush(Palette.Text); using var detail = new SolidBrush(Palette.Muted);
        using var format = new StringFormat { Alignment = StringAlignment.Center };
        e.Graphics.DrawString(EmptyTitle, Font, title, new RectangleF(0, y, Width, 23 * s), format);
        e.Graphics.DrawString(EmptyDetail, Font, detail, new RectangleF(0, y + 25 * s, Width, 25 * s), format);
    }
}

internal sealed class RouterCombo : ComboBox
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal RouterVisualPalette Palette { get; set; } = new(Color.FromArgb(9, 11, 15));
    internal RouterCombo() { DrawMode = DrawMode.OwnerDrawFixed; FlatStyle = FlatStyle.Flat; DropDownStyle = ComboBoxStyle.DropDownList; }
    protected override void OnFontChanged(EventArgs e)
    { base.OnFontChanged(e); ItemHeight = (int)Math.Ceiling(Font.Size + 9 * RouterVisualGeometry.Scale(this)); }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var selected = DroppedDown && (e.State & DrawItemState.Selected) != 0;
        var background = selected ? RouterVisualPalette.Mix(Palette.Input, Palette.Accent, .15f) : Palette.Input;
        using var brush = new SolidBrush(background); e.Graphics.FillRectangle(brush, e.Bounds);
        var index = e.Index >= 0 ? e.Index : SelectedIndex;
        var text = index >= 0 && index < Items.Count ? GetItemText(Items[index]) : Text;
        var bounds = Rectangle.Inflate(e.Bounds, -(int)(7 * RouterVisualGeometry.Scale(this)), 0);
        TextRenderer.DrawText(e.Graphics, text, Font, bounds, Enabled ? Palette.Text : Palette.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        var printing = message.Msg is 0x0317 or 0x0318;
        if (message.Msg != 0x000F && !printing || !IsHandleCreated || Width < 20 || Height < 10 || printing && message.WParam == IntPtr.Zero) return;
        using var g = printing ? Graphics.FromHdc(message.WParam) : Graphics.FromHwnd(Handle);
        var s = RouterVisualGeometry.Scale(this); g.SmoothingMode = SmoothingMode.AntiAlias;
        using var nativeEdge = new Pen(Palette.Input, 4 * s);
        g.DrawRectangle(nativeEdge, 2 * s, 2 * s, Width - 4 * s, Height - 4 * s);
        using var arrowBackground = new SolidBrush(Palette.Input);
        g.FillRectangle(arrowBackground, Width - 23 * s, 0, 23 * s, Height);
        using var shape = RouterVisualGeometry.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), 5 * s);
        using var border = new Pen(Focused && ShowFocusCues ? Palette.Accent : Palette.Border, s); g.DrawPath(border, shape);
        var x = Width - 12 * s; var y = Height / 2f;
        using var arrow = new Pen(Palette.Muted, 1.3f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(arrow, [new PointF(x - 3 * s, y - s), new PointF(x, y + 2 * s), new PointF(x + 3 * s, y - s)]);
    }
}
