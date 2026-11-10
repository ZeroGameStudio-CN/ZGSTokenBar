using System.Globalization;
using System.Text;

namespace ZGSTokenBar.Core;

public static class GlassDockSvg
{
    public static string Pill(GlassDockPlan plan, bool light, double? tokensPerSecond, bool routerWarning, IReadOnlyList<double>? bars = null)
    {
        bars ??= [18, 46, 13];
        var svg = new StringBuilder();
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"640\" height=\"120\" viewBox=\"0 0 640 120\">");
        svg.Append(light
            ? "<defs><linearGradient id=\"wall\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"0\"><stop offset=\"0\" stop-color=\"#ffd0c2\"/><stop offset=\"1\" stop-color=\"#b7f3ff\"/></linearGradient></defs><rect width=\"640\" height=\"120\" fill=\"url(#wall)\"/>"
            : "<defs><linearGradient id=\"wall\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"0\"><stop offset=\"0\" stop-color=\"#ff6a4a\"/><stop offset=\".55\" stop-color=\"#7a4dff\"/><stop offset=\"1\" stop-color=\"#1ec8e8\"/></linearGradient></defs><rect width=\"640\" height=\"120\" fill=\"url(#wall)\"/>");
        svg.Append("<rect y=\"76\" width=\"640\" height=\"44\" fill=\"").Append(light ? "rgba(255,255,255,.72)" : "rgba(18,28,36,.55)").Append("\"/>");
        var x = 24;
        var y = 76;
        Glass(svg, x, y, GlassDockLayout.Width, GlassDockLayout.Height, GlassDockLayout.Corner, light);
        foreach (var slot in plan.Slots)
        {
            if (slot.Chip)
            {
                Chip(svg, x + slot.Bounds.X, y + slot.Bounds.Y, slot.Bounds.Width, slot.Bounds.Height, "+" + plan.OverflowCount.ToString(CultureInfo.InvariantCulture), plan.OverflowUrgent, light);
                continue;
            }

            var index = plan.Slots.TakeWhile(item => !item.Bounds.Equals(slot.Bounds)).Count();
            if (index >= plan.Visible.Count) continue;
            var ring = plan.Visible[index];
            var ringY = y + (GlassDockLayout.Height - GlassDockLayout.RingSize) / 2;
            Ring(svg, x + slot.Bounds.X, ringY, GlassDockLayout.RingSize, ring, light);
            var textX = x + slot.Bounds.X + GlassDockLayout.RingSize + 3;
            svg.Append("<text x=\"").Append(textX).Append("\" y=\"").Append(y + 21).Append("\" fill=\"").Append(light ? "#1c2430" : "#ffffff")
                .Append("\" font-family=\"Segoe UI, sans-serif\" font-size=\"12\" font-weight=\"700\">")
                .Append(Escape(ring.BalanceText ?? GlassDockLayout.PercentText(ring.RemainingPercent))).Append("</text>");
            svg.Append("<text x=\"").Append(textX).Append("\" y=\"").Append(y + 32).Append("\" fill=\"").Append(light ? "rgba(28,36,48,.6)" : "rgba(255,255,255,.6)")
                .Append("\" font-family=\"Segoe UI, sans-serif\" font-size=\"8.5\">")
                .Append(Escape(ring.AccountNumber > 0 ? "#" + ring.AccountNumber.ToString(CultureInfo.InvariantCulture) : ring.Title)).Append("</text>");
        }

