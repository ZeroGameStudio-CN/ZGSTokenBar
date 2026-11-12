using System.Globalization;
using System.Text;

namespace ZGSTokenBar.Core;

public static class UsageBoardSvg
{
    public static string Render(UsageBoard board, UsageTheme theme, bool compact, bool chinese) =>
        Panel(board, theme, compact, chinese, taskbar: false);

    public static string Taskbar(UsageBoard board, UsageTheme theme, bool chinese)
    {
        var (name, remaining) = Tightest(board);
        var svg = new StringBuilder();
        svg.Append("""<svg xmlns="http://www.w3.org/2000/svg" width="560" height="92" viewBox="0 0 560 92">""");
        Defs(svg, theme);
        svg.Append(theme.Light
            ? """<rect width="560" height="92" fill="url(#wallpaper)"/>"""
            : """<rect width="560" height="92" fill="#07080c"/><rect width="560" height="92" fill="url(#wallpaper)" opacity="0.9"/>""");
        Blobs(svg, theme);
        svg.Append("""<rect x="0" y="58" width="560" height="34" fill="#05060a" opacity="0.72"/>""");
        Glass(svg, theme, 118, 14, 324, 52, 26);
        Ring(svg, 150, 40, 14, remaining ?? 0, "pill");
        Text(svg, 176, 36, name ?? "Usage", 15, theme.Text.Hex, true);
        Text(svg, 176, 52, chinese ? "任务栏胶囊" : "Taskbar pill", 10, theme.Muted.Hex, false);
        Text(svg, 300, 40, UsageFormatting.Percent(remaining), 16, remaining is null ? theme.Muted.Hex : UsageThemes.ForRemaining(remaining.Value).Hex, true);
        var x = 352;
        var index = 0;
        foreach (var provider in board.Providers.Take(3))
        {
            var value = ProviderRemaining(provider);
            Ring(svg, x, 40, 8, value ?? 0, "mini" + index);
            x += 22;
            index++;
        }

        svg.Append("</svg>");
        return svg.ToString();
    }

    public static string Before(bool chinese)
    {
        var body = chinese ? "额度不可读" : "Quota unavailable";
        return "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"285\" height=\"92\" viewBox=\"0 0 285 92\">"
            + "<rect width=\"285\" height=\"92\" rx=\"18\" fill=\"#1c212b\"/>"
            + "<text x=\"14\" y=\"28\" fill=\"#e6ebf4\" font-family=\"Segoe UI Variable, Segoe UI, sans-serif\" font-size=\"14\" font-weight=\"600\">Grok</text>"
            + "<text x=\"14\" y=\"56\" fill=\"#e6ebf4\" font-family=\"Segoe UI Variable, Segoe UI, sans-serif\" font-size=\"12\">"
            + body + "</text></svg>";
    }

    private static string Panel(UsageBoard board, UsageTheme theme, bool compact, bool chinese, bool taskbar)
    {
        _ = taskbar;
        var width = UsageBoardLayout.Width;
        var height = UsageBoardLayout.Height(board, compact);
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"""<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">""");
        Defs(svg, theme);
        svg.Append("""<rect width="100%" height="100%" fill="url(#wallpaper)"/>""");
        Blobs(svg, theme);
        Glass(svg, theme, 0, 0, width, height, UsageBoardLayout.Corner);
        Text(svg, 24, 40, chinese ? "用量" : "Usage", 24, theme.Text.Hex, true);
        Text(svg, 24, 62, UsageFormatting.Overall(board, chinese), 12, theme.Muted.Hex, false);
        Chip(svg, width - 168, 24, 46, chinese ? "刷新" : "Refresh", theme);
        Chip(svg, width - 116, 24, 46, theme.Light ? (chinese ? "深色" : "Dark") : (chinese ? "浅色" : "Light"), theme);
        Chip(svg, width - 64, 24, 40, chinese ? "设置" : "Set", theme);
        var y = UsageBoardLayout.Padding + UsageBoardLayout.HeaderHeight;
        if (board.Providers.Count == 0)
        {
            Text(svg, 28, y + 28, chinese ? "没有可读的登录" : "No readable sign-in", 14, theme.Muted.Hex, false);
        }

        var cardIndex = 0;
        foreach (var provider in board.Providers)
        {
            var cardHeight = UsageBoardLayout.CardHeight(provider, compact);
            DrawProvider(svg, theme, provider, 16, y, width - 32, cardHeight, compact, chinese, cardIndex);
            y += cardHeight + UsageBoardLayout.CardGap;
            cardIndex++;
        }

