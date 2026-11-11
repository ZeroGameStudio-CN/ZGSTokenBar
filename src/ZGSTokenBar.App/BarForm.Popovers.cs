using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>Popover management: hover hints and the quota, Codex accounts, radar and system-usage popovers.</summary>
internal sealed partial class BarForm
{
    private void UpdateHintHover(HoverTarget target)
    {
        if (_popoverPinned) return;
        if (target == HoverTarget.Plugin && _pluginPopover?.Visible == true
            && _hoverPluginTarget?.Card.PluginId == _pluginPopoverId) return;
        var next = target is HoverTarget.Refresh or HoverTarget.MiniCollapse or HoverTarget.MiniReorder
            or HoverTarget.Settings or HoverTarget.Plugin
            ? target
            : HoverTarget.None;
        if (_hoverHintTarget == next)
        {
            if (next != HoverTarget.None && _hintPopover?.Visible != true)
            {
                _popoverHoverTimer.Start();
            }
            return;
        }

        _popoverHoverTimer.Stop();
        _hoverHintTarget = next;
        if (next == HoverTarget.None)
        {
            HideHintPopover();
            return;
        }

        HideSystemUsagePopover();
        HideRadarPopover();
        HideCodexAccountsPopover();
        if (_quotaPopover?.Visible == true) HideQuotaPopover();
        _popoverHoverTimer.Start();
    }

    private (string Title, string Detail)? HintFor(HoverTarget target)
    {
        if (target == HoverTarget.MiniCollapse)
        {
            var area = HoveredMiniArea();
            if (area is null) return null;
            return (area.Title, _text.MiniCardCollapseHint(area.Collapsed));
        }
        if (target == HoverTarget.MiniReorder)
        {
            var area = HoveredReorderArea();
            return area is null ? null : (area.Title, _text.MiniCardReorderHint);
        }
        if (target == HoverTarget.Settings)
        {
            return (_text.Settings, _text.OpenSettingsHint);
        }
        if (target == HoverTarget.Plugin && _hoverPluginTarget is { } plugin)
        {
            var lines = plugin.Card.Card.Summary
                .Take(6)
                .Select(item =>
                {
                    var label = plugin.Card.Text.TryGetValue(item.LabelKey, out var localized)
                        ? localized
                        : item.LabelKey;
                    return $"{label}: {PluginValue(item.Value)}";
                })
                .ToArray();
            return (
                plugin.Card.Title,
                lines.Length == 0 ? plugin.Card.PluginId : string.Join("\n", lines));
        }
        if (target != HoverTarget.Refresh) return null;

        var failures = _snapshot.Health
            .Where(health => _activeProviders.Contains(health.Provider))
            .Where(health => !health.Connected)
            .Select(health => _text.Health(health, DateTimeOffset.UtcNow))
            .ToArray();
        if (!_refreshing && failures.Length > 0)
        {
            return (_text.RefreshNow, string.Join("\n", failures));
        }

        var age = DateTimeOffset.UtcNow - _snapshot.CapturedAt;
        return (_text.RefreshNow, _refreshing ? _text.RefreshingLiveLimits : _text.RefreshUpdatedDetail(age));
    }

    private void UpdateSystemUsageHover(bool hovered)
    {
        if (_popoverPinned) return;
        if (!hovered && _systemUsagePopover?.Visible == true)
        {
            _popoverLeaveStarted ??= DateTime.UtcNow;
            return;
        }
        if (_hoverSystemUsage == hovered)
        {
            if (hovered) _popoverLeaveStarted = null;
            return;
        }

        _popoverHoverTimer.Stop();
        _hoverSystemUsage = hovered;
        if (!hovered) return;
        HideQuotaPopover();
        HideRadarPopover();
        _hoverSystemUsage = true;
        SystemUsageDetailsRequested?.Invoke(this, EventArgs.Empty);
        _popoverHoverTimer.Start();
    }

