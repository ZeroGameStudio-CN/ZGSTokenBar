using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>Glass dock: ring items, layered-window rendering, overflow chip and its flyout.</summary>
internal sealed partial class BarForm
{
    private UsageBoard _usageBoard = UsageBoard.Empty;
    private int _glassMaxRings = GlassDockLayout.MaxVisibleRings;
    private ContextMenuStrip? _glassMenu;
    private bool _glassExpanded;
    private bool _glassChipHovered;
    private bool _glassRenderQueued;
    private byte[] _glassFrame = [];
    private byte[] _glassFrameScratch = [];
    private Size _glassFrameSize;
    private RectangleF _glassChipBounds;
    /// <summary>When the hover timer (not a click) opened the overflow; a click right after keeps it open.</summary>
    private HashSet<string> _hiddenDockItems = new(StringComparer.Ordinal);
    /// <summary>Raised when the user expands or collapses the overflow, so the state is saved.</summary>
    public event EventHandler<bool>? GlassExpandedChanged;
    private readonly Dictionary<string, Image?> _glassIcons = new(StringComparer.Ordinal);
    private bool GlassLayered => !_renderOnly;

    /// <summary>Raised after every usage-board update so the pace tracker can sample its windows.</summary>
    internal event EventHandler<UsageBoard>? UsageBoardChanged;

    internal void SetUsageBoard(UsageBoard board)
    {
        _usageBoard = board ?? UsageBoard.Empty;
        UsageBoardChanged?.Invoke(this, _usageBoard);
        Invalidate();
    }

    private static int NormalizeGlassMax(int value) =>
        value <= 0 ? GlassDockLayout.MaxVisibleRings : Math.Clamp(value, 1, 8);

    private sealed record GlassItem(
        string Id,
        GlassDockRing Ring,
        string Name,
        Image? Icon,
        QuotaCard? Card,
        QuotaWindow? Window,
        PluginMiniCardView? Plugin,
        Image? Logo = null);

