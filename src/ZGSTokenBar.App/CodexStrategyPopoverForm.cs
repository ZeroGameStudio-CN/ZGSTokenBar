using System.Globalization;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed class CodexStrategyPopoverForm : Form
{
    private const int LogicalWidth = 640;
    private const int LogicalRowHeight = 24;
    private readonly Font _headingFont = new("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _bodyFont = new("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _strongFont = new("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly List<(string[] Values, Font Font, int Height)> _rows = [];
    private float _scale = 1;
    private int ContentHeight => (int)Math.Ceiling((14 + _rows.Sum(row => row.Height)) * _scale);
    private NativeText _text = NativeText.For("zh-CN");

    internal CodexStrategyPopoverForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        AutoScroll = true;
        DoubleBuffered = true;
        Text = TitleFor(_text);
    }

    protected override bool ShowWithoutActivation => true;

    internal static string TitleFor(NativeText text) =>
        text.Locale == "zh-CN" ? "Codex 使用策略" : "Codex usage strategy";

    internal static string UrgentText(
        CodexUsageStrategy strategy,
        NativeText text)
    {
        if (strategy.Urgent.Count == 0)
            return text.Locale == "zh-CN" ? "未来 24 小时无已知截止 · 点击查看" : "No known deadlines in 24h · Click to view";
        var next = strategy.Urgent[0];
        return $"{CodexAccountFormatting.MaskEmail(next.Account.Account.Email)} · {DeadlineText(next.Kind, text)} {Time(next.At)}";
    }

    internal void ShowFor(
        BarForm owner,
        Rectangle anchor,
        CodexUsageStrategy strategy,
        NativeText text,
        QuotaBackgroundTheme theme,
        float scale)
    {
        var area = Screen.FromRectangle(anchor).WorkingArea;
        var width = Math.Min(area.Width, (int)Math.Round(LogicalWidth * scale));
        SetContent(strategy, text, theme, scale);
        var height = Math.Min(area.Height, ContentHeight);
        var placement = TaskbarMiniPopoverMath.Place(anchor, new Size(width, height), 0, 4, area);
        if (!Visible)
        {
            ClientSize = placement.WindowSize;
            Location = placement.Location;
            Show(owner);
        }
        if (!TaskbarPlacement.ShowAt(Handle, placement.Location, placement.WindowSize))
            Bounds = new Rectangle(placement.Location, placement.WindowSize);
    }

    internal Bitmap RenderForTest(CodexUsageStrategy strategy, NativeText text, QuotaBackgroundTheme theme)
    {
        SetContent(strategy, text, theme, 1);
        var bitmap = new Bitmap(LogicalWidth, ContentHeight);
        using var graphics = Graphics.FromImage(bitmap);
        DrawContent(graphics, LogicalWidth);
        return bitmap;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawContent(e.Graphics, ClientSize.Width);
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

    private void DrawContent(Graphics graphics, int width)
    {
        graphics.Clear(BackColor);
        var state = graphics.Save();
        graphics.TranslateTransform(0, AutoScrollPosition.Y);
        graphics.ScaleTransform(_scale, _scale);
        using var foreground = new SolidBrush(ForeColor);
        using var muted = new SolidBrush(Color.FromArgb(160, 174, 192));
        using var format = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        var contentWidth = Math.Max(1, width / _scale - 24);
        var columnWidths = new[] { .23f, .19f, .29f, .29f };
        var y = 6f;
        foreach (var (values, font, height) in _rows)
        {
            var x = 12f;
            for (var column = 0; column < values.Length; column++)
            {
                var cellWidth = contentWidth * (values.Length == 1 ? 1 : columnWidths[column]);
                graphics.DrawString(values[column], column == 0 ? font : _bodyFont,
                    font == _bodyFont ? muted : foreground,
                    new RectangleF(x, y, Math.Max(1, cellWidth - 5), height), format);
                x += cellWidth;
            }
            y += height;
        }
        graphics.Restore(state);
    }

    private void SetContent(
        CodexUsageStrategy strategy,
        NativeText text,
        QuotaBackgroundTheme theme,
        float scale)
    {
        _text = text;
        _scale = scale;
        Text = TitleFor(text);
        BackColor = theme.Popover;
        ForeColor = Color.FromArgb(241, 245, 249);
        _rows.Clear();

        Heading(TitleFor(text), _headingFont, extraHeight: 5);
        Heading(T($"未来 24 小时有 {strategy.Urgent.Count} 项已知截止。按任务所需额度选择账号。",
            $"{strategy.Urgent.Count} known deadlines in 24h. Choose accounts that fit each task."));
        _rows.Add(([T("账号", "Account"), T("余量 / 复查", "Quota / review"),
            T("事项", "Action"), T("截止 / 恢复", "Deadline / recovery")], _bodyFont, LogicalRowHeight));
        var ready = strategy.Accounts.Where(row => row.State == CodexStrategyState.Ready).ToArray();
        ReadyRows(T("现在可用", "Ready now"), ready.Where(row => !row.HasLittleQuota));
        ReadyRows(T("余量较少 · 先核对任务", "Low quota · Check task fit"), ready.Where(row => row.HasLittleQuota));
        Rows(T("等待恢复或刷新", "Waiting or needs refresh"),
            strategy.Accounts.Where(row => row.State != CodexStrategyState.Ready),
            row => row.State switch
            {
                CodexStrategyState.Waiting => T("等待额度恢复", "Waiting for quota"),
                CodexStrategyState.Expired => T("订阅有效期结束", "Subscription ended"),
                _ => T("额度待刷新", "Refresh quota"),
            },
            row => Time(row.Recovery));
        Rows(T("重置卡", "Reset credits"),
            strategy.Accounts.Where(row => row.State != CodexStrategyState.Expired && (row.CardExpiry is not null || row.Cards > 0))
                .OrderBy(row => row.RedeemBy ?? DateTimeOffset.MaxValue),
            row => row.CardExpiry is null ? T("到期待刷新", "Expiry unavailable")
                : row.ResetBeforeCard ? T("自然重置后复查", "Check after scheduled reset")
                : row.CanRedeem ? T("可兑卡 · 手动确认", "Eligible · Confirm manually")
                : T("核对兑换条件", "Check eligibility"),
            row => row.RedeemBy != row.CardExpiry
                ? $"{Time(row.RedeemBy)} {T("订阅结束", "plan ends")}"
                : Time(row.CardExpiry),
            showCredits: true);
        if (strategy.Accounts.Count == 0) Heading(T("暂无 Cockpit Tools 账号额度。", "No Cockpit Tools account quotas."));

        AccessibleDescription = string.Join(Environment.NewLine, _rows.Select(row => string.Join(" · ", row.Values)));
        AutoScrollMinSize = new Size(0, ContentHeight);
        Invalidate();
    }

    private void ReadyRows(string title, IEnumerable<CodexStrategyAccount> accounts) =>
        Rows(title, accounts,
            row => row.DeadlineKind is { } kind ? DeadlineText(kind, _text) : T("可分配任务", "Available"),
            row => Time(row.Deadline));

    private void Rows(
        string title,
        IEnumerable<CodexStrategyAccount> accounts,
        Func<CodexStrategyAccount, string> action,
        Func<CodexStrategyAccount, string> time,
        bool showCredits = false)
    {
        var rows = accounts.ToArray();
        if (rows.Length == 0) return;
        Heading(title, _strongFont, extraHeight: 4);
        foreach (var row in rows)
        {
            var quota = showCredits
                ? row.ReviewBy is { } review ? T($"{Time(review)} 前复查", $"Check by {Time(review)}")
                    : row.CardExpiry is not null ? T("现在复查", "Check now")
                    : row.Cards is { } count ? T($"{count} 张", $"{count} credits") : "—"
                : Quotas(row.Windows);
            _rows.Add(([CodexAccountFormatting.MaskEmail(row.Account.Email), quota, action(row), time(row)],
                _strongFont, LogicalRowHeight));
        }
    }

    private void Heading(string value, Font? font = null, int extraHeight = 0) =>
        _rows.Add(([value], font ?? _bodyFont, LogicalRowHeight + extraHeight));

    private static string Quotas(IReadOnlyList<QuotaWindow> windows) =>
        windows.Count == 0 ? "—" : string.Join(" · ", windows.Select(window =>
            $"{QuotaDisplayFormatting.FormatWindowShort(window)} {Remaining(window.UsedPercent)}"));

    private static string Remaining(double? used) => used switch
    {
        null => "—",
        > 99 and < 100 => "<1%",
        _ => $"{Math.Floor(100 - used.Value):0}%",
    };

    private static string Time(DateTimeOffset? timestamp) =>
        timestamp?.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—";

    private string T(string zh, string en) => _text.Locale == "zh-CN" ? zh : en;

    private static string DeadlineText(CodexStrategyDeadline kind, NativeText text) => kind switch
    {
        CodexStrategyDeadline.Reset => text.Locale == "zh-CN" ? "自然重置" : "Scheduled reset",
        CodexStrategyDeadline.Subscription => text.Locale == "zh-CN" ? "订阅有效期结束" : "Subscription ends",
        _ => text.Locale == "zh-CN" ? "兑卡截止" : "Redeem by",
    };

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        _headingFont.Dispose();
        _bodyFont.Dispose();
        _strongFont.Dispose();
    }
}