    private void UpdateRadarHover(MiniRadarTarget? target)
    {
        if (_popoverPinned) return;
        if (target is null && _radarPopover?.Visible == true)
        {
            _popoverLeaveStarted ??= DateTime.UtcNow;
            return;
        }
        var previousId = _hoverRadarTarget?.Id;
        var nextId = target?.Id;
        if (target is not null
            && _radarPopover?.Visible == true
            && string.Equals(previousId, nextId, StringComparison.Ordinal))
        {
            _hoverRadarTarget = target;
            _popoverLeaveStarted = null;
            return;
        }
        if (string.Equals(previousId, nextId, StringComparison.Ordinal))
        {
            if (target is not null) _popoverLeaveStarted = null;
            return;
        }

        _popoverHoverTimer.Stop();
        _hoverRadarTarget = target;
        if (target is null) return;
        HideSystemUsagePopover();
        HideQuotaPopover();
        _hoverRadarTarget = target;
        _popoverHoverTimer.Start();
    }

    private void UpdateCodexAccountsHover((RectangleF Bounds, QuotaCard Card)? target)
    {
        if (_popoverPinned) return;
        var previousKey = _hoverCodexAccountTarget?.Card.Key;
        var nextKey = target?.Card.Key;
        if (target is not null
            && _codexAccountsPopover?.Visible == true
            && string.Equals(previousKey, nextKey, StringComparison.Ordinal))
        {
            _hoverCodexAccountTarget = target;
            return;
        }
        if (string.Equals(previousKey, nextKey, StringComparison.Ordinal)) return;

        _popoverHoverTimer.Stop();
        _hoverCodexAccountTarget = target;
        EnsureAnimationTimer();
        Invalidate();
        if (target is null)
        {
            HideCodexAccountsPopover();
            return;
        }

        HideSystemUsagePopover();
        HideRadarPopover();
        if (_quotaPopover?.Visible == true) HideQuotaPopover();
        _popoverHoverTimer.Start();
    }

    private void UpdateQuotaHover(MiniQuotaTarget? target)
    {
        if (_popoverPinned) return;
        var previousId = _hoverQuotaTarget?.Id;
        var nextId = target?.Id;
        if (target is not null
            && _quotaPopover?.Visible == true
            && string.Equals(_popoverTargetId, nextId, StringComparison.Ordinal))
        {
            _hoverQuotaTarget = target;
            _popoverLeaveStarted = null;
            return;
        }
        if (string.Equals(previousId, nextId, StringComparison.Ordinal))
        {
            if (target is not null) _popoverLeaveStarted = null;
            return;
        }

        _popoverHoverTimer.Stop();
        _hoverQuotaTarget = target;
        EnsureAnimationTimer();
        Invalidate();
        if (target is null) return;
        HideSystemUsagePopover();
        HideRadarPopover();
        if (_quotaPopover?.Visible == true) HideQuotaPopover();
        _hoverQuotaTarget = target;
        _popoverHoverTimer.Start();
    }

    private void TogglePinnedSystemUsagePopover()
    {
        if (_popoverPinned && _systemUsagePopover?.Visible == true && _hoverSystemUsage)
        {
            HideSystemUsagePopover();
            return;
        }
        ShowSystemUsagePopover(pinned: true);
    }

    private void ShowSystemUsagePopover(bool pinned)
    {
        HideCodexStrategyPopover();
        HidePluginPopover();
        var needsDetails = !_systemAnchorIsLive && _systemUsage.TopProcesses.Count == 0;
        _popoverHoverTimer.Stop();
        if (_hintPopover?.Visible == true || _hoverHintTarget != HoverTarget.None) HideHintPopover();
        if (_codexAccountsPopover?.Visible == true) HideCodexAccountsPopover();
        if (_quotaPopover?.Visible == true || _popoverTargetId is not null) HideQuotaPopover();
        if (_radarPopover?.Visible == true || _hoverRadarTarget is not null) HideRadarPopover();
        _systemUsagePopover ??= new SystemUsagePopoverForm();
        _systemUsagePopover.ApplyTheme(_backgroundTheme);
        _systemUsagePopover.ShowFor(
            this,
            QuotaTargetScreenBounds(SystemUsageAnchorBounds),
            SystemPopoverContent(_systemUsage, pinned),
            _text,
            _scale,
            _animationsEnabled);
        _hoverSystemUsage = true;
        _hoverRadarTarget = null;
        _popoverTargetId = null;
        _popoverPinned = pinned;
        _popoverLeaveStarted = null;
        _popoverMouseWasDown = Control.MouseButtons != MouseButtons.None;
        _popoverEscapeWasDown = IsEscapeDown();
        _popoverStateTimer.Start();
        Invalidate();
        if (needsDetails) SystemUsageDetailsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void MonitorSystemUsagePopover()
    {
        if (_systemUsagePopover?.Visible != true || !_hoverSystemUsage)
        {
            _popoverStateTimer.Stop();
            return;
        }

        var cursor = Cursor.Position;
        var anchor = QuotaTargetScreenBounds(SystemUsageAnchorBounds);
        var mouseDown = Control.MouseButtons != MouseButtons.None;
        var escapeDown = IsEscapeDown();
        if (_popoverPinned)
        {
            var outsideClick = mouseDown
                && !_popoverMouseWasDown
                && !anchor.Contains(cursor)
                && !_systemUsagePopover.Bounds.Contains(cursor);
            var escapePressed = escapeDown && !_popoverEscapeWasDown;
            _popoverMouseWasDown = mouseDown;
            _popoverEscapeWasDown = escapeDown;
            if (outsideClick || escapePressed) HideSystemUsagePopover();
            return;
        }

        if (anchor.Contains(cursor) || _systemUsagePopover.Bounds.Contains(cursor))
        {
            _popoverLeaveStarted = null;
            return;
        }

        _popoverLeaveStarted ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _popoverLeaveStarted >= TimeSpan.FromMilliseconds(150))
        {
            HideSystemUsagePopover();
        }
    }

