using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed record WindowPlacementCommit(
    string TopologyKey,
    WindowPlacementProfile Profile,
    bool IsMigration,
    string? DockedMonitorName,
    double? DockedPosition,
    IReadOnlyDictionary<string, double> LegacyTaskbarPositions,
    Point? FloatingLocation);

internal sealed record WindowPlacementActivation(
    WindowPlacementProfile Profile,
    WindowPlacementCommit? MigrationCommit);

internal readonly record struct DockedPlacementPreference(
    string PreferredMonitorName,
    double RelativePosition);

internal sealed class WindowPlacementCoordinator
{
    private Dictionary<string, WindowPlacementProfile> _profiles;
    private PlacementMigrationSeed? _migrationSeed;

    public WindowPlacementCoordinator(AppSettings settings)
    {
        _profiles = AppSettings.CopyPlacementProfiles(settings.PlacementProfiles);
        _migrationSeed = settings.PlacementMigrationSeed?.Copy();
    }

    public DisplayTopologySnapshot? ActiveTopology { get; private set; }
    public WindowPlacementProfile? ActiveProfile { get; private set; }

    public void Reload(AppSettings settings)
    {
        _profiles = AppSettings.CopyPlacementProfiles(settings.PlacementProfiles);
        _migrationSeed = settings.PlacementMigrationSeed?.Copy();
    }

    public WindowPlacementProfile Preview(DisplayTopologySnapshot topology, Size windowSize) =>
        SelectProfile(topology, windowSize, ActiveTopology, ActiveProfile).Profile;

    public WindowPlacementActivation Activate(DisplayTopologySnapshot topology, Size windowSize)
    {
        var selected = SelectProfile(topology, windowSize, ActiveTopology, ActiveProfile);
        ActiveTopology = topology;
        ActiveProfile = selected.Profile;
        if (!selected.IsMigration) return new WindowPlacementActivation(selected.Profile, null);

        _profiles[topology.Key] = selected.Profile.Copy();
        return new WindowPlacementActivation(
            selected.Profile,
            BuildCommit(topology, selected.Profile, windowSize, isMigration: true));
    }

    public DockedPlacementPreference DockedPreference(
        DisplayTopologySnapshot topology,
        WindowPlacementProfile profile)
    {
        var target = topology.FindByMonitorKey(profile.DockedMonitorKey) ?? topology.Primary;
        var position = profile.TaskbarPositions.TryGetValue(target.MonitorKey, out var saved)
            ? saved
            : 1;
        return new DockedPlacementPreference(target.GdiName, position);
    }

    public double PositionForResolvedMonitor(
        DisplayTopologySnapshot topology,
        WindowPlacementProfile profile,
        string? resolvedMonitorName,
        double fallback)
    {
        var resolved = topology.FindByGdiName(resolvedMonitorName);
        return resolved is not null && profile.TaskbarPositions.TryGetValue(resolved.MonitorKey, out var saved)
            ? saved
            : fallback;
    }

    public Point FloatingLocation(
        DisplayTopologySnapshot topology,
        WindowPlacementProfile profile,
        Size windowSize)
    {
        var target = topology.FindByMonitorKey(profile.FloatingMonitorKey) ?? topology.Primary;
        return RestoreFloatingLocation(
            target.WorkingArea,
            windowSize,
            profile.FloatingX ?? .5,
            profile.FloatingY ?? 0);
    }

    public WindowPlacementCommit? CommitDocked(
        string? resolvedMonitorName,
        double relativePosition,
        Size windowSize)
    {
        if (ActiveTopology is null || ActiveProfile is null) return null;
        var target = ActiveTopology.FindByGdiName(resolvedMonitorName);
        if (target is null) return null;

        var profile = ActiveProfile.Copy();
        profile.IsDocked = true;
        profile.DockedMonitorKey = target.MonitorKey;
        profile.TaskbarPositions[target.MonitorKey] = Math.Clamp(relativePosition, 0, 1);
        ActiveProfile = profile;
        _profiles[ActiveTopology.Key] = profile.Copy();
        return BuildCommit(ActiveTopology, profile, windowSize, isMigration: false);
    }