    /// <summary>
    /// Builds one ring per provider account that currently reports usage. Accounts and plugins
    /// without a remaining percentage or balance (signed out, failed, loading) stay off the bar.
    /// </summary>
    private IReadOnlyList<GlassItem> BuildGlassItems()
    {
        var items = new List<GlassItem>();
        var cards = _snapshot.Cards.Where(card => _activeProviders.Contains(card.Provider)).ToArray();
        var codexCount = cards.Count(card => card.Provider == ProviderKind.Codex && !card.IsService);
        foreach (var card in cards)
        {
            var window = card.Windows.Where(item => item.UsedPercent is not null)
                .OrderByDescending(item => item.UsedPercent)
                .FirstOrDefault();
            double? remaining = window?.UsedPercent is { } used ? Math.Clamp(100 - used, 0, 100) : null;
            var balance = card.Balance is { TotalBalance: { } amount } money
                ? UsageFormatting.Money(money.Currency, amount)
                : null;
            if (remaining is null && balance is null) continue;
            window ??= card.Windows.FirstOrDefault();
            var multi = card.Provider == ProviderKind.Codex && !card.IsService && codexCount > 1;
            var ordinal = multi && int.TryParse(CodexAccountOrdinal(card), out var number) ? number : 0;
            var providerId = card.Provider switch
            {
                ProviderKind.Claude => UsageProviderIds.Claude,
                ProviderKind.Codex => UsageProviderIds.Codex,
                _ => card.Key,
            };
            var title = card.Provider == ProviderKind.Codex ? "Codex" : card.DisplayLabel;
            var ring = new GlassDockRing(
                providerId,
                title,
                card.AccountHint ?? card.DisplayLabel,
                ordinal,
                remaining,
                window?.ResetsAt,
                remaining is null ? balance : null,
                string.IsNullOrWhiteSpace(card.Accent) ? "#10a37f" : card.Accent);
            var name = multi ? GlassAccountName(card, ordinal) : card.DisplayLabel;
            items.Add(new GlassItem("card:" + card.Key, ring, name, ProviderLogo(card.Provider), card, window, null));
        }

        foreach (var view in _pluginMiniCards)
        {
            if (view.PluginId is MiniAreaIds.Router or MiniAreaIds.SystemMetrics or MiniAreaIds.LiveMetrics) continue;
            if (items.Any(item => string.Equals(item.Ring.ProviderId, view.PluginId, StringComparison.Ordinal))) continue;
            var summary = view.Card.Summary.FirstOrDefault();
            if (summary is null) continue;
            double? remaining = summary.Status == "usage-remaining" ? summary.Value.Number : null;
            string? balance = null;
            if (remaining is null
                && (view.Card.Kind == ContributionKind.Balance || summary.Value.Kind == "currency")
                && summary.Status is not ("unavailable" or "error" or "loading" or "unknown"))
            {
                var text = CompactPluginValue(summary.Value);
                if (!string.IsNullOrWhiteSpace(text) && text != "—") balance = text;
            }
            if (remaining is null && balance is null) continue;
            var accent = view.Card.AccentToken is { Length: 7 } token && token[0] == '#' ? token : "#8b5cf6";
            var icon = GlassPluginIcon(view);
            var quota = remaining is not null ? GlassPluginQuota(view) : null;
            var ring = new GlassDockRing(view.PluginId, view.Title, view.Title, 0, remaining, quota?.Window.ResetsAt, balance, accent)
            {
                // The provider layer flags accounts needing attention outside the headline (e.g. another
                // Antigravity group running low); the ring's chip shows it.
                Warn = remaining is not null && ProviderWarning(view.PluginId),
            };
            items.Add(quota is { } scoped
                ? new GlassItem(view.PluginId, ring, view.Title, icon, scoped.Card, scoped.Window, null, icon)
                : new GlassItem(view.PluginId, ring, view.Title, icon, null, null, view));
        }

        return items;
    }

    /// <summary>True when the provider layer flagged any of this provider's accounts (<see cref="UsageAccountSnapshot.Warning"/>).</summary>
    private bool ProviderWarning(string providerId) =>
        _usageBoard.Providers
            .Where(item => string.Equals(item.ProviderId, providerId, StringComparison.Ordinal))
            .SelectMany(item => item.Accounts)
            .Any(account => account.Warning);

    /// <summary>
    /// Projects a usage-board provider (Grok, Cursor, …) onto the same quota card/window shape the
    /// Codex and Claude rings use, so hovering its ring opens the shared per-ring quota popover
    /// scoped to that provider's own account instead of the combined usage board.
    /// </summary>
    private (QuotaCard Card, QuotaWindow Window)? GlassPluginQuota(PluginMiniCardView view)
    {
        var provider = _usageBoard.Providers.FirstOrDefault(item => string.Equals(item.ProviderId, view.PluginId, StringComparison.Ordinal));
        if (provider is null) return null;
        foreach (var account in provider.Accounts)
        {
            if (UsageQuotaCard(provider, account, view.Title) is { } projected) return projected;
        }

        return null;
    }