        svg.Append("</svg>");
        return svg.ToString();
    }

    private static void DrawProvider(
        StringBuilder svg,
        UsageTheme theme,
        UsageProviderSnapshot provider,
        int x,
        int y,
        int width,
        int height,
        bool compact,
        bool chinese,
        int index)
    {
        Glass(svg, theme, x, y, width, height, 22);
        var accent = UsageColor.Parse(provider.Accent);
        svg.Append(CultureInfo.InvariantCulture, $"""<circle cx="{x + 28}" cy="{y + 30}" r="12" fill="{accent.Rgba(0.9)}"/>""");
        Text(svg, x + 22, y + 34, Monogram(provider.DisplayName), 11, "#FFFFFF", true);
        Text(svg, x + 48, y + 28, provider.DisplayName, 15, theme.Text.Hex, true);
        Text(svg, x + 48, y + 44, chinese ? $"{provider.Accounts.Count} 个账号" : $"{provider.Accounts.Count} accounts", 11, theme.Muted.Hex, false);
        var tight = ProviderRemaining(provider);
        Ring(svg, x + width - 58, y + 30, 12, tight ?? 0, "card" + index);
        Text(svg, x + width - 40, y + 34, UsageFormatting.Percent(tight), 13, tight is null ? theme.Muted.Hex : UsageThemes.ForRemaining(tight.Value).Hex, true);
        var rowY = y + UsageBoardLayout.CardPad + UsageBoardLayout.CardHeader;
        var rowHeight = compact ? UsageBoardLayout.CompactAccountRow : UsageBoardLayout.AccountRow;
        var rowIndex = 0;
        foreach (var account in provider.Accounts)
        {
            DrawRow(svg, theme, account, x + 14, rowY, width - 28, rowHeight - 6, compact, chinese, index + "-" + rowIndex);
            rowY += rowHeight;
            rowIndex++;
        }

        if (compact || !UsageBoardLayout.HasLedger(provider)) return;
        var ledger = provider.Accounts.Select(account => account.Ledger).FirstOrDefault(item => item is not null);
        if (ledger is null) return;
        Text(svg, x + 16, rowY + 16, chinese ? "账本" : "Ledger", 11, theme.Muted.Hex, true);
        Text(svg, x + 70, rowY + 16, Ledger(chinese ? "今日" : "Today", ledger.Today), 11, theme.Text.Hex, false);
        Text(svg, x + 180, rowY + 16, Ledger("7d", ledger.SevenDays), 11, theme.Text.Hex, false);
        Text(svg, x + 290, rowY + 16, Ledger("30d", ledger.ThirtyDays), 11, theme.Text.Hex, false);
    }

    private static void DrawRow(
        StringBuilder svg,
        UsageTheme theme,
        UsageAccountSnapshot account,
        int x,
        int y,
        int width,
        int height,
        bool compact,
        bool chinese,
        string id)
    {
        var window = UsageFormatting.Primary(account);
        var remaining = UsageFormatting.Remaining(window);
        Text(svg, x, y + 14, account.Label, 12, theme.Text.Hex, true);
        var caption = window?.Label ?? UsageFormatting.Error(account.ErrorKey, chinese);
        if (!compact)
        {
            Text(svg, x, y + 30, caption + " · " + UsageFormatting.Reset(window?.ResetsAt, account.SampledAt ?? DateTimeOffset.UnixEpoch, chinese), 10, theme.Muted.Hex, false);
        }

        var barY = compact ? y + 22 : y + 40;
        var barWidth = width - 52;
        svg.Append(CultureInfo.InvariantCulture, $"""<rect x="{x}" y="{barY}" width="{barWidth}" height="6" rx="3" fill="{theme.Track.Rgba(theme.Light ? 0.12 : 0.16)}"/>""");
        if (remaining is { } value && value > 0)
        {
            var fill = Math.Max(8, (int)Math.Round(barWidth * value / 100));
            var color = UsageThemes.ForRemaining(value);
            var glow = UsageThemes.Glow(value);
            svg.Append(CultureInfo.InvariantCulture, $"""<rect x="{x}" y="{barY - 1}" width="{fill}" height="8" rx="4" fill="{color.Rgba(0.35)}" filter="url(#glow)"/>""");
            svg.Append(CultureInfo.InvariantCulture, $"""<defs><linearGradient id="g{id}" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="{glow.Hex}"/><stop offset="1" stop-color="{color.Hex}"/></linearGradient></defs>""");
            svg.Append(CultureInfo.InvariantCulture, $"""<rect x="{x}" y="{barY}" width="{fill}" height="6" rx="3" fill="url(#g{id})"/>""");
        }

        Text(svg, x + barWidth + 8, y + (compact ? 28 : 46), UsageFormatting.Percent(remaining), 11, remaining is null ? theme.Muted.Hex : UsageThemes.ForRemaining(remaining.Value).Hex, true);
    }

    private static void Defs(StringBuilder svg, UsageTheme theme)
    {
        var wash = theme.Light ? "#F4F6FB" : "#101218";
        var blobA = theme.Light ? "#C9DBFF" : "#1E3A5F";
        svg.Append(CultureInfo.InvariantCulture, $"""<defs><linearGradient id="wallpaper" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="{wash}"/><stop offset="1" stop-color="{blobA}"/></linearGradient><filter id="glow" x="-40%" y="-80%" width="180%" height="260%"><feGaussianBlur stdDeviation="2.4"/></filter><filter id="shadow" x="-20%" y="-20%" width="140%" height="150%"><feDropShadow dx="0" dy="8" stdDeviation="10" flood-color="#000" flood-opacity="0.28"/></filter></defs>""");
    }

    private static void Blobs(StringBuilder svg, UsageTheme theme)
    {
        var blobB = theme.Light ? "#F7D7C4" : "#3A2458";
        var blobC = theme.Light ? "#D8F3E4" : "#12343A";
        svg.Append(CultureInfo.InvariantCulture, $"""<circle cx="70" cy="36" r="110" fill="{blobB}" opacity="0.7" filter="url(#glow)"/><circle cx="400" cy="180" r="130" fill="{blobC}" opacity="0.55" filter="url(#glow)"/>""");
    }

    private static void Glass(StringBuilder svg, UsageTheme theme, int x, int y, int width, int height, int radius)
    {
        var fill = theme.Light ? "#FFFFFF" : "#141820";
        var opacity = theme.Light ? "0.62" : "0.58";
        svg.Append(CultureInfo.InvariantCulture, $"""<rect x="{x}" y="{y}" width="{width}" height="{height}" rx="{radius}" fill="{fill}" fill-opacity="{opacity}" stroke="#FFFFFF" stroke-opacity="{(theme.Light ? "0.72" : "0.34")}" stroke-width="1" filter="url(#shadow)"/>""");
        svg.Append(CultureInfo.InvariantCulture, $"""<rect x="{x + 1}" y="{y + 1}" width="{Math.Max(8, width - 2)}" height="16" rx="{Math.Max(8, radius - 8)}" fill="#FFFFFF" fill-opacity="0.16"/>""");
    }

    private static void Chip(StringBuilder svg, int x, int y, int width, string label, UsageTheme theme)
    {
        svg.Append(CultureInfo.InvariantCulture, $"""<rect x="{x}" y="{y}" width="{width}" height="26" rx="13" fill="{theme.Accent.Rgba(0.16)}" stroke="#FFFFFF" stroke-opacity="0.45"/>""");
        Text(svg, x + 10, y + 17, label, 11, theme.Text.Hex, false);
    }

    private static void Ring(StringBuilder svg, int cx, int cy, int radius, double remaining, string id)
    {
        var color = UsageThemes.ForRemaining(remaining);
        var length = 2 * Math.PI * radius;
        var dash = (remaining / 100d * length).ToString("0.##", CultureInfo.InvariantCulture);
        var gap = length.ToString("0.##", CultureInfo.InvariantCulture);
        svg.Append(CultureInfo.InvariantCulture, $"""<circle cx="{cx}" cy="{cy}" r="{radius}" fill="none" stroke="#FFFFFF" stroke-opacity="0.28" stroke-width="2.4"/>""");
        svg.Append(CultureInfo.InvariantCulture, $"""<circle cx="{cx}" cy="{cy}" r="{radius}" fill="none" stroke="{color.Hex}" stroke-width="2.4" stroke-linecap="round" stroke-dasharray="{dash} {gap}" transform="rotate(-90 {cx} {cy})"/>""");
        _ = id;
    }

    private static (string? Name, double? Remaining) Tightest(UsageBoard board)
    {
        string? name = null;
        double? tightest = null;
        foreach (var provider in board.Providers)
        {
            var remaining = ProviderRemaining(provider);
            if (remaining is not { } value) continue;
            if (tightest is null || value < tightest)
            {
                tightest = value;
                name = provider.DisplayName;
            }
        }

        return (name, tightest);
    }

    private static double? ProviderRemaining(UsageProviderSnapshot provider)
    {
        double? tightest = null;
        foreach (var account in provider.Accounts)
        {
            var remaining = UsageFormatting.Remaining(UsageFormatting.Primary(account));
            if (remaining is { } value && (tightest is null || value < tightest)) tightest = value;
        }

        return tightest;
    }

    private static string Monogram(string name) =>
        name.Length == 0 ? "?" : name[..1].ToUpperInvariant();

    private static string Ledger(string label, UsageLedgerPeriod period)
    {
        var cost = period.ApiEquivalentCost is { } value ? " " + UsageFormatting.Money("USD", value) : "";
        return label + " " + UsageFormatting.Tokens(period.Tokens) + cost;
    }

    private static void Text(StringBuilder svg, int x, int y, string value, int size, string fill, bool bold)
    {
        svg.Append(CultureInfo.InvariantCulture, $"""<text x="{x}" y="{y}" fill="{fill}" font-family="Segoe UI Variable, Segoe UI, sans-serif" font-size="{size}" """);
        if (bold) svg.Append("""font-weight="650" """);
        svg.Append('>').Append(Escape(value)).Append("</text>");
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