    private void ShowHoveredPopover()
    {
        if (_hoverCodexStrategy && ResolveCodexStrategyBounds() is { IsEmpty: false } strategyBounds
            && QuotaTargetScreenBounds(strategyBounds).Contains(Cursor.Position))
        {
            _popoverHoverTimer.Stop();
            if (!_popoverPinned) ShowCodexStrategyPopover(pinned: false);
            return;
        }
        if (_hoverSystemUsage)
        {
            _popoverHoverTimer.Stop();
            if (!QuotaTargetScreenBounds(SystemUsageAnchorBounds).Contains(Cursor.Position)) return;
            ShowSystemUsagePopover(pinned: false);
            return;
        }
        if (_hoverRadarTarget is { } radarTarget)
        {
            _popoverHoverTimer.Stop();
            if (!QuotaTargetScreenBounds(radarTarget.Bounds).Contains(Cursor.Position)) return;
            ShowRadarPopover(radarTarget, pinned: false);
            return;
        }
        if (_hoverCodexAccountTarget is { } codexAccountTarget)
        {
            _popoverHoverTimer.Stop();
            if (!QuotaTargetScreenBounds(codexAccountTarget.Bounds).Contains(Cursor.Position)) return;
            ShowCodexAccountsPopover(codexAccountTarget);
            return;
        }
        if (_hoverHintTarget is HoverTarget.Refresh or HoverTarget.MiniCollapse or HoverTarget.Settings
            or HoverTarget.Plugin)
        {
            var hintTarget = _hoverHintTarget;
            _popoverHoverTimer.Stop();
            var hintBounds = hintTarget switch
            {
                HoverTarget.Refresh => _refreshBounds,
                HoverTarget.MiniCollapse when HoveredMiniArea() is { } area => area.HandleBounds,
                HoverTarget.Plugin when _hoverPluginTarget is { } plugin => plugin.Bounds,
                _ => _settingsBounds,
            };
            if (!QuotaTargetScreenBounds(hintBounds).Contains(Cursor.Position)) return;
            ShowHintPopover(hintTarget);
            return;
        }
        ShowHoveredQuotaPopover();
    }

    private void ShowCodexAccountsPopover((RectangleF Bounds, QuotaCard Card) target)
    {
        if (_popoverPinned) return;
        HideCodexStrategyPopover();
        HidePluginPopover();
        _popoverHoverTimer.Stop();
        if (_hintPopover?.Visible == true || _hoverHintTarget != HoverTarget.None) HideHintPopover();
        if (_systemUsagePopover?.Visible == true || _hoverSystemUsage) HideSystemUsagePopover();
        if (_radarPopover?.Visible == true || _hoverRadarTarget is not null) HideRadarPopover();
        if (_quotaPopover?.Visible == true) HideQuotaPopover();
        _codexAccountsPopover ??= new CodexAccountsPopoverForm();
        _codexAccountsPopover.ApplyTheme(_backgroundTheme);
        _codexAccountsPopover.ShowFor(
            this,
            QuotaTargetScreenBounds(target.Bounds),
            _codexAccounts,
            target.Card,
            _text,
            _scale,
            _animationsEnabled,
            _snapshot.CodexAccounts);
        Invalidate();
    }

