using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>Rendering of the health dot, refresh/settings controls and module handles/grips.</summary>
internal sealed partial class BarForm
{
    private void DrawHealth(Graphics graphics)
    {
        var cardsWidth = _visibleCards.Length * BarLayoutMath.CardWidth
            + Math.Max(0, _visibleCards.Length - 1) * BarLayoutMath.CardGap;
        if (_hiddenCardCount > 0) cardsWidth += BarLayoutMath.CardGap + BarLayoutMath.OverflowWidth;
        var x = BarLayoutMath.OuterPadding + BarLayoutMath.LabelWidth + BarLayoutMath.SectionGap
            + cardsWidth + BarLayoutMath.SectionGap;
        var health = _snapshot.Health.Count > 0
            ? _snapshot.Health
            : [new ProviderHealth(
                ProviderKind.Claude,
                false,
                _text.QuotaWaiting,
                ProviderHealthCode.Waiting)];
        foreach (var status in health)
        {
            using var brush = new SolidBrush(status.Connected
                ? Color.FromArgb(52, 211, 153)
                : Color.FromArgb(100, 116, 139));
            graphics.FillEllipse(brush, x, 18, BarLayoutMath.HealthDotWidth, BarLayoutMath.HealthDotWidth);
            x += BarLayoutMath.HealthDotWidth + BarLayoutMath.HealthDotGap;
        }
    }

    private void DrawControls(Graphics graphics, float logicalWidth)
    {
        var x = logicalWidth - BarLayoutMath.OuterPadding - BarLayoutMath.ControlsWidth;
        _taskbarAreaBounds.Clear();
        _refreshBounds = new RectangleF(x + 1, 10, 22, 22);
        _settingsBounds = new RectangleF(x + 25, 10, 22, 22);
        DrawControlButton(graphics, _refreshBounds, HoverTarget.Refresh);
        DrawControlButton(graphics, _settingsBounds, HoverTarget.Settings);
        DrawRefreshIcon(
            graphics,
            PressedIconBounds(_refreshBounds, HoverTarget.Refresh),
            _refreshing,
            _refreshRotation,
            HoverProgress(HoverTarget.Refresh));
        DrawSettingsIcon(
            graphics,
            PressedIconBounds(_settingsBounds, HoverTarget.Settings),
            HoverProgress(HoverTarget.Settings));
    }

    private void DrawControlButton(Graphics graphics, RectangleF bounds, HoverTarget target)
    {
        using var path = RoundedRectangle(bounds, 5);
        var (fillColor, borderEmphasis) = ControlVisual(target);
        using var fill = new SolidBrush(fillColor);
        using var border = new Pen(MixColor(
            Color.FromArgb(42, 148, 163, 184),
            Color.FromArgb(130, 129, 140, 248),
            borderEmphasis));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
    }

    private void DrawTaskbarControlGroup(Graphics graphics, bool drawShell = true)
    {
        var groupBounds = RectangleF.FromLTRB(
            _refreshBounds.Left,
            _refreshBounds.Top,
            _settingsBounds.Right,
            _settingsBounds.Bottom);
        using var path = RoundedRectangle(groupBounds, 6);
        var state = graphics.Save();
        graphics.SetClip(path);
        DrawTaskbarControlSegment(graphics, _refreshBounds, HoverTarget.Refresh);
        DrawTaskbarControlSegment(graphics, _settingsBounds, HoverTarget.Settings);
        graphics.Restore(state);

        using var separator = new Pen(Color.FromArgb(42, 100, 116, 139));
        if (drawShell)
        {
            using var border = new Pen(Color.FromArgb(54, 100, 116, 139));
            graphics.DrawPath(border, path);
        }
        graphics.DrawLine(separator, groupBounds.Left + 5, _settingsBounds.Top, groupBounds.Right - 5, _settingsBounds.Top);
    }

    private void DrawTaskbarControlSegment(Graphics graphics, RectangleF bounds, HoverTarget target)
    {
        var (fillColor, _) = ControlVisual(target);
        using var fill = new SolidBrush(fillColor);
        graphics.FillRectangle(fill, bounds);
    }

    private (Color Fill, float BorderEmphasis) ControlVisual(HoverTarget target)
    {
        var hover = HoverProgress(target);
        var pressed = _pressedTarget == target;
        var refreshPulse = target == HoverTarget.Refresh && _refreshing
            ? .35f + .18f * (float)Math.Sin(_refreshRotation * Math.PI / 180)
            : 0;
        var emphasis = Math.Max(hover, refreshPulse);
        var fillColor = MixColor(
            _backgroundTheme.QuotaGroup,
            Color.FromArgb(226, 232, 240),
            emphasis * .08f);
        if (pressed) fillColor = MixColor(fillColor, Color.FromArgb(49, 46, 129), .55f);
        return (fillColor, Math.Max(emphasis * .75f, pressed ? 1 : 0));
    }

