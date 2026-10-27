namespace ZGSTokenBar.App;

internal static class ModelIdentityColors
{
    public static Color Model(string model) => Identity("model:" + model.Trim().ToLowerInvariant());
    public static Color Effort(string effort) => Identity("effort:" + effort.Trim().ToLowerInvariant());

    private static Color Identity(string identity)
    {
        var hash = 2166136261u;
        foreach (var character in identity) hash = unchecked((hash ^ character) * 16777619u);
        var hue = hash % 360 / 60d;
        const double saturation = .55;
        const double lightness = .72;
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = chroma * (1 - Math.Abs(hue % 2 - 1));
        var offset = lightness - chroma / 2;
        var (red, green, blue) = hue switch
        {
            < 1 => (chroma, x, 0d), < 2 => (x, chroma, 0d), < 3 => (0d, chroma, x),
            < 4 => (0d, x, chroma), < 5 => (x, 0d, chroma), _ => (chroma, 0d, x),
        };
        return Color.FromArgb((int)Math.Round((red + offset) * 255),
            (int)Math.Round((green + offset) * 255), (int)Math.Round((blue + offset) * 255));
    }
}