    private void ShowHintPopover(HoverTarget target)
    {
        _popoverHoverTimer.Stop();
        if (_popoverPinned) return;
        HideCodexStrategyPopover();
        if (target == HoverTarget.Plugin && _hoverPluginTarget is { } requestedPlugin
            && RequestPluginPopover(requestedPlugin.Card.PluginId, QuotaTargetScreenBounds(requestedPlugin.Bounds), pinned: false)) return;
        var content = HintFor(target);
        if (content is null) return;
        HideSystemUsagePopover();
        HideRadarPopover();
        HideCodexAccountsPopover();
        if (_quotaPopover?.Visible == true) HideQuotaPopover();
        _hintPopover ??= new TaskbarHintPopoverForm();
        _hintPopover.ApplyTheme(_backgroundTheme);
        var anchor = target switch
        {
            HoverTarget.Refresh => _refreshBounds,
            HoverTarget.MiniCollapse when HoveredMiniArea() is { } area => area.HandleBounds,
            HoverTarget.MiniReorder when HoveredReorderArea() is { } area => area.ReorderBounds,
            HoverTarget.Plugin when _hoverPluginTarget is { } plugin => plugin.Bounds,
            _ => _settingsBounds,
        };
        _hintPopover.ShowFor(
            this,
            QuotaTargetScreenBounds(anchor),
            content.Value.Title,
            content.Value.Detail,
            _scale,
            _animationsEnabled);
        Invalidate();
    }

    private void ShowHoveredQuotaPopover()
    {
        _popoverHoverTimer.Stop();
        if (_popoverPinned || _hoverQuotaTarget is not { } target) return;
        if (!QuotaTargetScreenBounds(target.Bounds).Contains(Cursor.Position)) return;
        ShowQuotaPopover(target, pinned: false);
    }

    private void TogglePinnedQuotaPopover(MiniQuotaTarget target)
    {
        if (_popoverPinned && string.Equals(_popoverTargetId, target.Id, StringComparison.Ordinal))
        {
            HideQuotaPopover();
            return;
        }
        ShowQuotaPopover(target, pinned: true);
    }

    private void ShowQuotaPopover(MiniQuotaTarget target, bool pinned)
    {
        HideCodexStrategyPopover();
        HidePluginPopover();
        _popoverHoverTimer.Stop();
        if (_codexAccountsPopover?.Visible == true) HideCodexAccountsPopover();
        if (_hintPopover?.Visible == true || _hoverHintTarget != HoverTarget.None) HideHintPopover();
        if (_systemUsagePopover?.Visible == true || _hoverSystemUsage) HideSystemUsagePopover();
        if (_radarPopover?.Visible == true || _hoverRadarTarget is not null) HideRadarPopover();
        _quotaPopover ??= new QuotaPopoverForm();
        _quotaPopover.ApplyTheme(_backgroundTheme);
        var anchor = QuotaTargetScreenBounds(target.Bounds);
        var logo = target.Logo ?? ProviderLogo(target.Card.Provider);
        _quotaPopover.ShowFor(
            this,
            anchor,
            QuotaPopoverContentFor(target, pinned, DateTimeOffset.UtcNow),
            _text,
            logo,
            _resetClockIcon,
            _scale,
            _animationsEnabled);
        _popoverTargetId = target.Id;
        _popoverPinned = pinned;
        _popoverLeaveStarted = null;
        _popoverMouseWasDown = Control.MouseButtons != MouseButtons.None;
        _popoverEscapeWasDown = IsEscapeDown();
        _popoverStateTimer.Start();
        Invalidate();
    }

