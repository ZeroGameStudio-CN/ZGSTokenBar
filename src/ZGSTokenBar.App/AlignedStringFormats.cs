using System.Drawing;

namespace ZGSTokenBar.App;

internal sealed class AlignedStringFormats : IDisposable
{
    private readonly StringFormat _near = Create(StringAlignment.Near);
    private readonly StringFormat _center = Create(StringAlignment.Center);
    private readonly StringFormat _far = Create(StringAlignment.Far);

    public StringFormat For(StringAlignment alignment) => alignment switch
    {
        StringAlignment.Center => _center,
        StringAlignment.Far => _far,
        _ => _near,
    };

    public void Dispose()
    {
        _near.Dispose();
        _center.Dispose();
        _far.Dispose();
    }

    private static StringFormat Create(StringAlignment alignment) => new()
    {
        Alignment = alignment,
        LineAlignment = StringAlignment.Center,
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap,
    };
}