    /// <summary>
    /// One usage-board account as a quota card. Its key is stable per provider/account, so the pace
    /// tracker (QuotaApplicationContext.ObserveUsagePace) samples the same series the hover reads.
    /// </summary>
    internal static (QuotaCard Card, QuotaWindow Window)? UsageQuotaCard(UsageProviderSnapshot provider, UsageAccountSnapshot account, string title)
    {
        {
            // The headline is the provider's primary window, the same one the ring's % comes from
            // (UsageProjection.MiniCard), so ring and hover always agree; every window is listed below it.
            var window = UsageFormatting.Primary(account) is { UsedPercent: not null, Unlimited: false } primary
                ? primary
                : account.Windows.Where(item => item.UsedPercent is not null && !item.Unlimited)
                    .OrderByDescending(item => item.UsedPercent)
                    .FirstOrDefault();
            if (window is null) return null;
            // Every window is listed; ones the provider highlighted (the ring's group) are starred.
            QuotaWindow Project(UsageWindow item) => new(
                item.Highlighted ? "★ " + item.Label : item.Label,
                item.UsedPercent, item.ResetsAt, item.Duration ?? TimeSpan.Zero);
            var quotaWindow = Project(window);
            var windows = new List<QuotaWindow> { quotaWindow };
            windows.AddRange(account.Windows.Where(item => !ReferenceEquals(item, window) && !item.Unlimited && item.UsedPercent is not null).Select(Project));
            var card = new QuotaCard(
                "usage:" + provider.ProviderId + ":" + account.AccountId,
                ProviderKind.Claude,
                title,
                account.Plan,
                string.IsNullOrWhiteSpace(provider.Accent) ? "#8b5cf6" : provider.Accent,
                true,
                windows)
            {
                CapturedAt = account.SampledAt,
                Subscription = account.Period,
                // Same masked "a***b@domain" form the Codex rings show.
                AccountHint = string.Equals(account.Label, title, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : account.Label?.Contains('@') == true ? CodexAccountFormatting.MaskEmail(account.Label) : account.Label,
            };
            return (card, quotaWindow);
        }
    }

    private static string? CodexAccountOrdinal(QuotaCard card)
    {
        if (card.Provider != ProviderKind.Codex) return null;
        var separator = card.Label.IndexOf('·');
        if (separator < 0) return null;
        return card.Label.AsSpan(separator + 1).Trim().ToString();
    }

    private static string GlassAccountName(QuotaCard card, int ordinal)
    {
        var hint = card.AccountHint;
        if (!string.IsNullOrWhiteSpace(hint)
            && !string.Equals(hint, "Codex account", StringComparison.OrdinalIgnoreCase))
        {
            var at = hint.IndexOf('@');
            return at > 0 ? hint[..at] : hint;
        }

        return ordinal > 0 ? "#" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) : card.DisplayLabel;
    }

    private Image? GlassPluginIcon(PluginMiniCardView view)
    {
        if (_pluginIcons.TryGetValue(view.PluginId, out var pluginIcon)) return pluginIcon;
        if (_glassIcons.TryGetValue(view.PluginId, out var cached)) return cached;
        var resource = ProviderIcons.Resource(view.PluginId);
        Image? image = null;
        if (resource is not null)
        {
            try
            {
                image = LoadEmbeddedImage("ZGSTokenBar.App.Assets." + resource);
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException)
            {
                image = null;
            }
        }
        _glassIcons[view.PluginId] = image;
        return image;
    }

    private (GlassDockPlan Plan, IReadOnlyList<GlassItem> Items) CurrentGlassLayout()
    {
        var items = BuildGlassItems().Where(item => !_hiddenDockItems.Contains(item.Id)).ToArray();
        var rings = items.Select(item => item.Ring).ToArray();
        var plan = GlassDockLayout.Plan(rings, _glassMaxRings);
        if (_glassExpanded)
        {
            if (plan.OverflowCount > 0) plan = GlassDockLayout.PlanExpanded(rings, _glassMaxRings);
            else _glassExpanded = false;
        }
        var byRing = new Dictionary<GlassDockRing, GlassItem>(ReferenceEqualityComparer.Instance);
        foreach (var item in items) byRing[item.Ring] = item;
        var visible = plan.Visible.Select(ring => byRing[ring]).ToArray();
        return (plan, visible);
    }

    private GlassDockPlan CurrentGlassPlan() => CurrentGlassLayout().Plan;

