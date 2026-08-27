using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal readonly record struct PlanBadgeStyle(Color Fill, Color Border, Color Text);

internal static class PlanBadgePresentation
{
    internal static string Label(string? plan) =>
        CodexAccountFormatting.PlanLabel(plan).ToUpperInvariant();

    internal static float Width(string label) => label switch
    {
        "PLUS" => 29f,
        "PRO" => 25f,
        "FREE" => 29f,
        "API KEY" => 42f,
        _ => 34f,
    };

    internal static bool TryGetStyle(string label, out PlanBadgeStyle style)
    {
        style = label switch
        {
            "PLUS" => new PlanBadgeStyle(
                Color.FromArgb(20, 67, 67),
                Color.FromArgb(45, 212, 191),
                Color.FromArgb(94, 234, 212)),
            "PRO" => new PlanBadgeStyle(
                Color.FromArgb(78, 59, 25),
                Color.FromArgb(245, 158, 11),
                Color.FromArgb(253, 230, 138)),
            "FREE" or "API KEY" => new PlanBadgeStyle(
                Color.FromArgb(30, 41, 59),
                Color.FromArgb(100, 116, 139),
                Color.FromArgb(203, 213, 225)),
            _ => default,
        };
        return label is "PLUS" or "PRO" or "FREE" or "API KEY";
    }
}
