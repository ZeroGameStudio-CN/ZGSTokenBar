using System.Security.Cryptography;
using System.Text;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

internal sealed partial class BarForm
{
    private bool DockAccountInUse(string providerId, bool bound = true, bool fallback = false) => bound
        && (_usageProviderActivity.TryGetValue(providerId, out var observed) ? observed is true : fallback);

    private QuotaWindow UnknownDockWindow() => new(_text.IsChinese ? "额度" : "Quota", null, null, TimeSpan.Zero);

    private static bool KnownUsageAccount(UsageAccountSnapshot account) => account.SourceKind != "probe"
        && account.Freshness != UsageFreshness.MissingCredentials && !string.IsNullOrWhiteSpace(account.AccountId);

    private static string DockUsageId(string providerId, string accountId) => providerId + ":account:"
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId)))[..16].ToLowerInvariant();

    private void AddUsageDockAccounts(List<GlassItem> items)
    {
        foreach (var provider in _usageBoard.Providers)
        {
            var view = _pluginMiniCards.FirstOrDefault(item => item.PluginId == provider.ProviderId);
            var extraClaude = provider.ProviderId == UsageProviderIds.Claude && _enabledQuotaProviders.Contains(ProviderKind.Claude);
            if (view is null && !extraClaude) continue;
            view ??= new(provider.ProviderId, provider.DisplayName, UsageProjection.MiniCard(provider, _text.IsChinese),
                new Dictionary<string, string>());
            var accounts = provider.Accounts.Where(KnownUsageAccount)
                .Where(account => provider.ProviderId != UsageProviderIds.Antigravity
                    || account.Windows.Any(window => window.UsedPercent is not null))
                .Where(account => account.SourceKind is not ("quota-card" or "codex-account"))
                .OrderBy(account => account.AccountId, StringComparer.Ordinal).ToArray();
            var icon = GlassPluginIcon(view);
            for (var index = 0; index < accounts.Length; index++)
            {
                var account = accounts[index];
                var projected = UsageQuotaCard(provider, account, view.Title);
                var window = projected?.Window ?? UnknownDockWindow();
                var card = projected?.Card ?? new QuotaCard("usage:" + provider.ProviderId + ":" + account.AccountId,
                    ProviderKind.Claude, view.Title, account.Plan, provider.Accent, true, [window])
                    { HasAccountIdentity = true, CapturedAt = account.SampledAt };
                double? remaining = window.UsedPercent is { } used ? Math.Clamp(100 - used, 0, 100) : null;
                var balance = remaining is null && account.Credits?.PrepaidBalance is { } money
                    ? UsageFormatting.Money(account.Credits.Currency, money) : null;
                var id = accounts.Length == 1 ? provider.ProviderId : DockUsageId(provider.ProviderId, account.AccountId);
                var inUse = DockAccountInUse(provider.ProviderId);
                var label = account.Label.Contains('@') ? CodexAccountFormatting.MaskEmail(account.Label) : account.Label;
                var ring = new GlassDockRing(provider.ProviderId, view.Title, label,
                    accounts.Length > 1 ? index + 1 : 0, remaining, window.ResetsAt, balance, provider.Accent)
                {
                    Warn = account.Warning || account.Freshness is UsageFreshness.Unavailable or UsageFreshness.Stale,
                    ExpectedRemaining = RingPace(remaining, window), InUse = inUse, OverflowOnly = !inUse,
                    ProviderOrder = provider.Order, StableId = DockUsageId(provider.ProviderId, account.AccountId),
                };
                items.Add(balance is not null && projected is null
                    ? new(id, ring, view.Title, icon, null, null, view)
                    : new(id, ring, view.Title, icon, card, window, null, icon));
            }
        }

        // Independent balance plugins have no client-activity signal; keep their signed-in
        // observations reachable in overflow rather than guessing activity from money amounts.
        foreach (var view in _pluginMiniCards)
        {
            if (ProviderCatalog.IsUsageProvider(view.PluginId)
                || view.PluginId is MiniAreaIds.Router or MiniAreaIds.Intake or MiniAreaIds.SystemMetrics or MiniAreaIds.LiveMetrics
                || items.Any(item => item.Id == view.PluginId || item.Ring.ProviderId == view.PluginId)) continue;
            var summary = view.Card.Summary.FirstOrDefault();
            if (summary is null || summary.Status is "unavailable" or "error" or "loading" or "unknown") continue;
            double? remaining = summary.Status == "usage-remaining" ? summary.Value.Number : null;
            var balance = remaining is null && (view.Card.Kind == ContributionKind.Balance || summary.Value.Kind == "currency")
                ? CompactPluginValue(summary.Value) : null;
            if (remaining is null && string.IsNullOrWhiteSpace(balance)) continue;
            var inUse = DockAccountInUse(view.PluginId);
            var ring = new GlassDockRing(view.PluginId, view.Title, view.Title, 0, remaining, null, balance,
                view.Card.AccentToken is { Length: 7 } color && color[0] == '#' ? color : "#8b5cf6")
                { InUse = inUse, OverflowOnly = !inUse, ProviderOrder = view.Card.Order, StableId = view.PluginId };
            items.Add(new(view.PluginId, ring, view.Title, GlassPluginIcon(view), null, null, view));
        }
    }
}