    public WindowPlacementCommit? CommitFloating(Rectangle bounds)
    {
        if (ActiveTopology is null || ActiveProfile is null) return null;
        var target = ActiveTopology.ScreenForWindow(bounds);
        if (target is null) return null;

        var normalized = NormalizeFloatingLocation(target.WorkingArea, bounds.Size, bounds.Location);
        var profile = ActiveProfile.Copy();
        profile.IsDocked = false;
        profile.FloatingMonitorKey = target.MonitorKey;
        profile.FloatingX = normalized.X;
        profile.FloatingY = normalized.Y;
        ActiveProfile = profile;
        _profiles[ActiveTopology.Key] = profile.Copy();
        return BuildCommit(ActiveTopology, profile, bounds.Size, isMigration: false);
    }

    internal static PointF NormalizeFloatingLocation(
        Rectangle workingArea,
        Size windowSize,
        Point location)
    {
        var travelX = Math.Max(0, workingArea.Width - windowSize.Width);
        var travelY = Math.Max(0, workingArea.Height - windowSize.Height);
        var x = travelX == 0 ? 0 : (location.X - workingArea.Left) / (double)travelX;
        var y = travelY == 0 ? 0 : (location.Y - workingArea.Top) / (double)travelY;
        return new PointF((float)Math.Clamp(x, 0, 1), (float)Math.Clamp(y, 0, 1));
    }

    internal static Point RestoreFloatingLocation(
        Rectangle workingArea,
        Size windowSize,
        double normalizedX,
        double normalizedY)
    {
        var travelX = Math.Max(0, workingArea.Width - windowSize.Width);
        var travelY = Math.Max(0, workingArea.Height - windowSize.Height);
        return new Point(
            workingArea.Left + (int)Math.Round(travelX * Math.Clamp(normalizedX, 0, 1)),
            workingArea.Top + (int)Math.Round(travelY * Math.Clamp(normalizedY, 0, 1)));
    }

