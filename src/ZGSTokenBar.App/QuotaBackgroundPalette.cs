using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed record QuotaBackgroundTheme(
    string Id,
    Color Outer,
    Color ProviderGroup,
    Color QuotaGroup,
    Color Popover);

internal static class QuotaBackgroundPalette
{
    internal static readonly IReadOnlyList<QuotaBackgroundTheme> All =
    [
        new(
            "midnight",
            Color.FromArgb(2, 6, 23),
            Color.FromArgb(6, 11, 22),
            Color.FromArgb(10, 18, 32),
            Color.FromArgb(7, 12, 24)),
        new(
            "graphite",
            Color.FromArgb(8, 9, 11),
            Color.FromArgb(18, 19, 22),
            Color.FromArgb(27, 28, 31),
            Color.FromArgb(16, 17, 20)),
        new(
            "navy",
            Color.FromArgb(3, 21, 37),
            Color.FromArgb(7, 28, 44),
            Color.FromArgb(11, 38, 56),
            Color.FromArgb(7, 27, 45)),
        new(
            "plum",
            Color.FromArgb(22, 10, 29),
            Color.FromArgb(31, 15, 39),
            Color.FromArgb(42, 21, 50),
            Color.FromArgb(29, 14, 38)),
    ];

    internal static QuotaBackgroundTheme Resolve(string? id)
    {
        var normalized = AppSettings.NormalizeBackgroundPalette(id);
        return All.First(theme => string.Equals(theme.Id, normalized, StringComparison.Ordinal));
    }
}