    /// <summary>Content of the shared quota card for one ring (live hover and offscreen captures).</summary>
    private QuotaPopoverContent QuotaPopoverContentFor(MiniQuotaTarget target, bool pinned, DateTimeOffset now) => new(
        target.Card,
        target.Window,
        QuotaDisplayFormatting.WeeklyBlockReset(target.Card, target.Window, now),
        target.Card.CapturedAt ?? _snapshot.CapturedAt,
        _quotaPaceEstimates.GetValueOrDefault(target.PaceKey),
        pinned,
        target.Card.Provider == ProviderKind.AiGateway ? _aiGatewayUsage : null,
        target.Card.Provider == ProviderKind.Codex
            ? _codexQuotaTokenSummaries.GetValueOrDefault(target.PaceKey)
            : null);

    /// <summary>Set from ZGSTOKENBAR_CAPTURE_HOVERS: render each ring's hover card offscreen once live data arrives.</summary>
    internal static string? PendingHoverCaptureDirectory { get; set; }
    private System.Windows.Forms.Timer? _hoverCaptureTimer;
    private DateTime _hoverCaptureDeadline;

    private void StartHoverCaptureIfRequested()
    {
        if (_renderOnly || PendingHoverCaptureDirectory is null || _hoverCaptureTimer is not null) return;
        _hoverCaptureDeadline = DateTime.UtcNow.AddMinutes(3);
        _hoverCaptureTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _hoverCaptureTimer.Tick += (_, _) =>
        {
            var items = BuildGlassItems().Where(item => item.Card is not null && item.Window is not null).ToArray();
            var codex = items.Any(item => item.Card!.Provider == ProviderKind.Codex);
            var other = items.Any(item => item.Card!.Provider != ProviderKind.Codex);
            if (!(codex && other) && DateTime.UtcNow < _hoverCaptureDeadline) return;
            _hoverCaptureTimer!.Stop();
            try
            {
                WriteHoverCaptures(PendingHoverCaptureDirectory!, items);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                System.Diagnostics.Trace.WriteLine("ZGSTokenBar: hover capture failed: " + error.Message);
            }
            PendingHoverCaptureDirectory = null;
        };
        _hoverCaptureTimer.Start();
    }

