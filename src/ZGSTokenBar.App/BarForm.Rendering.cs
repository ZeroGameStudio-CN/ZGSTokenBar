using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>Rendering entry point: paint loop, per-module fault isolation, shared drawing and formatting helpers.</summary>
internal sealed partial class BarForm
{
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        graphics.ScaleTransform(_scale, _scale);

        var logicalWidth = ClientSize.Width / _scale;
        var logicalHeight = ClientSize.Height / _scale;
        if (_glassDock)
        {
            try
            {
                DrawGlassDock(graphics);
                _taskbarRenderFailureAreaIds.Remove("bar");
            }
            catch (Exception exception) when (IsRecoverableRenderException(exception))
            {
                TraceTaskbarRenderFailure("bar", exception);
            }

            return;
        }

        using var panelPath = RoundedRectangle(
            new RectangleF(0.5f, 0.5f, logicalWidth - 1, logicalHeight - 1),
            8);
        using var panelBrush = new SolidBrush(_backgroundTheme.Outer);
        using var panelBorder = new Pen(UiPalette.Slate400.WithAlpha(56), 1);
        graphics.FillPath(panelBrush, panelPath);
        graphics.DrawPath(panelBorder, panelPath);

        try
        {
            DrawTaskbarCards(graphics, logicalWidth);
            _taskbarRenderFailureAreaIds.Remove("bar");
        }
        catch (Exception exception) when (IsRecoverableRenderException(exception))
        {
            TraceTaskbarRenderFailure("bar", exception);
        }
    }

    private void DrawLabel(Graphics graphics)
    {
        using var primary = new SolidBrush(UiPalette.Slate50);
        using var muted = new SolidBrush(UiPalette.Slate400);
        DrawString(graphics, _text.Quota, _titleFont, primary, new RectangleF(6, 6, 78, 15), StringAlignment.Near);
        DrawString(
            graphics,
            _refreshing ? _text.Refreshing : _text.LiveLimits,
            _subtitleFont,
            muted,
            new RectangleF(6, 22, 78, 12),
            StringAlignment.Near);
    }

    private void DrawCards(Graphics graphics)
    {
        _cardBounds.Clear();
        var x = BarLayoutMath.OuterPadding + BarLayoutMath.LabelWidth + BarLayoutMath.SectionGap;
        foreach (var card in _visibleCards)
        {
            var bounds = new RectangleF(x, 4, BarLayoutMath.CardWidth, 34);
            DrawCard(graphics, bounds, card);
            _cardBounds.Add((bounds, card));
            x += BarLayoutMath.CardWidth + BarLayoutMath.CardGap;
        }

        if (_hiddenCardCount > 0)
        {
            var bounds = new RectangleF(x, 9, BarLayoutMath.OverflowWidth, 24);
            using var path = RoundedRectangle(bounds, 6);
            using var fill = new SolidBrush(_backgroundTheme.QuotaGroup);
            using var border = new Pen(UiPalette.Slate400.WithAlpha(42));
            using var text = new SolidBrush(UiPalette.Slate400);
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
            DrawString(graphics, $"+{_hiddenCardCount}", _overflowFont, text, bounds, StringAlignment.Center);
        }
    }

    private void DrawTaskbarCards(Graphics graphics, float logicalWidth)
    {
        _cardBounds.Clear();
        _taskbarWindowBounds.Clear();
        _taskbarRadarBounds.Clear();
        _taskbarPluginBounds.Clear();
        _taskbarCodexAccountBounds.Clear();
        _codexStrategyBounds = RectangleF.Empty;
        _taskbarAreaBounds.Clear();
        var x = (float)TaskbarMiniLayoutMath.OuterPadding;
        _liveMetricsBounds = RectangleF.Empty;
        foreach (var area in _visibleTaskbarAreas)
        {
            var layout = AreaLayout(area.AreaId);
            var areaWidth = TaskbarAreaWidth(area);
            var areaBounds = new RectangleF(x, 4, areaWidth, 36);
            var handleBounds = TaskbarCollapseHandleBounds(areaBounds);
            var bounds = TaskbarAreaContentBounds(areaBounds);
            var target = new MiniAreaTarget(
                area.AreaId,
                area.Title,
                areaBounds,
                handleBounds,
                TaskbarReorderHandleBounds(areaBounds),
                ResizeBounds(areaBounds, layout.Collapsed),
                layout.Collapsed,
                Reorderable: true);
            _taskbarAreaBounds.Add(target);
            DrawTaskbarAreaSafely(graphics, area, layout, target, bounds);
            x += areaWidth + TaskbarMiniLayoutMath.ModuleGap;
        }

        if (_hiddenCardCount > 0)
        {
            var overflowBounds = new RectangleF(x, 10, TaskbarMiniLayoutMath.OverflowWidth, 24);
            using var path = RoundedRectangle(overflowBounds, 6);
            using var fill = new SolidBrush(_backgroundTheme.QuotaGroup);
            using var border = new Pen(UiPalette.Slate400.WithAlpha(42));
            using var text = new SolidBrush(UiPalette.Slate400);
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
            DrawString(graphics, $"+{_hiddenCardCount}", _overflowFont, text, overflowBounds, StringAlignment.Center);
        }

        DrawMiniReorderInsertionMarker(graphics);
        var controlsX = logicalWidth
            - TaskbarMiniLayoutMath.OuterPadding
            - TaskbarMiniLayoutMath.ControlsWidth;
        _refreshBounds = new RectangleF(controlsX, 4, TaskbarControlWidth, TaskbarControlHeight);
        _settingsBounds = new RectangleF(
            controlsX,
            _refreshBounds.Bottom,
            TaskbarControlWidth,
            TaskbarControlHeight);
        DrawTaskbarControlGroup(graphics);
        DrawRefreshIcon(
            graphics,
            PressedIconBounds(_refreshBounds, HoverTarget.Refresh),
            _refreshing,
            _refreshRotation,
            HoverProgress(HoverTarget.Refresh),
            TaskbarControlIconSize);
        DrawSettingsIcon(
            graphics,
            PressedIconBounds(_settingsBounds, HoverTarget.Settings),
            HoverProgress(HoverTarget.Settings),
            taskbarCompact: true);
    }

    private void DrawTaskbarAreaSafely(
        Graphics graphics,
        TaskbarMiniAreaContent area,
        MiniAreaLayout layout,
        MiniAreaTarget target,
        RectangleF bounds)
    {
        var hitTargetCounts = CaptureTaskbarHitTargetCounts();
        var previousStrategyBounds = _codexStrategyBounds;
        var graphicsState = graphics.Save();
        var graphicsStateRestored = false;
        try
        {
            if (_renderFaultForAcceptance?.Invoke(area.AreaId) is { } fault) throw fault;
            DrawTaskbarModuleShell(graphics, target);
            if (area.Group is { } group)
            {
                var fullBounds = bounds;
                bounds = CodexQuotaBounds(area.AreaId, bounds, layout.Collapsed);
                var primaryCard = PrimaryTaskbarCard(group);
                if (layout.Collapsed)
                {
                    if (group.IsCodexPool)
                    {
                        DrawCollapsedCodexPool(graphics, bounds, group.Cards);
                    }
                    else if (primaryCard.Provider == ProviderKind.AiGateway)
                    {
                        DrawCollapsedAiGatewayCard(graphics, bounds, primaryCard);
                    }
                    else
                    {
                        DrawCollapsedQuotaCard(graphics, bounds, primaryCard);
                    }
                    _cardBounds.Add((bounds, primaryCard));
                }
                else if (group.IsCodexPool)
                {
                    DrawTaskbarCodexPool(graphics, bounds, group.Cards);
                    _cardBounds.Add((bounds, primaryCard));
                }
                else if (group.IsStackedCodex)
                {
                    DrawTaskbarCodexGroup(graphics, bounds, group.Cards);
                    for (var index = 0; index < group.Cards.Count; index++)
                    {
                        var rowBounds = TaskbarAccountRowBounds(bounds, index, group.Cards.Count);
                        _cardBounds.Add((rowBounds, group.Cards[index]));
                        _taskbarCodexAccountBounds.Add((
                            TaskbarAccountOrdinalRowBounds(bounds, index, group.Cards.Count),
                            group.Cards[index]));
                    }
                }
                else
                {
                    DrawTaskbarCard(graphics, bounds, group.Cards[0]);
                    _cardBounds.Add((bounds, group.Cards[0]));
                }
                if (bounds.Width < fullBounds.Width)
                    DrawCodexStrategyButton(graphics, RectangleF.FromLTRB(
                        bounds.Right, bounds.Top, fullBounds.Right, bounds.Bottom));
            }
            else if (area.Plugin is { } pluginCard)
            {
                DrawPluginMiniCard(graphics, bounds, pluginCard, layout.Collapsed);
                _taskbarPluginBounds.Add((bounds, pluginCard));
            }
            else if (string.Equals(area.AreaId, MiniAreaIds.RadarReset, StringComparison.Ordinal))
            {
                DrawRadarResetCard(graphics, bounds, layout.Collapsed);
            }
            else if (string.Equals(area.AreaId, MiniAreaIds.SystemMetrics, StringComparison.Ordinal))
            {
                _systemUsageBounds = bounds;
                DrawSystemUsageCard(graphics, bounds, layout.Collapsed);
            }
            else if (string.Equals(area.AreaId, MiniAreaIds.LiveMetrics, StringComparison.Ordinal))
            {
                _liveMetricsBounds = bounds;
                DrawLiveMetricsCard(graphics, bounds, layout.Collapsed);
            }
            DrawTaskbarCollapseHandle(graphics, target);
            DrawTaskbarReorderGrip(graphics, target);
            DrawTaskbarResizeGrip(graphics, target);
            _taskbarRenderFailureAreaIds.Remove(area.AreaId);
        }
        catch (Exception exception) when (IsRecoverableRenderException(exception))
        {
            graphicsStateRestored = true;
            graphics.Restore(graphicsState);
            RestoreTaskbarHitTargetCounts(hitTargetCounts);
            _codexStrategyBounds = previousStrategyBounds;
            if (string.Equals(area.AreaId, MiniAreaIds.SystemMetrics, StringComparison.Ordinal))
            {
                _systemUsageBounds = RectangleF.Empty;
            }
            TraceTaskbarRenderFailure(area.AreaId, exception);
            try
            {
                DrawTaskbarModuleFailure(graphics, target);
                DrawTaskbarCollapseHandle(graphics, target);
                DrawTaskbarReorderGrip(graphics, target);
                DrawTaskbarResizeGrip(graphics, target);
            }
            catch (Exception fallbackException) when (IsRecoverableRenderException(fallbackException))
            {
                TraceTaskbarRenderFailure($"{area.AreaId}:fallback", fallbackException);
            }
        }
        finally
        {
            if (!graphicsStateRestored) graphics.Restore(graphicsState);
        }
    }

    private void DrawTaskbarCardShell(Graphics graphics, RectangleF bounds)
    {
        using var path = RoundedRectangle(bounds, 6);
        using var fill = new SolidBrush(_backgroundTheme.ProviderGroup);
        using var border = new Pen(UiPalette.Slate700.WithAlpha(24), 1);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
    }

    private void DrawTaskbarModuleShell(Graphics graphics, MiniAreaTarget target)
    {
        using var path = RoundedRectangle(target.Bounds, 7);
        using var fill = new SolidBrush(MixColor(
            _backgroundTheme.Outer,
            _backgroundTheme.QuotaGroup,
            .72f));
        using var border = new Pen(UiPalette.Slate500.WithAlpha(76), 1);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
    }

    private void DrawTaskbarModuleFailure(Graphics graphics, MiniAreaTarget target)
    {
        using var path = RoundedRectangle(target.Bounds, 7);
        using var fill = new SolidBrush(MixColor(
            _backgroundTheme.Outer,
            Color.FromArgb(127, 29, 29),
            .28f));
        using var border = new Pen(UiPalette.Rose400.WithAlpha(128), 1);
        using var mark = new Pen(UiPalette.Rose400.WithAlpha(220), 1.5f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        var centerX = target.Bounds.Left + target.Bounds.Width / 2;
        var centerY = target.Bounds.Top + target.Bounds.Height / 2;
        graphics.DrawLine(mark, centerX - 3, centerY - 3, centerX + 3, centerY + 3);
        graphics.DrawLine(mark, centerX + 3, centerY - 3, centerX - 3, centerY + 3);
    }

    private TaskbarHitTargetCounts CaptureTaskbarHitTargetCounts() => new(
        _cardBounds.Count,
        _taskbarWindowBounds.Count,
        _taskbarRadarBounds.Count,
        _taskbarPluginBounds.Count,
        _taskbarCodexAccountBounds.Count);

    private void RestoreTaskbarHitTargetCounts(TaskbarHitTargetCounts counts)
    {
        TrimToCount(_cardBounds, counts.Cards);
        TrimToCount(_taskbarWindowBounds, counts.Windows);
        TrimToCount(_taskbarRadarBounds, counts.Radar);
        TrimToCount(_taskbarPluginBounds, counts.Plugins);
        TrimToCount(_taskbarCodexAccountBounds, counts.CodexAccounts);
    }

    private void TraceTaskbarRenderFailure(string areaId, Exception exception)
    {
        if (!_taskbarRenderFailureAreaIds.Add(areaId)) return;
        System.Diagnostics.Trace.TraceWarning(
            "Taskbar render fault isolated in {0}: {1}",
            areaId,
            exception.GetType().Name);
    }

    private static bool IsRecoverableRenderException(Exception exception) =>
        exception is not OutOfMemoryException
        and not AccessViolationException
        and not StackOverflowException;

    private static void TrimToCount<T>(List<T> items, int count)
    {
        if (items.Count > count) items.RemoveRange(count, items.Count - count);
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius) => UiPaint.RoundedPath(bounds, radius);

    private void DrawString(
        Graphics graphics,
        string text,
        Font font,
        Brush brush,
        RectangleF bounds,
        StringAlignment alignment) =>
        UiPaint.Text(graphics, text, font, brush, bounds, alignment);

    private static float MeasureWidth(Graphics graphics, string text, Font font) =>
        graphics.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic).Width;

    private static Color MixColor(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(from.A + (to.A - from.A) * amount),
            (int)Math.Round(from.R + (to.R - from.R) * amount),
            (int)Math.Round(from.G + (to.G - from.G) * amount),
            (int)Math.Round(from.B + (to.B - from.B) * amount));
    }

    private static string FormatPercent(double percent)
    {
        if (percent <= 0) return "0%";
        if (percent < 1) return "<1%";
        if (percent < 10) return $"{Math.Round(percent, 1):0.#}%";
        return $"{Math.Round(percent):0}%";
    }

    private static string CompactUsage(double? percent)
    {
        if (percent is null) return "--";
        if (percent < 1 && percent > 0) return "<1%";
        return $"{Math.Round(Math.Clamp(percent.Value, 0, 100)):0}%";
    }

    private static string CompactRemaining(double? percent)
    {
        if (percent is null) return "—";
        if (percent < 1 && percent > 0) return "<1%";
        return $"{Math.Round(Math.Clamp(percent.Value, 0, 100)):0}%";
    }

    private static Color UsageColor(double usage) => usage switch
    {
        >= 90 => UiPalette.Rose400,
        >= 70 => UiPalette.Amber400,
        _ => UiPalette.Emerald400,
    };

    /// <summary>Largest edge kept for embedded icons; the biggest on-screen use is ~64 device px.</summary>
    internal const int EmbeddedIconMaxPixels = 160;

    internal static Image LoadEmbeddedImage(string logicalName)
    {
        using var stream = typeof(BarForm).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Missing embedded image resource: {logicalName}");
        using var source = Image.FromStream(stream);
        // Official icons ship at 1024-1280 px (4-6.5 MB decoded each); keep a small copy only.
        var longest = Math.Max(source.Width, source.Height);
        if (longest <= EmbeddedIconMaxPixels) return new Bitmap(source);
        var scale = EmbeddedIconMaxPixels / (float)longest;
        var bitmap = new Bitmap(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        return bitmap;
    }

    private static string PluginMonogram(string title)
    {
        var characters = title
            .Where(char.IsLetterOrDigit)
            .Take(2)
            .ToArray();
        return characters.Length == 0 ? "P" : new string(characters).ToUpperInvariant();
    }

    private static Color PluginIconColor(PluginMiniCardView view)
    {
        var accent = view.Card.AccentToken;
        if (accent.Length == 7
            && accent[0] == '#'
            && int.TryParse(accent.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgb))
        {
            return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        }

        return view.Card.Kind switch
        {
            ContributionKind.Balance => Color.FromArgb(42, 157, 143),
            ContributionKind.Metric => Color.FromArgb(37, 99, 235),
            _ => Color.FromArgb(124, 58, 237),
        };
    }

    private static string PluginValue(ContributionValue value) =>
        value.Kind == "currency" && value.Decimal is { } currency
            ? value.Text switch
            {
                "CNY" => $"¥{currency:0.00}",
                "USD" => $"${currency:0.00}",
                { Length: > 0 } code => $"{code} {currency:0.00}",
                _ => currency.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            }
        : value.Kind == "percent" && value.Number is { } percent
            ? $"{percent:0.#}%"
        : value.Text
        ?? value.Decimal?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
        ?? value.Number?.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
        ?? value.Integer?.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
        ?? value.Boolean?.ToString()
        ?? value.Timestamp?.ToLocalTime().ToString(
            "HH:mm",
            System.Globalization.CultureInfo.CurrentCulture)
        ?? "—";

    internal static string CompactPluginValue(ContributionValue value) =>
        value.Kind == "currency"
            && value.Decimal is { } currency
            && string.Equals(value.Text, "CNY", StringComparison.OrdinalIgnoreCase)
            ? AiGatewayBalanceFormatting.CompactAmount(currency)
            : PluginValue(value);

    private static Color PluginCompactStatusColor(string? status) => status switch
    {
        "available" => UiPalette.Emerald400,
        "cached" or "stale" => UiPalette.Amber400,
        "unavailable" => UiPalette.Rose400,
        _ => UiPalette.Slate500,
    };

    private Image ProviderLogo(ProviderKind provider) => provider switch
    {
        ProviderKind.Claude => _claudeLogo,
        ProviderKind.AiGateway => _deepSeekLogo,
        _ => _openAiLogo,
    };

    private static Font CreateSystemIconFont(float size = 14f)
    {
        const string fluent = "Segoe Fluent Icons";
        var familyName = FontFamily.Families.Any(family =>
            string.Equals(family.Name, fluent, StringComparison.OrdinalIgnoreCase))
            ? fluent
            : "Segoe MDL2 Assets";
        return new Font(familyName, size, FontStyle.Regular, GraphicsUnit.Pixel);
    }
}