    private ProfileSelection SelectProfile(
        DisplayTopologySnapshot topology,
        Size windowSize,
        DisplayTopologySnapshot? previousTopology,
        WindowPlacementProfile? previousProfile)
    {
        if (_profiles.TryGetValue(topology.Key, out var exact))
        {
            return new ProfileSelection(exact.Copy(), false);
        }

        var migrated = Migrate(topology, windowSize);
        if (migrated is not null) return new ProfileSelection(migrated, true);
        if (previousTopology is not null && previousProfile is not null)
        {
            return new ProfileSelection(Project(topology, previousTopology, previousProfile), false);
        }

        var primary = topology.Primary;
        return new ProfileSelection(new WindowPlacementProfile
        {
            IsDocked = true,
            DockedMonitorKey = primary.MonitorKey,
            TaskbarPositions = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [primary.MonitorKey] = 1,
            },
            FloatingMonitorKey = primary.MonitorKey,
            FloatingX = .5,
            FloatingY = 0,
        }, false);
    }

    private WindowPlacementProfile? Migrate(DisplayTopologySnapshot topology, Size windowSize)
    {
        if (_migrationSeed is null) return null;
        var seed = _migrationSeed;
        DisplayTopologyScreen? dockedTarget;
        if (string.IsNullOrWhiteSpace(seed.TaskbarMonitor))
        {
            dockedTarget = topology.Primary;
        }
        else
        {
            dockedTarget = topology.FindByGdiName(seed.TaskbarMonitor);
        }

        DisplayTopologyScreen? floatingTarget = null;
        PointF? floating = null;
        if (seed.WindowX is { } x && seed.WindowY is { } y)
        {
            var legacyBounds = new Rectangle(new Point(x, y), windowSize);
            floatingTarget = topology.ScreenForWindow(legacyBounds);
            if (floatingTarget is not null)
            {
                floating = NormalizeFloatingLocation(
                    floatingTarget.WorkingArea,
                    windowSize,
                    legacyBounds.Location);
            }
        }

        if (seed.TaskbarDocked && dockedTarget is null) return null;
        if (!seed.TaskbarDocked && (floatingTarget is null || floating is null)) return null;

        var positions = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var entry in seed.TaskbarPositions)
        {
            var screen = topology.FindByGdiName(entry.Key);
            if (screen is not null) positions[screen.MonitorKey] = Math.Clamp(entry.Value, 0, 1);
        }
        if (dockedTarget is not null && !positions.ContainsKey(dockedTarget.MonitorKey))
        {
            positions[dockedTarget.MonitorKey] = Math.Clamp(seed.TaskbarPosition ?? 1, 0, 1);
        }

        var primary = topology.Primary;
        return new WindowPlacementProfile
        {
            IsDocked = seed.TaskbarDocked,
            DockedMonitorKey = dockedTarget?.MonitorKey ?? primary.MonitorKey,
            TaskbarPositions = positions,
            FloatingMonitorKey = floatingTarget?.MonitorKey ?? primary.MonitorKey,
            FloatingX = floating?.X ?? .5,
            FloatingY = floating?.Y ?? 0,
        };
    }

    private static WindowPlacementProfile Project(
        DisplayTopologySnapshot topology,
        DisplayTopologySnapshot previousTopology,
        WindowPlacementProfile previousProfile)
    {
        var previousDocked = previousTopology.FindByMonitorKey(previousProfile.DockedMonitorKey);
        var dockedTarget = previousDocked is null
            ? topology.Primary
            : topology.FindByMonitorKey(previousDocked.MonitorKey) ?? topology.Primary;
        var previousPosition = previousDocked is not null
            && previousProfile.TaskbarPositions.TryGetValue(previousDocked.MonitorKey, out var saved)
                ? saved
                : 1;

        var previousFloating = previousTopology.FindByMonitorKey(previousProfile.FloatingMonitorKey);
        var floatingTarget = previousFloating is null
            ? topology.Primary
            : topology.FindByMonitorKey(previousFloating.MonitorKey) ?? topology.Primary;
        return new WindowPlacementProfile
        {
            IsDocked = previousProfile.IsDocked,
            DockedMonitorKey = dockedTarget.MonitorKey,
            TaskbarPositions = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [dockedTarget.MonitorKey] = previousPosition,
            },
            FloatingMonitorKey = floatingTarget.MonitorKey,
            FloatingX = previousProfile.FloatingX ?? .5,
            FloatingY = previousProfile.FloatingY ?? 0,
        };
    }

    private static WindowPlacementCommit BuildCommit(
        DisplayTopologySnapshot topology,
        WindowPlacementProfile profile,
        Size windowSize,
        bool isMigration)
    {
        var docked = topology.FindByMonitorKey(profile.DockedMonitorKey) ?? topology.Primary;
        var position = profile.TaskbarPositions.TryGetValue(docked.MonitorKey, out var saved) ? saved : 1;
        var legacyPositions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var screen in topology.Screens)
        {
            if (profile.TaskbarPositions.TryGetValue(screen.MonitorKey, out var screenPosition))
            {
                legacyPositions[screen.GdiName] = screenPosition;
            }
        }

        var floating = topology.FindByMonitorKey(profile.FloatingMonitorKey) ?? topology.Primary;
        var floatingLocation = RestoreFloatingLocation(
            floating.WorkingArea,
            windowSize,
            profile.FloatingX ?? .5,
            profile.FloatingY ?? 0);
        return new WindowPlacementCommit(
            topology.Key,
            profile.Copy(),
            isMigration,
            docked.GdiName,
            position,
            legacyPositions,
            floatingLocation);
    }

    private sealed record ProfileSelection(WindowPlacementProfile Profile, bool IsMigration);
}
