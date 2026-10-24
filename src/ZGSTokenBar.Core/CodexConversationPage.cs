namespace ZGSTokenBar.Core;

public readonly record struct CodexConversationPage(int Offset, int Count, int Total)
{
    public const int PageSize = 6;
    public int PageLength { get; init; } = PageSize;
    public bool HasPrevious => Offset > 0;
    public bool HasNext => Offset + Count < Total;
    public int PageNumber => Offset / PageLength + 1;
    public int PageCount => Math.Max(1, (Total + PageLength - 1) / PageLength);

    public static CodexConversationPage Create(
        int offset,
        int total,
        int pageLength = PageSize)
    {
        total = Math.Max(0, total);
        pageLength = Math.Max(1, pageLength);
        offset = Math.Clamp(offset / pageLength, 0, Math.Max(0, (total - 1) / pageLength)) * pageLength;
        return new(offset, Math.Min(pageLength, total - offset), total)
        {
            PageLength = pageLength,
        };
    }
}
