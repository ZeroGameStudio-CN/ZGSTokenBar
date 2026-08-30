using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed class ProviderRadarPopoverForm : Form
{
    private const int ToolWindowStyle = 0x00000080;
    private const int NoActivateStyle = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const int EntranceDurationMs = 130;
    private const int ExitDurationMs = 90;
    private readonly System.Windows.Forms.Timer _motionTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _countdownTimer = new() { Interval = 1_000 };
    private readonly RadarPopoverRenderer _renderer = new();
    private RadarViewState _state = new(null, null, false, null);
    private RadarPresentationResult? _presentation;
    private RadarPopoverLayout _layout = RadarPopoverLayout.Create(96, 0, false);
    private CodexSpendHistoryLayout? _historyLayout;
    private NativeText _text = NativeText.For("zh-CN");
    private Image? _logo;
    private CodexTokenUsageSummary? _tokenUsage;
    private AiGatewayUsageSummary? _aiGatewayUsage;
    private PopoverTailSide _tailSide = PopoverTailSide.Bottom;
    private int _tailOffset = RadarPopoverLayout.LogicalWidth / 2;
    private bool _animateMotion;
    private bool _pinned;
    private bool _deepSeekOnly;
    private bool _historyVisible;
    private bool _spendCardHovered;
    private bool _backHovered;
    private int _selectedHistoryDayIndex = -1;
    private Rectangle _anchorScreen;
    private bool _exiting;
    private DateTime _motionStarted;
    private Point _motionFrom;
    private Point _motionTo;
    private double _motionFromOpacity = 1;

    public ProviderRadarPopoverForm()
    {
        _motionTimer.Tick += (_, _) => AdvanceMotion();
        _countdownTimer.Tick += (_, _) => AdvanceCountdown();
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(7, 12, 24);
        FormBorderStyle = FormBorderStyle.None;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = _text.RadarTitle;
        TopMost = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw, true);
    }

    public event EventHandler? SpendHistoryRequested;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= ToolWindowStyle | NoActivateStyle;
            return parameters;
        }
    }

    public void ShowFor(
        BarForm owner,
        Rectangle anchorScreen,
        RadarViewState state,
        NativeText text,
        Image logo,
        float scale,
        bool animateMotion,
        bool pinned,
        bool radarEnabled,
        CodexTokenUsageSummary? tokenUsage,
        AiGatewayUsageSummary? aiGatewayUsage = null,
        bool deepSeekOnly = false)
    {
        var wasVisible = Visible;
        _motionTimer.Stop();
        _exiting = false;
        _animateMotion = animateMotion;
        _pinned = pinned;
        _deepSeekOnly = deepSeekOnly;
        _state = state;
        _text = text;
        _tokenUsage = tokenUsage;
        _aiGatewayUsage = aiGatewayUsage;
        Text = radarEnabled
            ? deepSeekOnly ? _text.DeepSeekRadarTitle : _text.RadarTitle
            : _text.CodexTokenTitle;
        _presentation = radarEnabled && state.Snapshot is { } snapshot
            ? deepSeekOnly
                ? RadarPresentation.DeepSeekOnly(RadarPresentation.Build(snapshot))
                : RadarPresentation.CodexOnly(RadarPresentation.Build(snapshot))
            : null;
        _logo = logo;
        var dpi = Math.Max(
            96,
            (int)Math.Round(scale * 96, MidpointRounding.AwayFromZero));
        var hasInlineError = radarEnabled && _presentation is not null && state.Error is not null;
        var modelKeys = _presentation?.Rows
            .Select(row => row.Model.Model)
            .ToArray() ?? [];
        _layout = radarEnabled
            ? RadarPopoverLayout.Create(
                dpi,
                modelKeys,
                hasInlineError,
                state.Snapshot?.ResetWindow?.Open == true,
                tokenUsage is not null)
            : RadarPopoverLayout.CreateTokenUsage(dpi);
        _historyLayout = !deepSeekOnly && HasSpendHistory(tokenUsage)
            ? CodexSpendHistoryLayout.Create(
                dpi,
                radarEnabled,
                Math.Min(CodexSpendHistoryLayout.MaximumTrendDays, tokenUsage!.SpendHistory!.Days.Count))
            : null;
        if (_historyLayout is null) _historyVisible = false;
        _anchorScreen = anchorScreen;
        var placement = CurrentPlacement();
        _tailSide = placement.TailSide;
        _tailOffset = placement.TailOffset;
        ClientSize = placement.WindowSize;
        UpdateWindowRegion();
        Invalidate();

        if (!wasVisible && animateMotion)
        {
            _motionFrom = TaskbarPopoverMath.OffsetFromAnchor(
                placement.Location,
                _tailSide,
                3);
            _motionTo = placement.Location;
            _motionFromOpacity = .01;
            _motionStarted = DateTime.UtcNow;
            Opacity = .01;
            Location = _motionFrom;
            Show(owner);
            if (!TaskbarPlacement.ShowAt(Handle, _motionFrom, placement.WindowSize)) Location = _motionFrom;
            _motionTimer.Start();
        }
        else
        {
            Opacity = 1;
            if (!wasVisible) Show(owner);
            if (!TaskbarPlacement.ShowAt(Handle, placement.Location, placement.WindowSize))
            {
                Location = placement.Location;
            }
        }
        UpdateCountdownTimer();
    }

    public void HidePopover()
    {
        _countdownTimer.Stop();
        ResetHistoryView();
        if (!Visible) return;
        _motionTimer.Stop();
        if (!_animateMotion)
        {
            Opacity = 1;
            Hide();
            return;
        }

        _exiting = true;
        _motionFrom = Location;
        _motionTo = TaskbarPopoverMath.OffsetFromAnchor(Location, _tailSide, 2);
        _motionFromOpacity = Opacity;
        _motionStarted = DateTime.UtcNow;
        _motionTimer.Start();
    }

    internal static bool HasActiveResetCountdown(
        RadarResetWindow? window,
        DateTimeOffset now) =>
        RadarResetTiming.RefreshIntervalMilliseconds(window, now)
            == RadarResetTiming.ExactRefreshIntervalMilliseconds;

    private void UpdateCountdownTimer()
    {
        if (!_layout.TokenOnly
            && !_historyVisible
            && Visible
            && HasActiveResetCountdown(_state.Snapshot?.ResetWindow, DateTimeOffset.UtcNow))
        {
            _countdownTimer.Start();
        }
        else
        {
            _countdownTimer.Stop();
        }
    }

    private void AdvanceCountdown()
    {
        Invalidate();
        if (!HasActiveResetCountdown(_state.Snapshot?.ResetWindow, DateTimeOffset.UtcNow))
        {
            _countdownTimer.Stop();
        }
    }

    private void AdvanceMotion()
    {
        var duration = _exiting ? ExitDurationMs : EntranceDurationMs;
        var progress = Math.Clamp((DateTime.UtcNow - _motionStarted).TotalMilliseconds / duration, 0, 1);
        var eased = _exiting
            ? TaskbarPopoverMath.ExitEase(progress)
            : TaskbarPopoverMath.EntranceEase(progress);
        var location = TaskbarPopoverMath.Interpolate(_motionFrom, _motionTo, eased);
        Opacity = _exiting
            ? TaskbarPopoverMath.FadeOut(_motionFromOpacity, eased)
            : TaskbarPopoverMath.FadeIn(_motionFromOpacity, eased);
        if (!TaskbarPlacement.ShowAt(Handle, location, ClientSize)) Location = location;
        if (progress < 1) return;

        _motionTimer.Stop();
        if (_exiting)
        {
            _exiting = false;
            Opacity = 1;
            Hide();
        }
        else
        {
            Opacity = 1;
            if (!TaskbarPlacement.ShowAt(Handle, _motionTo, ClientSize)) Location = _motionTo;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_historyVisible
            && _historyLayout is { } historyLayout
            && _tokenUsage is { SpendHistory: not null } tokenUsage)
        {
            _renderer.DrawSpendHistory(
                e.Graphics,
                historyLayout,
                _tailSide,
                _tailOffset,
                tokenUsage,
                _logo,
                _text,
                _selectedHistoryDayIndex,
                _pinned);
            return;
        }
        _renderer.Draw(
            e.Graphics,
            _layout,
            _tailSide,
            _tailOffset,
            _state,
            _presentation,
            _logo,
            _text,
            _tokenUsage,
            _aiGatewayUsage,
            _pinned,
            radarTitle: _deepSeekOnly
                ? _text.DeepSeekRadarTitle
                : _text.RadarTitle,
            spendCardHovered: _spendCardHovered);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_historyVisible && _historyLayout is { } historyLayout)
        {
            var backHovered = historyLayout
                .InWindow(historyLayout.BackBounds, _tailSide)
                .Contains(e.Location);
            var selected = HistoryDayIndexAt(e.Location, historyLayout);
            var changed = backHovered != _backHovered;
            _backHovered = backHovered;
            if (selected >= 0 && selected != _selectedHistoryDayIndex)
            {
                _selectedHistoryDayIndex = selected;
                changed = true;
            }
            Cursor = backHovered ? Cursors.Hand : Cursors.Default;
            if (changed) Invalidate();
            return;
        }

        var hovered = HasSpendHistory(_tokenUsage) && SpendCardBounds().Contains(e.Location);
        if (hovered == _spendCardHovered) return;
        _spendCardHovered = hovered;
        Cursor = hovered ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_spendCardHovered && !_backHovered) return;
        _spendCardHovered = false;
        _backHovered = false;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (_historyVisible && _historyLayout is { } historyLayout)
        {
            if (historyLayout.InWindow(historyLayout.BackBounds, _tailSide).Contains(e.Location))
            {
                _historyVisible = false;
                _backHovered = false;
                _selectedHistoryDayIndex = -1;
                ApplyCurrentPlacement();
            }
            return;
        }
        if (_historyLayout is null
            || !HasSpendHistory(_tokenUsage)
            || !SpendCardBounds().Contains(e.Location))
        {
            return;
        }

        _historyVisible = true;
        _spendCardHovered = false;
        _selectedHistoryDayIndex = Math.Max(0, _historyLayout.BarBounds.Count - 1);
        _pinned = true;
        SpendHistoryRequested?.Invoke(this, EventArgs.Empty);
        ApplyCurrentPlacement();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWindowRegion();
    }

    private void UpdateWindowRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        var next = _historyVisible && _historyLayout is { } historyLayout
            ? RadarPopoverRenderer.CreateWindowRegion(historyLayout, _tailSide, _tailOffset)
            : RadarPopoverRenderer.CreateWindowRegion(_layout, _tailSide, _tailOffset);
        Region?.Dispose();
        Region = next;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmMouseActivate)
        {
            message.Result = (IntPtr)MaNoActivate;
            return;
        }
        base.WndProc(ref message);
    }

    private TaskbarMiniPopoverPlacement CurrentPlacement()
    {
        var bodySize = _historyVisible && _historyLayout is { } historyLayout
            ? historyLayout.BodySize
            : _layout.BodySize;
        var tailSize = _historyVisible && _historyLayout is { } activeHistoryLayout
            ? activeHistoryLayout.TailSize
            : _layout.TailSize;
        var gap = _historyVisible && _historyLayout is { } activeHistory
            ? activeHistory.Gap
            : _layout.Gap;
        return TaskbarMiniPopoverMath.Place(
            _anchorScreen,
            bodySize,
            tailSize,
            gap,
            Screen.FromRectangle(_anchorScreen).WorkingArea);
    }

    private void ApplyCurrentPlacement()
    {
        var placement = CurrentPlacement();
        _tailSide = placement.TailSide;
        _tailOffset = placement.TailOffset;
        ClientSize = placement.WindowSize;
        UpdateWindowRegion();
        if (!TaskbarPlacement.ShowAt(Handle, placement.Location, placement.WindowSize))
        {
            Location = placement.Location;
        }
        UpdateCountdownTimer();
        Cursor = Cursors.Default;
        Invalidate();
    }

    private Rectangle SpendCardBounds()
    {
        if (_layout.FooterSpendBounds.IsEmpty) return Rectangle.Empty;
        var body = RadarPopoverRenderer.BodyBounds(_layout, _tailSide);
        return new Rectangle(
            body.Left + _layout.FooterSpendBounds.Left,
            body.Top + _layout.FooterSpendBounds.Top,
            _layout.FooterSpendBounds.Width,
            _layout.FooterSpendBounds.Height);
    }

    private int HistoryDayIndexAt(Point location, CodexSpendHistoryLayout layout)
    {
        for (var index = 0; index < layout.BarBounds.Count; index++)
        {
            if (layout.InWindow(layout.BarBounds[index], _tailSide).Contains(location)) return index;
        }
        return -1;
    }

    private static bool HasSpendHistory(CodexTokenUsageSummary? tokenUsage) =>
        tokenUsage?.SpendHistory?.Days.Any(day => day.Spend.HasUsage) == true;

    private void ResetHistoryView()
    {
        _historyVisible = false;
        _spendCardHovered = false;
        _backHovered = false;
        _selectedHistoryDayIndex = -1;
        Cursor = Cursors.Default;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _countdownTimer.Stop();
            _countdownTimer.Dispose();
            _motionTimer.Stop();
            _motionTimer.Dispose();
            _renderer.Dispose();
        }
        base.Dispose(disposing);
    }
}