    private void WriteHoverCaptures(string directory, IReadOnlyList<GlassItem> items)
    {
        Directory.CreateDirectory(directory);
        var now = DateTimeOffset.UtcNow;
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var name = item.Card!.Provider == ProviderKind.Codex ? "codex" : item.Ring.ProviderId.Split('.', ':').Last();
            if (!written.Add(name)) continue;
            var hint = item.Card.AccountHint;
            var card = hint?.Contains('@') == true ? item.Card with { AccountHint = CodexAccountFormatting.MaskEmail(hint) } : item.Card;
            var target = new MiniQuotaTarget(RectangleF.Empty, card, item.Window!, null, item.Logo);
            using var popover = new QuotaPopoverForm();
            using var bitmap = popover.RenderForTest(
                QuotaPopoverContentFor(target, pinned: false, now) with { Card = card },
                _text,
                item.Logo ?? ProviderLogo(card.Provider),
                _resetClockIcon,
                192,
                now);
            bitmap.Save(Path.Combine(directory, $"hover-{name}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        File.WriteAllText(Path.Combine(directory, "hover-done.txt"), string.Join(",", written));
    }

    private void MonitorQuotaPopover()
    {
        if (_quotaPopover?.Visible != true || _popoverTargetId is null)
        {
            _popoverStateTimer.Stop();
            return;
        }

        var cursor = Cursor.Position;
        var anchor = ResolveQuotaTarget(_popoverTargetId) is { } target
            ? QuotaTargetScreenBounds(target.Bounds)
            : Rectangle.Empty;
        var mouseDown = Control.MouseButtons != MouseButtons.None;
        var escapeDown = IsEscapeDown();
        if (_popoverPinned)
        {
            var outsideClick = mouseDown
                && !_popoverMouseWasDown
                && !anchor.Contains(cursor)
                && !_quotaPopover.Bounds.Contains(cursor);
            var escapePressed = escapeDown && !_popoverEscapeWasDown;
            _popoverMouseWasDown = mouseDown;
            _popoverEscapeWasDown = escapeDown;
            if (outsideClick || escapePressed) HideQuotaPopover();
            return;
        }

        if (anchor.Contains(cursor) || _quotaPopover.Bounds.Contains(cursor))
        {
            _popoverLeaveStarted = null;
            return;
        }

        _popoverLeaveStarted ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _popoverLeaveStarted >= TimeSpan.FromMilliseconds(150)) HideQuotaPopover();
    }

    private void TogglePinnedRadarPopover(MiniRadarTarget target)
    {
        var sameVisibleTarget = _radarPopover?.Visible == true
            && string.Equals(_hoverRadarTarget?.Id, target.Id, StringComparison.Ordinal);
        if (_popoverPinned && sameVisibleTarget)
        {
            HideRadarPopover();
            return;
        }
        ShowRadarPopover(target, pinned: true, requestRefresh: !sameVisibleTarget);
    }

    private void ShowRadarPopover(
        MiniRadarTarget target,
        bool pinned,
        bool requestRefresh = true)
    {
        HideCodexStrategyPopover();
        HidePluginPopover();
        _popoverHoverTimer.Stop();
        if (_codexAccountsPopover?.Visible == true) HideCodexAccountsPopover();
        if (_hintPopover?.Visible == true || _hoverHintTarget != HoverTarget.None) HideHintPopover();
        if (_systemUsagePopover?.Visible == true || _hoverSystemUsage) HideSystemUsagePopover();
        if (_quotaPopover?.Visible == true || _popoverTargetId is not null) HideQuotaPopover();
        var radarEnabled = _radarProviders.Contains(target.SourceProvider);
        if (requestRefresh && radarEnabled && (target.DeepSeekOnly || _radarPopover?.RadarExtensionVisible == true))
        {
            RadarPreviewRequested?.Invoke(
                this,
                new RadarPreviewRequest(target.SourceProvider, target.SurfaceId));
        }
        if (_radarPopover is null)
        {
            _radarPopover = new ProviderRadarPopoverForm();
            _radarPopover.RadarExtensionRequested += (_, _) =>
            {
                if (_hoverRadarTarget is { } current)
                    RadarPreviewRequested?.Invoke(this, new RadarPreviewRequest(current.SourceProvider, current.SurfaceId));
            };
            _radarPopover.RestoreModelGroups(_radarModelGroups);
            _radarPopover.ModelGroupsChanged += (_, _) =>
            {
                _radarModelGroups = AppSettings.CopyRadarModelGroups(_radarPopover.ModelGroups);
                RadarModelGroupsChanged?.Invoke(this, EventArgs.Empty);
            };
            _radarPopover.SpendHistoryRequested += (_, _) => PinVisibleRadarPopoverForHistory();
            _radarPopover.ModelGroupInteraction += (_, _) => PinVisibleRadarPopoverForHistory();
            _radarPopover.DetailsInteraction += (_, _) => PinVisibleRadarPopoverForHistory();
        }
        var tokenUsage = target.DeepSeekOnly
            ? null
            : target.SourceProvider == ProviderKind.Codex
                ? _codexTokenUsage
                : null;
        var aiGatewayUsage = target.DeepSeekOnly ? _aiGatewayUsage : null;
        _radarPopover.ShowFor(
            this,
            QuotaTargetScreenBounds(target.Bounds),
            _radarState,
            _text,
            ProviderLogo(target.Provider),
            _scale,
            _animationsEnabled,
            pinned,
            radarEnabled,
            tokenUsage,
            aiGatewayUsage,
            target.DeepSeekOnly);
        _hoverRadarTarget = target;
        _popoverTargetId = null;
        _popoverPinned = pinned;
        _popoverLeaveStarted = null;
        _popoverMouseWasDown = Control.MouseButtons != MouseButtons.None;
        _popoverEscapeWasDown = IsEscapeDown();
        _popoverStateTimer.Start();
        Invalidate();
    }

    private void PinVisibleRadarPopoverForHistory()
    {
        if (_hoverRadarTarget is not { } target) return;
        ShowRadarPopover(
            ResolveRadarTarget(target.Id) ?? target,
            pinned: true,
            requestRefresh: false);
    }

    private void MonitorPopover()
    {
        if (_codexStrategyPopover?.IsOpen == true)
        {
            MonitorCodexStrategyPopover();
            return;
        }
        if (_pluginPopover is { Visible: false } && _pluginPopoverId is not null) HidePluginPopover();
        if (_pluginPopover?.Visible == true)
        {
            MonitorPluginPopover();
            return;
        }
        if (_systemUsagePopover?.Visible == true && _hoverSystemUsage)
        {
            MonitorSystemUsagePopover();
            return;
        }
        if (_radarPopover?.Visible == true && _hoverRadarTarget is not null)
        {
            MonitorRadarPopover();
            return;
        }
        MonitorQuotaPopover();
    }

    private void MonitorRadarPopover()
    {
        if (_hoverRadarTarget is not { } target || _radarPopover?.Visible != true)
        {
            _popoverStateTimer.Stop();
            return;
        }
        var currentTarget = ResolveRadarTarget(target.Id);
        if (currentTarget is null)
        {
            HideRadarPopover();
            return;
        }
        _hoverRadarTarget = currentTarget;
        var cursor = Cursor.Position;
        var anchor = QuotaTargetScreenBounds(currentTarget.Bounds);
        var mouseDown = Control.MouseButtons != MouseButtons.None;
        var escapeDown = IsEscapeDown();
        if (_popoverPinned)
        {
            var outsideClick = mouseDown
                && !_popoverMouseWasDown
                && !anchor.Contains(cursor)
                && !_radarPopover.ContainsScreenPoint(cursor);
            var escapePressed = escapeDown && !_popoverEscapeWasDown;
            _popoverMouseWasDown = mouseDown;
            _popoverEscapeWasDown = escapeDown;
            if (outsideClick || escapePressed) HideRadarPopover();
            return;
        }
        if (anchor.Contains(cursor) || _radarPopover.ContainsScreenPoint(cursor))
        {
            _popoverLeaveStarted = null;
            return;
        }
        _popoverLeaveStarted ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _popoverLeaveStarted >= TimeSpan.FromMilliseconds(150))
        {
            HideRadarPopover();
        }
    }

