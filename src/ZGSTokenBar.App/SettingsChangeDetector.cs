using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using ZGSTokenBar.Builtins;
using ZGSTokenBar.Core;
using ZGSTokenBar.Host;
using ZGSTokenBar.PluginAdapters;
using ZGSTokenBar.PluginSdk;
using ZGSTokenBar.Transport.NamedPipe;

namespace ZGSTokenBar.App;

/// <summary>Pure comparisons that decide how much of the app a settings change has to rebuild.</summary>
internal static class SettingsChangeDetector
{
    internal static bool IsLocaleOnlyChange(AppSettings previous, AppSettings next)
    {
        if (string.Equals(previous.Locale, next.Locale, StringComparison.Ordinal)) return false;
        return previous.EnabledProviders.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(next.EnabledProviders)
            && previous.RefreshMinutes == next.RefreshMinutes
            && previous.AutoRefreshClaudeOAuth == next.AutoRefreshClaudeOAuth
            && previous.OpenAtLogin == next.OpenAtLogin
            && previous.EnableAlerts == next.EnableAlerts
            && previous.TaskbarDocked == next.TaskbarDocked
            && previous.EnableAnimations == next.EnableAnimations
            && previous.EmbedInTaskbar == next.EmbedInTaskbar
            && previous.EnableRadar == next.EnableRadar
            && previous.EnableRadarAlerts == next.EnableRadarAlerts
            && PluginEnabledEqual(previous.RadarModelGroups, next.RadarModelGroups)
            && previous.EnableAiGatewayBalance == next.EnableAiGatewayBalance
            && previous.EnableSub2ApiPool == next.EnableSub2ApiPool
            && PluginEnabledEqual(previous.PluginEnabled, next.PluginEnabled)
            && MiniAreaLayoutsEqual(previous.MiniAreaLayouts, next.MiniAreaLayouts)
            && previous.MiniAreaOrder.SequenceEqual(next.MiniAreaOrder, StringComparer.Ordinal)
            && string.Equals(previous.CodexMiniDisplayMode, next.CodexMiniDisplayMode, StringComparison.Ordinal)
            && previous.WindowX == next.WindowX
            && previous.WindowY == next.WindowY
            && previous.TaskbarPosition == next.TaskbarPosition
            && string.Equals(previous.TaskbarMonitor, next.TaskbarMonitor, StringComparison.Ordinal)
            && TaskbarPositionsEqual(previous.TaskbarPositions, next.TaskbarPositions)
            && previous.PlacementSchemaVersion == next.PlacementSchemaVersion
            && PlacementMigrationSeedsEqual(previous.PlacementMigrationSeed, next.PlacementMigrationSeed)
            && PlacementProfilesEqual(previous.PlacementProfiles, next.PlacementProfiles);
    }

    internal static bool PluginEnabledEqual(
        IReadOnlyDictionary<string, bool> left,
        IReadOnlyDictionary<string, bool> right) =>
        left.Count == right.Count
        && left.All(entry => right.TryGetValue(entry.Key, out var value) && value == entry.Value);

    internal static bool TaskbarPositionsEqual(
        IReadOnlyDictionary<string, double>? left,
        IReadOnlyDictionary<string, double>? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Count != right.Count) return false;
        foreach (var entry in left)
        {
            if (!right.TryGetValue(entry.Key, out var value) || value != entry.Value) return false;
        }
        return true;
    }

    internal static bool MiniAreaLayoutsEqual(
        IReadOnlyDictionary<string, MiniAreaLayout> left,
        IReadOnlyDictionary<string, MiniAreaLayout> right) =>
        left.Count == right.Count
        && left.All(entry => right.TryGetValue(entry.Key, out var value) && value == entry.Value);

    internal static bool PlacementMigrationSeedsEqual(
        PlacementMigrationSeed? left,
        PlacementMigrationSeed? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.TaskbarDocked == right.TaskbarDocked
            && left.WindowX == right.WindowX
            && left.WindowY == right.WindowY
            && left.TaskbarPosition == right.TaskbarPosition
            && string.Equals(left.TaskbarMonitor, right.TaskbarMonitor, StringComparison.Ordinal)
            && TaskbarPositionsEqual(left.TaskbarPositions, right.TaskbarPositions);
    }

    internal static bool PlacementProfilesEqual(
        IReadOnlyDictionary<string, WindowPlacementProfile>? left,
        IReadOnlyDictionary<string, WindowPlacementProfile>? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Count != right.Count) return false;
        foreach (var entry in left)
        {
            if (!right.TryGetValue(entry.Key, out var profile)
                || entry.Value.IsDocked != profile.IsDocked
                || !string.Equals(entry.Value.DockedMonitorKey, profile.DockedMonitorKey, StringComparison.Ordinal)
                || !string.Equals(entry.Value.FloatingMonitorKey, profile.FloatingMonitorKey, StringComparison.Ordinal)
                || entry.Value.FloatingX != profile.FloatingX
                || entry.Value.FloatingY != profile.FloatingY
                || !TaskbarPositionsEqual(entry.Value.TaskbarPositions, profile.TaskbarPositions))
            {
                return false;
            }
        }
        return true;
    }
}
