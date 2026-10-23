namespace ZGSTokenBar.Core;

public readonly record struct CodexConversationPage(int Offset, int Count, int Total)
{
    public const int PageSize = 6;
    public bool HasPrevious => Offset > 0;
    public bool HasNext => Offset + Count < Total;
    public int PageNumber => Offset / PageSize + 1;
    public int PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);

    public static CodexConversationPage Create(int offset, int total)
    {
        total = Math.Max(0, total);
        offset = Math.Clamp(offset / PageSize, 0, Math.Max(0, (total - 1) / PageSize)) * PageSize;
        return new(offset, Math.Min(PageSize, total - offset), total);
    }
}