    private void RefreshQuotaPopover()
    {
        if (_quotaPopover?.Visible != true || _popoverTargetId is null) return;
        var target = ResolveQuotaTarget(_popoverTargetId);
        if (target is null)
        {
            HideQuotaPopover();
            return;
        }
        ShowQuotaPopover(target, _popoverPinned);
    }

    private void RefreshCodexAccountsPopover()
    {
        if (_codexAccountsPopover?.Visible != true || _hoverCodexAccountTarget is not { } target) return;
        ShowCodexAccountsPopover(target);
    }

    private void RefreshHintPopover()
    {
        if (_hintPopover?.Visible != true) return;
        if (_hoverHintTarget is not (HoverTarget.Refresh or HoverTarget.MiniCollapse or HoverTarget.Settings
            or HoverTarget.Plugin)) return;
        ShowHintPopover(_hoverHintTarget);
    }

    private void RepositionPopovers()
    {
        if (_codexStrategyPopover?.IsOpen == true) UpdateCodexStrategyContent();
        if (_pluginPopover?.Visible == true && _pluginPopoverId is { } pluginId) OpenPluginPopover(pluginId, _popoverPinned);
        if (_systemUsagePopover?.Visible == true) ShowSystemUsagePopover(_popoverPinned);
        if (_quotaPopover?.Visible == true) RefreshQuotaPopover();
        if (_codexAccountsPopover?.Visible == true) RefreshCodexAccountsPopover();
        if (_radarPopover?.Visible == true && _hoverRadarTarget is { } target)
        {
            ShowRadarPopover(target, _popoverPinned, requestRefresh: false);
        }
    }

    private MiniRadarTarget? ResolveRadarTarget(string id) =>
        _taskbarRadarBounds.FirstOrDefault(target =>
            string.Equals(target.Id, id, StringComparison.Ordinal));