        var dividerX = x + GlassDockLayout.SystemLeft - 6;
        svg.Append("<line x1=\"").Append(dividerX).Append("\" y1=\"").Append(y + 12).Append("\" x2=\"").Append(dividerX).Append("\" y2=\"").Append(y + 32).Append("\" stroke=\"").Append(light ? "rgba(20,24,32,.18)" : "rgba(255,255,255,.22)").Append("\" stroke-width=\"1\"/>");
        Router(svg, x + GlassDockLayout.RouterBounds.X, y + GlassDockLayout.RouterBounds.Y, routerWarning);
        Bars(svg, x + GlassDockLayout.BarsBounds.X, y + GlassDockLayout.BarsBounds.Y, bars);
        var rate = tokensPerSecond is { } value ? value.ToString("0", CultureInfo.InvariantCulture) : "—";
        var rateX = x + GlassDockLayout.RateBounds.X;
        var rateY = y + 18;
        svg.Append("<text x=\"").Append(rateX).Append("\" y=\"").Append(rateY).Append("\" fill=\"").Append(light ? "#1c2430" : "rgba(255,255,255,.92)").Append("\" font-family=\"Segoe UI, sans-serif\" font-size=\"13\" font-weight=\"650\">").Append(rate).Append("</text>");
        svg.Append("<text x=\"").Append(rateX).Append("\" y=\"").Append(rateY + 11).Append("\" fill=\"").Append(light ? "rgba(28,36,48,.55)" : "rgba(255,255,255,.55)").Append("\" font-family=\"Segoe UI, sans-serif\" font-size=\"8\">tok/s</text>");
        Spark(svg, x + GlassDockLayout.SparkBounds.X, y + GlassDockLayout.SparkBounds.Y);
        svg.Append("</svg>");
        return svg.ToString();
    }

    public static string Flyout(IReadOnlyList<GlassDockProviderGroup> groups, bool light, UsageLedger? ledger)
    {
        var svg = new StringBuilder();
        var height = 150 + groups.Sum(group => 36 + Math.Max(1, group.Accounts.Count) * 18) + (ledger is null ? 0 : 48);
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"360\" height=\"").Append(height + 40).Append("\" viewBox=\"0 0 360 ").Append(height + 40).Append("\">");
        svg.Append("<rect width=\"360\" height=\"").Append(height + 40).Append("\" fill=\"#6a4bff\"/>");
        Glass(svg, 16, 12, GlassDockLayout.FlyoutWidth, height, 16, light);
        svg.Append("<text x=\"32\" y=\"36\" fill=\"white\" font-family=\"Segoe UI, sans-serif\" font-size=\"13\" font-weight=\"650\">用量总览</text>");
        var y = 52;
        foreach (var group in groups)
        {
            var block = 28 + Math.Max(1, group.Accounts.Count) * 18;
            svg.Append("<rect x=\"28\" y=\"").Append(y).Append("\" width=\"292\" height=\"").Append(block).Append("\" rx=\"12\" fill=\"rgba(255,255,255,.06)\"/>");
            svg.Append("<text x=\"56\" y=\"").Append(y + 18).Append("\" fill=\"rgba(255,255,255,.78)\" font-family=\"Segoe UI, sans-serif\" font-size=\"10\" font-weight=\"700\">")
                .Append(Escape(group.Title.ToUpperInvariant())).Append("</text>");
            var row = y + 28;
            foreach (var account in group.Accounts)
            {
                var color = GlassDockLayout.BandColor(account.RemainingPercent);
                var label = account.BalanceText ?? UsageFormatting.Percent(account.RemainingPercent);
                svg.Append("<text x=\"40\" y=\"").Append(row + 11).Append("\" fill=\"rgba(255,255,255,.7)\" font-family=\"Segoe UI, sans-serif\" font-size=\"10\">")
                    .Append(Escape(account.Label)).Append("</text>");
                svg.Append("<text x=\"250\" y=\"").Append(row + 11).Append("\" fill=\"rgb(").Append(color.R).Append(',').Append(color.G).Append(',').Append(color.B).Append(")\" font-family=\"Segoe UI, sans-serif\" font-size=\"11\" font-weight=\"650\">")
                    .Append(Escape(label)).Append("</text>");
                row += 18;
            }

            y += block + 6;
        }

        svg.Append("<text x=\"32\" y=\"").Append(y + 16).Append("\" fill=\"rgba(255,255,255,.62)\" font-family=\"Segoe UI, sans-serif\" font-size=\"10\">系统</text>");
        svg.Append("<text x=\"32\" y=\"").Append(y + 36).Append("\" fill=\"rgba(255,255,255,.88)\" font-family=\"Segoe UI, sans-serif\" font-size=\"11\">CPU 18%   RAM 46%   GPU 13%</text>");
        svg.Append("<text x=\"32\" y=\"").Append(y + 56).Append("\" fill=\"rgba(255,255,255,.88)\" font-family=\"Segoe UI, sans-serif\" font-size=\"11\">~85 tok/s</text>");
        svg.Append("<text x=\"180\" y=\"").Append(y + 56).Append("\" fill=\"#F5C14A\" font-family=\"Segoe UI, sans-serif\" font-size=\"11\">Router 警告</text>");
        y += 64;
        if (ledger is not null)
        {
            svg.Append("<text x=\"32\" y=\"").Append(y + 16).Append("\" fill=\"rgba(255,255,255,.8)\" font-family=\"Segoe UI, sans-serif\" font-size=\"11\">今日 ")
                .Append(UsageFormatting.Tokens(ledger.Today.Tokens)).Append(" · 7天 ")
                .Append(UsageFormatting.Tokens(ledger.SevenDays.Tokens)).Append(" · 30天 ")
                .Append(UsageFormatting.Tokens(ledger.ThirtyDays.Tokens)).Append("</text>");
        }

        svg.Append("</svg>");
        return svg.ToString();
    }

    private static void Glass(StringBuilder svg, int x, int y, int width, int height, int radius, bool light)
    {
        var fill = light ? "rgba(255,255,255,.62)" : "rgba(22,18,28,.58)";
        var stroke = light ? "rgba(255,255,255,.8)" : "rgba(255,255,255,.26)";
        svg.Append("<rect x=\"").Append(x).Append("\" y=\"").Append(y).Append("\" width=\"").Append(width).Append("\" height=\"").Append(height)
            .Append("\" rx=\"").Append(radius).Append("\" fill=\"").Append(fill).Append("\" stroke=\"").Append(stroke).Append("\"/>");
        svg.Append("<rect x=\"").Append(x + 10).Append("\" y=\"").Append(y + 1).Append("\" width=\"").Append(width - 20).Append("\" height=\"1\" fill=\"rgba(255,255,255,.55)\"/>");
    }

    private static void Ring(StringBuilder svg, int x, int y, int size, GlassDockRing ring, bool light)
    {
        var color = GlassDockLayout.BandColor(ring.RemainingPercent);
        var c = size / 2d;
        var r = c - 2.2;
        var circumference = 2 * Math.PI * r;
        var remaining = ring.RemainingPercent is { } value ? Math.Clamp(value, 0, 100) / 100d : 0;
        var dash = (circumference * remaining).ToString("0.###", CultureInfo.InvariantCulture);
        var gap = (circumference * (1 - remaining)).ToString("0.###", CultureInfo.InvariantCulture);
        var cx = (x + c).ToString("0.###", CultureInfo.InvariantCulture);
        var cy = (y + c).ToString("0.###", CultureInfo.InvariantCulture);
        svg.Append("<circle cx=\"").Append(cx).Append("\" cy=\"").Append(cy).Append("\" r=\"").Append(r.ToString("0.###", CultureInfo.InvariantCulture))
            .Append("\" fill=\"none\" stroke=\"").Append(light ? "rgba(20,24,32,.12)" : "rgba(255,255,255,.16)").Append("\" stroke-width=\"2.4\"/>");
        if (remaining > 0)
        {
            svg.Append("<circle cx=\"").Append(cx).Append("\" cy=\"").Append(cy).Append("\" r=\"").Append(r.ToString("0.###", CultureInfo.InvariantCulture))
                .Append("\" fill=\"none\" stroke=\"rgb(").Append(color.R).Append(',').Append(color.G).Append(',').Append(color.B)
                .Append(")\" stroke-width=\"2.4\" stroke-linecap=\"round\" stroke-dasharray=\"").Append(dash).Append(' ').Append(gap)
                .Append("\" transform=\"rotate(-90 ").Append(cx).Append(' ').Append(cy).Append(")\"/>");
        }

        svg.Append("<circle cx=\"").Append(cx).Append("\" cy=\"").Append(cy).Append("\" r=\"").Append((c - 5).ToString("0.###", CultureInfo.InvariantCulture))
            .Append("\" fill=\"").Append(light ? "#ffffff" : "#16161c").Append("\"/>");
        var letter = string.IsNullOrWhiteSpace(ring.Title) ? "?" : ring.Title[..1].ToUpperInvariant();
        svg.Append("<text x=\"").Append(cx).Append("\" y=\"").Append((y + c + 3.2).ToString("0.###", CultureInfo.InvariantCulture))
            .Append("\" text-anchor=\"middle\" fill=\"").Append(light ? "#1c2430" : "#ffffff").Append("\" font-family=\"Segoe UI, sans-serif\" font-size=\"10\" font-weight=\"700\">")
            .Append(Escape(letter)).Append("</text>");
        if (ring.AccountNumber > 0)
        {
            svg.Append("<circle cx=\"").Append(x + size - 2).Append("\" cy=\"").Append(y + size - 2).Append("\" r=\"5.5\" fill=\"rgb(")
                .Append(color.R).Append(',').Append(color.G).Append(',').Append(color.B).Append(")\"/>");
            svg.Append("<text x=\"").Append(x + size - 2).Append("\" y=\"").Append(y + size + 1).Append("\" text-anchor=\"middle\" fill=\"white\" font-family=\"Segoe UI, sans-serif\" font-size=\"7\" font-weight=\"700\">")
                .Append(ring.AccountNumber.ToString(CultureInfo.InvariantCulture)).Append("</text>");
        }
    }

    private static void Chip(StringBuilder svg, int x, int y, int width, int height, string text, bool urgent, bool light)
    {
        svg.Append("<rect x=\"").Append(x).Append("\" y=\"").Append(y).Append("\" width=\"").Append(width).Append("\" height=\"").Append(height)
            .Append("\" rx=\"9\" fill=\"").Append(light ? "rgba(20,24,32,.08)" : "rgba(255,255,255,.1)").Append("\"/>");
        svg.Append("<text x=\"").Append(x + width / 2).Append("\" y=\"").Append(y + 12).Append("\" text-anchor=\"middle\" fill=\"")
            .Append(light ? "#1c2430" : "rgba(255,255,255,.86)").Append("\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" font-weight=\"700\">")
            .Append(Escape(text)).Append("</text>");
        if (urgent)
        {
            svg.Append("<circle cx=\"").Append(x + width - 1).Append("\" cy=\"").Append(y + 1).Append("\" r=\"3\" fill=\"#FFB020\"/>");
        }
    }

    private static void Router(StringBuilder svg, int x, int y, bool warning)
    {
        if (warning)
        {
            svg.Append("<path d=\"M").Append(x + 9).Append(' ').Append(y + 2).Append(" L").Append(x + 17).Append(' ').Append(y + 16).Append(" L").Append(x + 1).Append(' ').Append(y + 16).Append(" Z\" fill=\"none\" stroke=\"#F5C14A\" stroke-width=\"1.6\"/>");
            return;
        }

        svg.Append("<circle cx=\"").Append(x + 9).Append("\" cy=\"").Append(y + 9).Append("\" r=\"7\" fill=\"none\" stroke=\"#46E59A\" stroke-width=\"1.6\"/>");
    }

    private static void Bars(StringBuilder svg, int x, int y, IReadOnlyList<double> bars)
    {
        for (var index = 0; index < 3; index++)
        {
            var value = index < bars.Count ? Math.Clamp(bars[index], 0, 100) : 0;
            var bx = x + index * (GlassDockLayout.BarWidth + GlassDockLayout.BarGap);
            svg.Append("<rect x=\"").Append(bx).Append("\" y=\"").Append(y).Append("\" width=\"").Append(GlassDockLayout.BarWidth).Append("\" height=\"").Append(GlassDockLayout.BarHeight).Append("\" rx=\"1.5\" fill=\"rgba(255,255,255,.16)\"/>");
            var h = GlassDockLayout.BarHeight * value / 100d;
            var color = value > 80 ? "255,90,90" : value > 50 ? "245,193,74" : "70,229,154";
            svg.Append("<rect x=\"").Append(bx).Append("\" y=\"").Append((y + GlassDockLayout.BarHeight - h).ToString("0.###", CultureInfo.InvariantCulture))
                .Append("\" width=\"").Append(GlassDockLayout.BarWidth).Append("\" height=\"").Append(h.ToString("0.###", CultureInfo.InvariantCulture))
                .Append("\" rx=\"1.5\" fill=\"rgb(").Append(color).Append(")\"/>");
        }
    }

    private static void Spark(StringBuilder svg, int x, int y)
    {
        svg.Append("<polyline fill=\"none\" stroke=\"#46E59A\" stroke-width=\"1.4\" points=\"");
        double[] sample = [4, 6, 5, 8, 7, 11, 9, 12];
        for (var index = 0; index < sample.Length; index++)
        {
            var px = x + index * (GlassDockLayout.SparkWidth / (sample.Length - 1d));
            var py = y + GlassDockLayout.SparkHeight - sample[index];
            svg.Append(px.ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append(py.ToString("0.#", CultureInfo.InvariantCulture)).Append(' ');
        }

        svg.Append("\"/>");
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