    private void DrawRefreshIcon(
        Graphics graphics,
        RectangleF bounds,
        bool refreshing,
        float rotation,
        float hover,
        float iconSize = ControlIconSize)
    {
        var color = refreshing
            ? Color.FromArgb(129, 140, 248)
            : MixColor(Color.FromArgb(148, 163, 184), Color.FromArgb(226, 232, 240), hover);
        var iconBounds = CenteredSquare(bounds, iconSize);
        var center = new PointF(
            iconBounds.Left + iconBounds.Width / 2,
            iconBounds.Top + iconBounds.Height / 2);
        var state = graphics.Save();
        graphics.TranslateTransform(center.X, center.Y);
        graphics.RotateTransform(rotation);

        using var pen = new Pen(color, ControlIconStrokeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        var radius = iconSize * 4.8f / ControlIconSize;
        const float startAngle = 25f;
        const float sweepAngle = 290f;
        graphics.DrawArc(pen, -radius, -radius, radius * 2, radius * 2, startAngle, sweepAngle);

        var endAngle = (startAngle + sweepAngle) * Math.PI / 180;
        var tip = new PointF(
            radius * (float)Math.Cos(endAngle),
            radius * (float)Math.Sin(endAngle));
        var tangent = new PointF(
            -(float)Math.Sin(endAngle),
            (float)Math.Cos(endAngle));
        var normal = new PointF(-tangent.Y, tangent.X);
        var arrowLength = iconSize * 2f / ControlIconSize;
        var arrowHalfWidth = iconSize * 1.2f / ControlIconSize;
        var arrowBase = new PointF(
            tip.X - tangent.X * arrowLength,
            tip.Y - tangent.Y * arrowLength);
        graphics.DrawLines(pen,
        [
            new PointF(
                arrowBase.X + normal.X * arrowHalfWidth,
                arrowBase.Y + normal.Y * arrowHalfWidth),
            tip,
            new PointF(
                arrowBase.X - normal.X * arrowHalfWidth,
                arrowBase.Y - normal.Y * arrowHalfWidth),
        ]);
        graphics.Restore(state);
    }

    private void DrawSettingsIcon(
        Graphics graphics,
        RectangleF bounds,
        float hover,
        bool taskbarCompact = false)
    {
        var color = MixColor(
            Color.FromArgb(148, 163, 184),
            Color.FromArgb(226, 232, 240),
            hover);
        DrawSystemIcon(
            graphics,
            bounds,
            SettingsIconGlyph,
            color,
            taskbarCompact ? _taskbarControlIconFont : _systemIconFont);
    }

    private void DrawTaskbarCollapseHandle(Graphics graphics, MiniAreaTarget target)
    {
        var hover = string.Equals(_hoverMiniAreaId, target.AreaId, StringComparison.Ordinal)
            ? HoverProgress(HoverTarget.MiniCollapse)
            : 0;
        var bounds = target.HandleBounds;
        if (string.Equals(_pressedMiniAreaId, target.AreaId, StringComparison.Ordinal)) bounds.Offset(0, .7f);
        if (hover > 0)
        {
            using var hoverBrush = new SolidBrush(Color.FromArgb(
                (int)Math.Round(28 * hover),
                148,
                163,
                184));
            graphics.FillRoundedRectangle(hoverBrush, bounds, 4);
        }
        var color = MixColor(
            Color.FromArgb(118, 148, 163, 184),
            Color.FromArgb(226, 232, 240),
            hover);
        using var separator = new Pen(Color.FromArgb(
            (int)Math.Round(42 + hover * 46),
            100,
            116,
            139), 1)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        var separatorX = bounds.Left + 1.2f;
        var separatorBottom = target.Reorderable
            ? target.ReorderBounds.Bottom - 4
            : bounds.Bottom - 4;
        graphics.DrawLine(separator, separatorX, bounds.Top + 4, separatorX, separatorBottom);
        using var pen = new Pen(color, 1.5f + hover * .55f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        var x = bounds.X + bounds.Width / 2;
        var centerY = bounds.Top + bounds.Height / 2;
        var direction = target.Collapsed ? 1f : -1f;
        var tipX = x + direction * 2.2f;
        graphics.DrawLines(pen,
        [
            new PointF(x, centerY - 2.6f),
            new PointF(tipX, centerY),
            new PointF(x, centerY + 2.6f),
        ]);
    }

    private void DrawTaskbarReorderGrip(Graphics graphics, MiniAreaTarget target)
    {
        if (!target.Reorderable) return;
        var hover = string.Equals(_hoverReorderAreaId, target.AreaId, StringComparison.Ordinal)
            ? HoverProgress(HoverTarget.MiniReorder)
            : 0;
        var active = string.Equals(_reorderingMiniAreaId, target.AreaId, StringComparison.Ordinal);
        var bounds = target.ReorderBounds;
        if (hover > 0 || active)
        {
            using var fill = new SolidBrush(Color.FromArgb(
                (int)Math.Round(active ? 36 : 26 * hover),
                96,
                165,
                250));
            graphics.FillRoundedRectangle(fill, bounds, 4);
        }
        else
        {
            using var fill = new SolidBrush(Color.FromArgb(10, 148, 163, 184));
            graphics.FillRoundedRectangle(fill, bounds, 4);
        }
        var color = active
            ? Color.FromArgb(147, 197, 253)
            : MixColor(Color.FromArgb(176, 148, 163, 184), Color.FromArgb(226, 232, 240), hover);
        using var brush = new SolidBrush(color);
        var diameter = active || hover > 0 ? 2.2f : 2f;
        var left = bounds.Left + (bounds.Width - diameter * 2 - 1.35f) / 2;
        var top = bounds.Top + (bounds.Height - diameter * 3 - 2.5f) / 2;
        for (var row = 0; row < 3; row++)
        {
            var y = top + row * (diameter + 1.25f);
            graphics.FillEllipse(brush, left, y, diameter, diameter);
            graphics.FillEllipse(brush, left + diameter + 1.35f, y, diameter, diameter);
        }
    }

    private void DrawMiniReorderInsertionMarker(Graphics graphics)
    {
        if (!_reorderMoved || _reorderingMiniAreaId is null) return;
        var targets = _taskbarAreaBounds
            .Where(target => target.Reorderable
                && !string.Equals(target.AreaId, _reorderingMiniAreaId, StringComparison.Ordinal))
            .OrderBy(target => target.Bounds.Left)
            .ToArray();
        if (targets.Length == 0) return;
        var before = _reorderBeforeAreaId is null
            ? null
            : targets.FirstOrDefault(target => string.Equals(
                target.AreaId,
                _reorderBeforeAreaId,
                StringComparison.Ordinal));
        var x = before is null
            ? targets[^1].Bounds.Right + TaskbarMiniLayoutMath.ModuleGap / 2f
            : before.Bounds.Left - TaskbarMiniLayoutMath.ModuleGap / 2f;
        using var pen = new Pen(Color.FromArgb(210, 96, 165, 250), 1.7f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        var markerTop = targets.Min(target => target.Bounds.Top) + 4;
        var markerBottom = targets.Max(target => target.Bounds.Bottom) - 4;
        graphics.DrawLine(pen, x, markerTop, x, markerBottom);
    }

    private void DrawTaskbarResizeGrip(Graphics graphics, MiniAreaTarget target)
    {
        if (target.Collapsed || target.ResizeBounds.IsEmpty) return;
        var active = string.Equals(_hoverResizeAreaId, target.AreaId, StringComparison.Ordinal)
            || string.Equals(_resizingMiniAreaId, target.AreaId, StringComparison.Ordinal);
        using var pen = new Pen(
            active
                ? Color.FromArgb(150, 148, 163, 184)
                : Color.FromArgb(34, 100, 116, 139),
            active ? 1.4f : 1f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        var x = target.ResizeBounds.Left + target.ResizeBounds.Width / 2;
        graphics.DrawLine(pen, x, target.Bounds.Top + 11, x, target.Bounds.Bottom - 11);
    }

    private void DrawSystemIcon(
        Graphics graphics,
        RectangleF bounds,
        string glyph,
        Color color,
        Font font,
        float rotation = 0)
    {
        var state = graphics.Save();
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var iconBounds = bounds;
        iconBounds.Offset(0, SystemIconVerticalOffset);
        if (rotation != 0)
        {
            graphics.TranslateTransform(iconBounds.Left + iconBounds.Width / 2, iconBounds.Top + iconBounds.Height / 2);
            graphics.RotateTransform(rotation);
            graphics.TranslateTransform(-(iconBounds.Left + iconBounds.Width / 2), -(iconBounds.Top + iconBounds.Height / 2));
        }
        using var brush = new SolidBrush(color);
        graphics.DrawString(glyph, font, brush, iconBounds, _systemIconFormat);
        graphics.Restore(state);
    }

    private static RectangleF CenteredSquare(RectangleF bounds, float size) => new(
        bounds.Left + (bounds.Width - size) / 2,
        bounds.Top + (bounds.Height - size) / 2,
        size,
        size);
}
