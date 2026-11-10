using System.Globalization;
using System.Text;

namespace ZGSTokenBar.Core;

public static class UsageBoardSvg
{
    public static string Render(UsageBoard board, UsageTheme theme, bool compact, bool chinese)
    {
        var height = UsageBoardLayout.Height(board, compact);
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{UsageBoardLayout.Width}\" height=\"{height}\" viewBox=\"0 0 {UsageBoardLayout.Width} {height}\">");
        Rect(svg, 0, 0, UsageBoardLayout.Width, height, theme.Canvas.Hex, 0);
        Text(svg, 20, 34, chinese ? "用量" : "Usage", 20, theme.Text.Hex, true);
        Text(svg, 20, 52, chinese ? "本地只读 · 账号分组" : "Local read-only · grouped accounts", 12, theme.Muted.Hex, false);
        var y = UsageBoardLayout.HeaderHeight;
        if (board.Providers.Count == 0)
        {
            Text(svg, 20, y + 28, chinese ? "没有可读的登录" : "No readable sign-in", 14, theme.Muted.Hex, false);
        }

        foreach (var provider in board.Providers)
        {
            Text(svg, 20, y + 18, provider.DisplayName, 13, theme.Text.Hex, true);
            Text(svg, 360, y + 18, provider.Accounts.Count.ToString(CultureInfo.InvariantCulture), 12, theme.Muted.Hex, false);
            y += UsageBoardLayout.SectionHeight;
            var accounts = provider.Accounts.Count == 0
                ? Array.Empty<UsageAccountSnapshot>()
                : provider.Accounts.ToArray();
            if (accounts.Length == 0) continue;
            foreach (var account in accounts)
            {
                var row = compact ? UsageBoardLayout.CompactRowHeight : UsageBoardLayout.ExpandedCardHeight;
                DrawAccount(svg, theme, account, y, row, compact, chinese);
                y += row + UsageBoardLayout.CardGap;
            }

            y += 2;
        }

        svg.Append("</svg>");
        return svg.ToString();
    }

    public static string Before(bool chinese)
    {
        var title = "Grok";
        var body = chinese ? "额度不可读" : "Quota unavailable";
        return "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"285\" height=\"92\" viewBox=\"0 0 285 92\">"
            + "<rect width=\"285\" height=\"92\" fill=\"#1c212b\"/>"
            + "<text x=\"14\" y=\"28\" fill=\"#e6ebf4\" font-family=\"Segoe UI, sans-serif\" font-size=\"14\" font-weight=\"700\">"
            + title + "</text>"
            + "<text x=\"14\" y=\"56\" fill=\"#e6ebf4\" font-family=\"Segoe UI, sans-serif\" font-size=\"12\">"
            + body + "</text></svg>";
    }

    private static void DrawAccount(
        StringBuilder svg,
        UsageTheme theme,
        UsageAccountSnapshot account,
        int y,
        int height,
        bool compact,
        bool chinese)
    {
        Rect(svg, 16, y, UsageBoardLayout.Width - 32, height - 2, theme.Card.Hex, 12);
        var window = UsageFormatting.Primary(account);
        var remaining = UsageFormatting.Remaining(window);
        Text(svg, 28, y + 18, account.Label, 12, theme.Text.Hex, true);
        Text(svg, 300, y + 18, UsageFormatting.Percent(remaining), 12, remaining is null ? theme.Muted.Hex : UsageThemes.ForRemaining(remaining.Value).Hex, true);
        var barY = y + 26;
        Rect(svg, 28, barY, 300, 6, theme.Track.Hex, 3);
        if (remaining is { } value && value > 0)
        {
            Rect(svg, 28, barY, Math.Max(4, (int)Math.Round(300 * value / 100)), 6, UsageThemes.ForRemaining(value).Hex, 3);
        }

        if (compact) return;
        Text(svg, 28, y + 50, window is null ? UsageFormatting.Error(account.ErrorKey, chinese) : window.Label, 11, theme.Muted.Hex, false);
        Text(svg, 180, y + 50, UsageFormatting.Reset(window?.ResetsAt, account.SampledAt ?? DateTimeOffset.UnixEpoch, chinese), 11, theme.Muted.Hex, false);
        if (account.Credits?.PrepaidBalance is { } prepaid)
        {
            Text(svg, 28, y + 68, (chinese ? "预付 " : "Prepaid ") + UsageFormatting.Money(account.Credits.Currency, prepaid), 11, theme.Text.Hex, false);
        }

        if (account.Ledger is { } ledger)
        {
            Text(svg, 28, y + 88, Ledger(chinese ? "今日" : "Today", ledger.Today), 11, theme.Muted.Hex, false);
            Text(svg, 150, y + 88, Ledger(chinese ? "7 天" : "7d", ledger.SevenDays), 11, theme.Muted.Hex, false);
            Text(svg, 270, y + 88, Ledger(chinese ? "30 天" : "30d", ledger.ThirtyDays), 11, theme.Muted.Hex, false);
        }

        var productX = 28;
        foreach (var product in account.Products.Take(3))
        {
            var chip = product.Product + " " + product.Percent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
            Text(svg, productX, y + 108, chip, 10, theme.Muted.Hex, false);
            productX += 110;
        }
    }

    private static string Ledger(string label, UsageLedgerPeriod period)
    {
        var cost = period.ApiEquivalentCost is { } value ? " " + UsageFormatting.Money("USD", value) : "";
        return label + " " + UsageFormatting.Tokens(period.Tokens) + cost;
    }

    private static void Rect(StringBuilder svg, int x, int y, int width, int height, string fill, int radius)
    {
        svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{y}\" width=\"{width}\" height=\"{height}\" rx=\"{radius}\" fill=\"{fill}\"/>");
    }

    private static void Text(StringBuilder svg, int x, int y, string value, int size, string fill, bool bold)
    {
        svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{x}\" y=\"{y}\" fill=\"{fill}\" font-family=\"Segoe UI, sans-serif\" font-size=\"{size}\"");
        if (bold) svg.Append(" font-weight=\"700\"");
        svg.Append('>').Append(Escape(value)).Append("</text>");
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
