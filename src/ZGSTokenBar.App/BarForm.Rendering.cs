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

        try
        {
            DrawGlassDock(graphics);
            _taskbarRenderFailureAreaIds.Remove("bar");
        }
        catch (Exception exception) when (IsRecoverableRenderException(exception))
        {
            TraceTaskbarRenderFailure("bar", exception);
        }
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

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius) => UiPaint.RoundedPath(bounds, radius);

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
