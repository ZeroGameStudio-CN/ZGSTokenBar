using System.Drawing.Drawing2D;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

/// <summary>Quota-window section: remaining, pace, reset, plan/account, credits and reset cards.</summary>
internal sealed partial class QuotaPopoverForm
{
    /// <summary>
    /// The single quota card used by every provider ring: header (logo, name, window chip, plan
    /// pill + account), hero remaining % with reset countdown, progress rail, then fixed rows in a
    /// fixed order (pace, tokens + detail) and a footer (freshness, pin hint). Missing data keeps
    /// its row and shows a dash so all providers share one geometry.
    /// </summary>
    private void DrawQuotaCard(Graphics graphics, RectangleF body, QuotaPopoverContent content)
    {
        var used = content.Window.UsedPercent is null
            ? (double?)null
            : Math.Clamp(content.Window.UsedPercent.Value, 0, 100);
        var remaining = used is null ? (double?)null : 100 - used.Value;
        var now = _renderNow ?? DateTimeOffset.UtcNow;
        var blocked = content.WeeklyBlockResetAt is not null;
        var valueColor = blocked || remaining is null ? CardDim : QuotaColorScale.ForRemaining(remaining.Value);
        var left = body.X + 14;
        var right = body.Right - 14;
        var y = body.Y;
        using var titleBrush = new SolidBrush(CardTitle);
        using var valueBrush = new SolidBrush(CardValue);
        using var mutedBrush = new SolidBrush(CardMuted);
        using var dimBrush = new SolidBrush(CardDim);
        using var tealBrush = new SolidBrush(CardTeal);
        using var hairline = new Pen(CardHairline, 1f);

        // Header.
        DrawLogo(graphics, _providerLogo!, new RectangleF(left, y + 13, 28, 28));
        var window = QuotaDisplayFormatting.FormatWindowShort(content.Window);
        var chipWidth = graphics.MeasureString(window, _cardChipFont, PointF.Empty, StringFormat.GenericTypographic).Width + 12;
        var chip = new RectangleF(right - chipWidth, y + 14, chipWidth, 16);
        using (var chipPath = RoundedRectangle(chip, 8))
        using (var chipFill = new SolidBrush(Color.FromArgb(14, 255, 255, 255)))
        using (var chipEdge = new Pen(Color.FromArgb(40, 255, 255, 255), 1f))
        {
            graphics.FillPath(chipFill, chipPath);
            graphics.DrawPath(chipEdge, chipPath);
        }
        DrawString(graphics, window, _cardChipFont, mutedBrush, chip, StringAlignment.Center);
        var titleRight = chip.X - 6;
        if (content.Pinned)
        {
            UiIcons.Pin(graphics, new RectangleF(chip.X - 17, y + 16, 12, 12), UiPalette.Slate400);
            titleRight -= 16;
        }
        DrawString(graphics, content.Card.Label, _cardTitleFont, titleBrush,
            new RectangleF(left + 38, y + 12, Math.Max(1, titleRight - left - 38), 18), StringAlignment.Near);
        DrawPlanAndAccount(graphics, content.Card, new RectangleF(left + 38, y + 31, right - left - 38, 14));

        // Hero: remaining % on the left, reset countdown on the right.
        var percent = remaining is null ? "--" : FormatPercent(remaining.Value);
        using var heroBrush = new SolidBrush(valueColor);
        DrawString(graphics, percent, _cardHeroFont, heroBrush, new RectangleF(left - 1, y + 50, 110, 32), StringAlignment.Near);
        var heroWidth = graphics.MeasureString(percent, _cardHeroFont, PointF.Empty, StringFormat.GenericTypographic).Width;
        DrawString(graphics, _text.IsChinese ? "剩余" : "left", _cardRowFont, mutedBrush,
            new RectangleF(left + heroWidth + 4, y + 65, 40, 14), StringAlignment.Near);
        var countdown = blocked
            ? _text.FormatResetCountdown(content.WeeklyBlockResetAt!.Value, now)
            : content.Window.ResetsAt is { } reset ? _text.FormatResetCountdown(reset, now) : "—";
        DrawString(graphics, countdown, _cardRowStrongFont, blocked || content.Window.ResetsAt is null ? dimBrush : valueBrush,
            new RectangleF(right - 120, y + 54, 120, 15), StringAlignment.Far);
        var resetText = blocked
            ? _text.WeeklyQuotaBlocked
            : content.Window.ResetsAt is { } resetAt ? _text.ResetAt(resetAt, now) : _text.ResetUnavailable;
        DrawString(graphics, resetText, _cardSmallFont, mutedBrush,
            new RectangleF(right - 140, y + 70, 140, 12), StringAlignment.Far);

        // Progress rail.
        // The rail fills to the figure shown (floored like the hero %), and a small diamond marks
        // where remaining would be now if the window were used evenly (100 − elapsed share).
        var rail = new RectangleF(left, y + 88, right - left, 6);
        UiPaint.ProgressBar(graphics, rail, remaining is { } shown ? PercentFormat.RemainingValue(shown) / 100 : null,
            valueColor, Color.White.WithAlpha(18), minFill: 6);
        if (!blocked && remaining is not null && QuotaDisplayFormatting.ExpectedRemaining(content.Window, now) is { } expected)
            UiPaint.PaceMarker(graphics, rail, expected / 100);
        graphics.DrawLine(hairline, left, y + 104.5f, right, y + 104.5f);

        // Row 1: pace.
        var pace = _text.QuotaPace(content.Pace?.ForDisplay(now), now, content.Window.ResetsAt);
        var hasPace = pace.Right.Length > 0;
        UiIcons.Trend(graphics, new RectangleF(left, y + 115, 10, 10), hasPace ? CardTeal : CardMuted);
        DrawString(graphics, pace.Left, _cardRowFont, hasPace ? tealBrush : mutedBrush,
            new RectangleF(left + 16, y + 112, hasPace ? 110 : right - left - 16, 16), StringAlignment.Near);
        DrawString(graphics, hasPace ? pace.Right : "—", _cardRowStrongFont, hasPace ? tealBrush : dimBrush,
            new RectangleF(right - 96, y + 112, 96, 16), StringAlignment.Far);

        // Row 2: tokens (label, value) + detail line.
        var tokens = QuotaTokenUsage;
        UiIcons.Tokens(graphics, new RectangleF(left, y + 135, 10, 10), tokens is null ? CardMuted : CardValue);
        DrawString(graphics, tokens?.Label ?? (_text.IsChinese ? "Token 用量" : "Tokens"), _cardRowFont, mutedBrush,
            new RectangleF(left + 16, y + 132, 120, 16), StringAlignment.Near);
        DrawString(graphics, tokens?.Value ?? "—", _cardRowStrongFont, tokens is null ? dimBrush : valueBrush,
            new RectangleF(right - 96, y + 132, 96, 16), StringAlignment.Far);
        // A provider without token stats keeps the row (one geometry for every ring) but only its dash.
        if (HasTokenNote(content) && tokens?.Detail is { Length: > 0 } tokenDetail)
            DrawString(graphics, tokenDetail, _cardSmallFont, dimBrush, new RectangleF(left + 16, y + 149, right - left - 16, 12), StringAlignment.Near);

        // Detail rows (CodexBar-style): every window with its own reset, credits, subscription dates.
        var lift = TokenNoteLift(content);
        var rowY = y + 166 - lift + DetailSectionPadding / 2f;
        var rows = 0;
        if (DetailRowCount(content) > 0) graphics.DrawLine(hairline, left, y + 166.5f - lift, right, y + 166.5f - lift);
        foreach (var detail in DetailWindows(content.Card))
        {
            var detailUsed = detail.UsedPercent is { } value ? Math.Clamp(value, 0, 100) : (double?)null;
            var detailRemaining = detailUsed is null ? (double?)null : 100 - detailUsed.Value;
            var current = string.Equals(detail.Label, content.Window.Label, StringComparison.Ordinal)
                && detail.Duration == content.Window.Duration;
            var tone = detailRemaining is null ? CardDim : QuotaColorScale.ForRemaining(detailRemaining.Value);
            using (var dot = new SolidBrush(current ? tone : Color.FromArgb(120, tone)))
                graphics.FillEllipse(dot, left + 1, rowY + 6, 6, 6);
            DrawString(graphics, DetailWindowLabel(detail), _cardRowFont, current ? valueBrush : mutedBrush,
                new RectangleF(left + 13, rowY, 100, DetailRowHeight), StringAlignment.Near);
            DrawString(graphics, detail.ResetsAt is { } at ? CompactResetTime(at, now) : "—", _cardSmallFont, dimBrush,
                new RectangleF(right - 140, rowY + 1, 96, DetailRowHeight), StringAlignment.Far);
            using (var percentBrush = new SolidBrush(detailRemaining is null ? CardDim : tone))
                DrawString(graphics, detailRemaining is null ? "—" : FormatPercent(detailRemaining.Value), _cardRowStrongFont, percentBrush,
                    new RectangleF(right - 42, rowY, 42, DetailRowHeight), StringAlignment.Far);
            rowY += DetailRowHeight;
            rows++;
        }
        if (content.Card.Provider == ProviderKind.Codex && content.Card.Credits is { } credits)
        {
            UiIcons.Credits(graphics, new RectangleF(left, rowY + 4, 10, 10), credits.HasCredits || credits.Unlimited ? CardValue : CardMuted);
            DrawString(graphics, _text.IsChinese ? "积分" : "Credits", _cardRowFont, mutedBrush,
                new RectangleF(left + 16, rowY, 90, DetailRowHeight), StringAlignment.Near);
            DrawString(graphics, CreditsText(credits, _text.IsChinese), _cardRowStrongFont,
                credits.HasCredits || credits.Unlimited ? valueBrush : dimBrush,
                new RectangleF(right - 120, rowY, 120, DetailRowHeight), StringAlignment.Far);
            rowY += DetailRowHeight;
            rows++;
        }
        if (content.Card.Provider == ProviderKind.Codex && content.ResetCards is { } cards)
        {
            var hasCards = cards.Count > 0;
            UiIcons.ResetCard(graphics, new RectangleF(left, rowY + 4, 10, 10), hasCards ? CardTeal : CardMuted);
            DrawString(graphics, _text.IsChinese ? "重置卡" : "Reset cards", _cardRowFont, mutedBrush,
                new RectangleF(left + 16, rowY, 90, DetailRowHeight), StringAlignment.Near);
            DrawString(graphics, ResetCardsText(cards, now, _text.IsChinese), _cardRowStrongFont,
                hasCards ? valueBrush : dimBrush,
                new RectangleF(right - 160, rowY, 160, DetailRowHeight), StringAlignment.Far);
            rowY += DetailRowHeight;
            rows++;
        }
        {
            var (label, value) = SubscriptionText(content.Card.Subscription, now, _text.IsChinese);
            UiIcons.Calendar(graphics, new RectangleF(left, rowY + 4, 10, 10), value is null ? CardMuted : CardValue);
            DrawString(graphics, label, _cardRowFont, mutedBrush,
                new RectangleF(left + 16, rowY, 90, DetailRowHeight), StringAlignment.Near);
            DrawString(graphics, value ?? "—", _cardRowStrongFont,
                value is null ? dimBrush : valueBrush,
                new RectangleF(right - 140, rowY, 140, DetailRowHeight), StringAlignment.Far);
            rows++;
        }
        var footerY = y + (UnifiedBodyHeight(content) - LogicalUnifiedQuotaBodyHeight);  // includes the token-note lift

        // Footer.
        graphics.DrawLine(hairline, left, footerY + 166.5f, right, footerY + 166.5f);
        DrawString(graphics, _text.Freshness(content.CapturedAt, now), _cardSmallFont, mutedBrush,
            new RectangleF(left, footerY + 170, 110, 12), StringAlignment.Near);
        DrawString(graphics, content.Pinned ? _text.ClosePinnedHint : _text.PinHint, _cardSmallFont, mutedBrush,
            new RectangleF(right - 110, footerY + 170, 110, 12), StringAlignment.Far);
    }