    private void DrawGlassDock(Graphics graphics)
    {
        // The dock is a solid dark card in every theme so rings and labels keep full contrast.
        var light = false;
        var (plan, items) = CurrentGlassLayout();
        _cardBounds.Clear();
        _taskbarWindowBounds.Clear();
        _taskbarPluginBounds.Clear();
        _taskbarCodexAccountBounds.Clear();
        _glassChipBounds = RectangleF.Empty;
        var width = plan.Width;
        var visuals = new List<GlassDockItemVisual>(items.Count);
        var itemIndex = 0;
        for (var index = 0; index < plan.Slots.Count; index++)
        {
            var slot = plan.Slots[index];
            RectangleF bounds = slot.Bounds;
            if (slot.Chip)
            {
                _glassChipBounds = RectangleF.Inflate(bounds, 2, 6);
                continue;
            }
            if (itemIndex >= items.Count) continue;
            var item = items[itemIndex++];
            var hovered = false;
            if (item.Card is { } card && item.Window is { } window)
            {
                var target = new MiniQuotaTarget(bounds, card, window, null, item.Logo);
                _taskbarWindowBounds.Add(target);
                _cardBounds.Add((bounds, card));
                hovered = string.Equals(_hoverQuotaTarget?.Id, target.Id, StringComparison.Ordinal);
            }
            else if (item.Plugin is { } plugin)
            {
                _taskbarPluginBounds.Add((bounds, plugin));
                hovered = string.Equals(_hoverPluginTarget?.Card.PluginId, plugin.PluginId, StringComparison.Ordinal);
            }
            visuals.Add(new GlassDockItemVisual(item.Ring, item.Icon, item.Name, hovered));
        }

        var router = _pluginMiniCards.FirstOrDefault(card => card.PluginId == MiniAreaIds.Router);
        var routerBounds = GlassDockLayout.RouterBoundsFor(width);
        if (router is not null) _taskbarPluginBounds.Add((routerBounds, router));
        var bars = GlassDockLayout.BarsBoundsFor(width);
        _systemUsageBounds = _showSystemMetrics ? RectangleF.Inflate(bars, 4, 3) : RectangleF.Empty;
        var rate = GlassDockLayout.RateBoundsFor(width);
        _liveMetricsBounds = RectangleF.Inflate(rate, 3, 3);
        var scene = new GlassDockScene(
            light,
            width,
            1f / Math.Max(1f, _scale),
            plan,
            visuals,
            _glassExpanded,
            _glassChipHovered,
            router?.Card,
            router is not null && _hoverPluginTarget?.Card.PluginId == MiniAreaIds.Router,
            _systemUsage.CpuPercent,
            _systemUsage.MemoryPercent,
            _systemUsage.DiskActivePercent,
            _systemUsage.GpuPercent,
            _hoverSystemUsage && !_systemAnchorIsLive,
            _systemUsage.CurrentOutputRate,
            _systemUsage.CurrentOutputRate is not null && _systemUsage.LiveRate?.HasEstimate == true,
            _hoverSystemUsage && _systemAnchorIsLive)
        {
            ItemPainted = GlassItemPainted,
        };
        _glassFailedThisPaint.Clear();
        GlassDockPainter.Draw(graphics, scene, _badgeFont);
        // A ring that painted cleanly this time rejoins (its earlier fault is forgotten).
        _taskbarRenderFailureAreaIds.RemoveWhere(id => id != "bar" && !_glassFailedThisPaint.Contains(id));
    }

    /// <summary>One ring's paint result: faults are isolated (traced, ring left blank) and clear on the next good paint.</summary>
    private readonly HashSet<string> _glassFailedThisPaint = new(StringComparer.Ordinal);

    private bool GlassItemPainted(string providerId, Exception? error)
    {
        if (error is null)
        {
            if (_renderFaultForAcceptance?.Invoke(providerId) is { } fault) throw fault;
            return true;
        }

        if (!IsRecoverableRenderException(error)) return false;
        _glassFailedThisPaint.Add(providerId);
        TraceTaskbarRenderFailure(providerId, error);
        return true;
    }

