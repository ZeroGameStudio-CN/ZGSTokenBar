namespace ZGSTokenBar.Core;

/// <summary>Quota ranking within fixed provider groups, with a stable interval before quota-only changes.</summary>
public sealed class GlassDockAccountOrder
{
    public const int SettleMilliseconds = 10000;
    private string[] _membership = [];
    private string[] _order = [];
    private string[] _candidate = [];
    private long _candidateSince;

    public IReadOnlyDictionary<string, int> Update(IReadOnlyList<GlassDockRing> rings, long now, bool interacting)
    {
        var unique = rings.DistinctBy(r => r.StableId).ToArray();
        var membership = unique.OrderBy(r => r.StableId, StringComparer.Ordinal)
            .Select(r => r.StableId + "|" + r.InUse + "|" + r.OverflowOnly + "|" + r.ProviderOrder).ToArray();
        var desired = unique.OrderBy(r => r.ProviderOrder).ThenBy(r => r.ProviderId, StringComparer.Ordinal)
            .ThenByDescending(r => r.RemainingPercent ?? double.NegativeInfinity)
            .ThenBy(r => r.AccountNumber).ThenBy(r => r.StableId, StringComparer.Ordinal)
            .Select(r => r.StableId).ToArray();
        if (!_membership.SequenceEqual(membership, StringComparer.Ordinal))
        {
            _membership = membership; _order = desired; _candidate = [];
        }
        else if (_order.SequenceEqual(desired, StringComparer.Ordinal)) _candidate = [];
        else
        {
            if (!_candidate.SequenceEqual(desired, StringComparer.Ordinal)) { _candidate = desired; _candidateSince = now; }
            if (!interacting && now >= _candidateSince && now - _candidateSince >= SettleMilliseconds)
            { _order = desired; _candidate = []; }
        }
        return _order.Select((id, index) => (id, index)).ToDictionary(entry => entry.id, entry => entry.index, StringComparer.Ordinal);
    }
}