    /// <summary>"Gemini · 7d" stays as the provider named it; plain windows use the short chip text (5h / 7d).</summary>
    internal static string DetailWindowLabel(QuotaWindow window) =>
        window.Label.Contains(" · ", StringComparison.Ordinal) ? window.Label : QuotaDisplayFormatting.FormatWindowShort(window);

    /// <summary>Local reset time: "HH:mm" inside a day, "M/d HH:mm" beyond.</summary>
    internal static string CompactResetTime(DateTimeOffset reset, DateTimeOffset now)
    {
        var local = reset.ToLocalTime();
        var format = reset - now < TimeSpan.FromHours(20) ? "HH:mm" : "M/d HH:mm";
        return local.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static string CreditsText(CodexCreditBalance credits, bool chinese)
    {
        if (credits.Unlimited) return chinese ? "无限" : "Unlimited";
        if (credits.Balance is { } balance)
        {
            var amount = balance.ToString(balance >= 1000 ? "N0" : "0.##", System.Globalization.CultureInfo.InvariantCulture);
            return chinese ? $"剩余 {amount}" : $"{amount} left";
        }
        return credits.HasCredits ? (chinese ? "有积分" : "Available") : (chinese ? "无" : "None");
    }

    /// <summary>Label and value of the subscription row; value is null when the source has no dates.</summary>
    /// <summary>"2 张 · 11/20 到期" / "2 cards · exp 11/20"; "0 张" when the account has none.</summary>
    internal static string ResetCardsText(CodexResetCards cards, DateTimeOffset now, bool chinese)
    {
        var count = chinese ? $"{cards.Count} 张" : $"{cards.Count} card{(cards.Count == 1 ? "" : "s")}";
        if (cards.Count <= 0 || cards.NextExpiry is not { } expiry || expiry <= now) return count;
        var local = expiry.ToLocalTime().ToString("M/d", System.Globalization.CultureInfo.InvariantCulture);
        return chinese ? $"{count} · {local} 到期" : $"{count} · exp {local}";
    }

    internal static (string Label, string? Value) SubscriptionText(UsageSubscriptionPeriod? period, DateTimeOffset now, bool chinese)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        string Days(DateTimeOffset end)
        {
            var days = (int)Math.Ceiling((end - now).TotalDays);
            return days <= 0 ? (chinese ? "今天" : "today") : chinese ? $"剩{days}天" : $"{days}d";
        }
        switch (period)
        {
            case { Kind: "active-until", End: { } until }:
                return (chinese ? "订阅有效期" : "Subscription",
                    (chinese ? "至 " : "until ") + until.ToLocalTime().ToString("yyyy/M/d", culture) + " · " + Days(until));
            case { Kind: "quota-reset", End: { } reset }:
                return (chinese ? "额度周期" : "Allowance",
                    (chinese ? "重置 " : "resets ") + reset.ToLocalTime().ToString("M/d", culture) + " · " + Days(reset));
            case { End: { } end }:
                var start = period.Start is { } from ? from.ToLocalTime().ToString("M/d", culture) + "–" : "";
                return (chinese ? "计费周期" : "Billing period",
                    start + end.ToLocalTime().ToString("M/d", culture) + " · " + (chinese ? "续费" : "renews ") + (chinese ? Days(end) : Days(end)));
            default:
                return (chinese ? "订阅" : "Subscription", null);
        }
    }