    private MiniQuotaTarget? ResolveQuotaTarget(string id)
    {
        if (_glassDock) return _taskbarWindowBounds.FirstOrDefault(target => string.Equals(target.Id, id, StringComparison.Ordinal));
        var x = TaskbarMiniLayoutMath.OuterPadding;
        foreach (var area in _visibleTaskbarAreas)
        {
            var areaWidth = TaskbarAreaWidth(area);
            if (area.Group is not { } group)
            {
                x += areaWidth + TaskbarMiniLayoutMath.ModuleGap;
                continue;
            }
            var layout = AreaLayout(area.AreaId);
            var cardBounds = CodexQuotaBounds(area.AreaId,
                TaskbarAreaContentBounds(new RectangleF(x, 4, areaWidth, 36)), layout.Collapsed);
            if (layout.Collapsed)
            {
                x += areaWidth + TaskbarMiniLayoutMath.ModuleGap;
                continue;
            }
            if (group.IsCodexPool)
            {
                foreach (var target in TaskbarCodexPoolTargetRows(
                             cardBounds,
                             group.Cards,
                             DateTimeOffset.UtcNow)
                             .SelectMany(row => row.Targets))
                {
                    if (string.Equals(target.Id, id, StringComparison.Ordinal)) return target;
                }
            }
            else if (group.IsStackedCodex)
            {
                for (var index = 0; index < group.Cards.Count; index++)
                {
                    var rowBounds = TaskbarCodexCapsuleBounds(cardBounds, index, group.Cards.Count);
                    if (group.Cards.Count > 2)
                    {
                        var target = TaskbarCompactCodexRowTarget(group.Cards[index], rowBounds);
                        if (string.Equals(target.Id, id, StringComparison.Ordinal)) return target;
                        continue;
                    }
                    foreach (var target in TaskbarCodexRowTargets(group.Cards[index], rowBounds))
                    {
                        if (string.Equals(target.Id, id, StringComparison.Ordinal)) return target;
                    }
                }
            }
            else
            {
                foreach (var target in TaskbarQuotaTargets(group.Cards[0], cardBounds))
                {
                    if (string.Equals(target.Id, id, StringComparison.Ordinal)) return target;
                }
            }
            x += areaWidth + TaskbarMiniLayoutMath.ModuleGap;
        }
        return null;
    }

    private Rectangle QuotaTargetScreenBounds(RectangleF logicalBounds)
    {
        var topLeft = PointToScreen(new Point(
            (int)Math.Round(logicalBounds.Left * _scale),
            (int)Math.Round(logicalBounds.Top * _scale)));
        var bottomRight = PointToScreen(new Point(
            (int)Math.Round(logicalBounds.Right * _scale),
            (int)Math.Round(logicalBounds.Bottom * _scale)));
        return Rectangle.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
    }

    private void HideQuotaPopover()
    {
        _popoverHoverTimer.Stop();
        _popoverStateTimer.Stop();
        _quotaPopover?.HidePopover();
        _popoverPinned = false;
        _hoverQuotaTarget = null;
        _popoverTargetId = null;
        _popoverLeaveStarted = null;
        EnsureAnimationTimer();
        Invalidate();
    }

    private void HideCodexAccountsPopover()
    {
        _codexAccountsPopover?.HidePopover();
        _hoverCodexAccountTarget = null;
        EnsureAnimationTimer();
        Invalidate();
    }

    private void HideHintPopover()
    {
        _hintPopover?.HidePopover();
        _hoverHintTarget = HoverTarget.None;
        EnsureAnimationTimer();
        Invalidate();
    }

    private void HideRadarPopover()
    {
        var ownsPin = _hoverRadarTarget is not null && _popoverTargetId is null;
        _popoverHoverTimer.Stop();
        _radarPopover?.HidePopover();
        if (ownsPin) _popoverPinned = false;
        _hoverRadarTarget = null;
        _popoverLeaveStarted = null;
        if (_quotaPopover?.Visible != true) _popoverStateTimer.Stop();
    }

    private void HideSystemUsagePopover()
    {
        var ownsPin = _hoverSystemUsage
            && _popoverTargetId is null
            && _hoverRadarTarget is null;
        _popoverHoverTimer.Stop();
        _systemUsagePopover?.HidePopover();
        if (ownsPin) _popoverPinned = false;
        _hoverSystemUsage = false;
        _popoverLeaveStarted = null;
        if (_quotaPopover?.Visible != true && _radarPopover?.Visible != true)
        {
            _popoverStateTimer.Stop();
        }
        EnsureAnimationTimer();
        Invalidate();
    }

    private void HidePopovers()
    {
        HideCodexStrategyPopover();
        HidePluginPopover();
        HideSystemUsagePopover();
        HideQuotaPopover();
        HideCodexAccountsPopover();
        HideHintPopover();
        HideRadarPopover();
    }

    private void ClearHoverStateForTaskbarDrag()
    {
        _hoverTarget = HoverTarget.None;
        _hoverReorderAreaId = null;
        Cursor = Cursors.SizeAll;
        EnsureAnimationTimer();
        Invalidate();
    }
}
