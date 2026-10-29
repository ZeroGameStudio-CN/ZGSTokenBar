using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed partial class RadarPopoverRenderer
{
    private static void DrawConversationTools(Graphics graphics, RadarPopoverLayout layout, Point origin,
        CodexConversationView view, NativeText text, RadarPopoverFonts fonts)
    {
        var muted = Color.FromArgb(148, 163, 184);
        var foreground = Color.FromArgb(226, 232, 240);
        void Button(Rectangle local, string caption, bool active)
        {
            var bounds = Offset(local, origin);
            using var shape = RoundedRectangle(bounds, Scale(layout.Dpi, 5));
            using var fill = new SolidBrush(active ? Color.FromArgb(30, 139, 187, 255) : Color.FromArgb(16, 148, 163, 184));
            using var line = new Pen(Color.FromArgb(active ? 85 : 30, 148, 163, 184));
            graphics.FillPath(fill, shape); graphics.DrawPath(line, shape);
            DrawText(graphics, caption, fonts.Meta, active ? foreground : muted,
                Rectangle.Inflate(bounds, -Scale(layout.Dpi, 7), 0), CellTextFlags);
        }
        Button(layout.ConversationRangeBounds, text.CodexHistoryPeriod(view.Query.Period) + " ▾", view.Query.Period != CodexHistoryPeriod.Retained);
        Button(layout.ConversationModelBounds, (view.Query.Model ?? text.CodexAllModels) + " ▾", view.Query.Model is not null);
        Button(layout.ConversationProjectBounds, (view.Query.Project ?? text.CodexAllProjects) + " ▾", view.Query.Project is not null);
        Button(layout.ConversationSearchBounds, view.Query.Search.Length == 0 ? text.CodexHistorySearch : view.Query.Search, view.Query.Search.Length > 0);
        Button(layout.ConversationExportBounds, "CSV", false);

        var summary = view.Summary;
        (string Title, string Value, string Detail)[] cards =
        [
            (text.CodexSelectedTurns, summary.Count.ToString("N0"), text.CodexTerminalCount(summary.TerminalCount)),
            (text.CodexSelectedOutput, summary.OutputCoverage == 0 ? "—" : NativeText.FormatTokenCount(summary.OutputTokens)
                + (summary.PartialOutput ? "+" : ""), text.CodexMeasuredCount(summary.OutputCoverage)),
            (text.CodexSelectedCost, text.CodexHistoryCost(summary.Cost, summary.PartialCost), text.CodexEstimateOnly),
            (text.CodexWholeTurnAverage, summary.AverageTokensPerSecond is { } rate ? $"{rate:0.#} tok/s" : "—", text.CodexSpeedSampleCount(summary.SpeedSamples)),
        ];
        var area = Offset(layout.ConversationSummaryBounds, origin);
        var gap = Scale(layout.Dpi, 7);
        var width = (area.Width - gap * 3) / 4;
        for (var index = 0; index < cards.Length; index++)
        {
            var bounds = new Rectangle(area.Left + index * (width + gap), area.Top,
                index == 3 ? area.Right - (area.Left + index * (width + gap)) : width, area.Height);
            using var shape = RoundedRectangle(bounds, Scale(layout.Dpi, 6));
            using var fill = new SolidBrush(Color.FromArgb(10, 148, 163, 184));
            graphics.FillPath(fill, shape);
            var inset = Scale(layout.Dpi, 9);
            var inner = Rectangle.Inflate(bounds, -inset, 0);
            DrawText(graphics, cards[index].Title, fonts.Meta, muted, inner with { Y = area.Top + Scale(layout.Dpi, 4), Height = Scale(layout.Dpi, 13) }, CellTextFlags);
            DrawText(graphics, cards[index].Value, fonts.Model, foreground, inner with { Y = area.Top + Scale(layout.Dpi, 18), Height = Scale(layout.Dpi, 18) }, CellTextFlags);
            DrawText(graphics, cards[index].Detail, fonts.Meta, muted, inner with { Y = area.Top + Scale(layout.Dpi, 37), Height = Scale(layout.Dpi, 13) }, CellTextFlags);
        }
    }
}