    /// <summary>Expands or collapses the overflow. Only a click changes it; the state persists.</summary>
    private void SetGlassExpanded(bool expanded, bool notify = false)
    {
        if (_glassExpanded == expanded) return;
        _glassExpanded = expanded;
        using var total = PerfTrace.Measure(expanded ? "glass.expand" : "glass.collapse");
        using (PerfTrace.Measure("glass.expand.hide-popovers")) HidePopovers();
        using (PerfTrace.Measure("glass.expand.layout")) ApplySnapshotLayout();
        Invalidate();
        if (notify) GlassExpandedChanged?.Invoke(this, expanded);
        if (PerfTrace.Enabled) BeginInvoke(() => PerfTrace.Note("glass.expand.first-frame-posted"));
    }

    private void QueueGlassRender()
    {
        if (_glassRenderQueued || !IsHandleCreated || IsDisposed) return;
        _glassRenderQueued = true;
        BeginInvoke(RenderGlassLayered);
    }

    /// <summary>
    /// Renders the pill into a per-pixel-alpha bitmap and hands it to UpdateLayeredWindow, so the
    /// rounded edge is anti-aliased against the real backdrop instead of a clipped region colour.
    /// </summary>
    private void RenderGlassLayered()
    {
        _glassRenderQueued = false;
        if (!IsHandleCreated || IsDisposed || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var trace = PerfTrace.Measure("glass.render");
        using var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
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

        // Most ticks repaint identical pixels; skipping the HBITMAP/UpdateLayeredWindow round trip
        // for unchanged frames removes the bulk of the bar's idle kernel time.
        if (!CaptureGlassFrame(bitmap)) return;
        using (PerfTrace.Measure("glass.render.update-layered"))
            if (!LayeredWindow.Update(Handle, bitmap)) _glassFrameSize = Size.Empty;
        if (PerfTrace.Enabled)
            PerfTrace.Note($"glass.bounds chip={Rectangle.Round(ScaleRect(_glassChipBounds))} router={Rectangle.Round(ScaleRect(GlassDockLayout.RouterBoundsFor(CurrentGlassPlan().Width)))} client={ClientSize}");
    }

    private RectangleF ScaleRect(RectangleF logical) =>
        new(logical.X * _scale, logical.Y * _scale, logical.Width * _scale, logical.Height * _scale);

    /// <summary>Returns true when the rendered frame differs from the last one handed to the window.</summary>
    private bool CaptureGlassFrame(Bitmap bitmap)
    {
        var rectangle = new Rectangle(Point.Empty, bitmap.Size);
        var data = bitmap.LockBits(rectangle, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var length = Math.Abs(data.Stride) * data.Height;
            if (_glassFrameScratch.Length != length) _glassFrameScratch = new byte[length];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, _glassFrameScratch, 0, length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        if (_glassFrameSize == bitmap.Size && _glassFrame.AsSpan().SequenceEqual(_glassFrameScratch)) return false;
        (_glassFrame, _glassFrameScratch) = (_glassFrameScratch, _glassFrame);
        _glassFrameSize = bitmap.Size;
        return true;
    }

    private void ShowGlassMenu(Point location)
    {
        _glassMenu ??= new ContextMenuStrip();
        _glassMenu.Items.Clear();
        _glassMenu.Items.Add(_text.IsChinese ? "刷新" : "Refresh", null, (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty));
        _glassMenu.Items.Add(_text.IsChinese ? "设置" : "Settings", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        _glassMenu.Items.Add(_text.IsChinese ? "用量面板" : "Usage panel", null, (_, _) => OverviewRequested?.Invoke(this, EventArgs.Empty));
        _glassMenu.Items.Add(_text.IsChinese ? "显示/隐藏项目（设置）…" : "Show/hide items (Settings)…", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        _glassMenu.Show(this, location);
    }
}