    /// <summary>Plan pill (any provider) followed by the account hint.</summary>
    private void DrawPlanAndAccount(Graphics graphics, QuotaCard card, RectangleF bounds)
    {
        var plan = CardPlanLabel(card);
        var x = bounds.X;
        if (plan is not null)
        {
            var style = PlanBadgePresentation.TryGetStyle(plan, out var known)
                ? known
                : new PlanBadgeStyle(Color.FromArgb(34, 38, 50), Color.FromArgb(70, 78, 98), Color.FromArgb(214, 220, 232));
            var width = graphics.MeasureString(plan, _statusFont, PointF.Empty, StringFormat.GenericTypographic).Width + 10;
            var pill = new RectangleF(x, bounds.Y, width, 13);
            using var path = RoundedRectangle(pill, 4);
            using var fill = new SolidBrush(style.Fill);
            using var edge = new Pen(style.Border, 1);
            using var ink = new SolidBrush(style.Text);
            graphics.FillPath(fill, path);
            graphics.DrawPath(edge, path);
            DrawString(graphics, plan, _statusFont, ink, pill, StringAlignment.Center);
            x = pill.Right + 6;
        }
        var account = HasAccountHint(card) ? card.AccountHint! : plan is null ? _text.LiveQuota : null;
        if (account is null) return;
        using var brush = new SolidBrush(Color.FromArgb(190, 198, 212));
        DrawString(graphics, account, _subtitleFont, brush,
            new RectangleF(x, bounds.Y, Math.Max(1, bounds.Right - x), bounds.Height), StringAlignment.Near);
    }

    /// <summary>Plan label for the header pill: shared Codex labels, otherwise the provider's own plan name.</summary>
    internal static string? CardPlanLabel(QuotaCard card)
    {
        if (string.IsNullOrWhiteSpace(card.Badge)) return null;
        if (card.Provider == ProviderKind.Codex) return PlanBadgePresentation.Label(card.Badge);
        var label = card.Badge.Trim().ToUpperInvariant();
        // A sign-in type is not a plan; older snapshots stored Grok's auth_mode here.
        if (SignInTypes.Contains(label)) return null;
        return label.Length > 18 ? label[..18].TrimEnd() : label;
    }

    private static readonly HashSet<string> SignInTypes = new(StringComparer.Ordinal)
    {
        "OIDC", "OAUTH", "OAUTH2", "API_KEY", "APIKEY", "API KEY", "SSO", "DEVICE", "BROWSER", "TOKEN",
    };

}
