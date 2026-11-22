namespace ZGSTokenBar.Core;

/// <summary>
/// Chooses which session logs one refresh reads. The cap counts unique filenames after deduplication,
/// each home keeps a newest-first slice, and older files rotate across refreshes.
/// </summary>
internal sealed class CodexSessionFileDiscovery
{
    private readonly Dictionary<string, int> _olderSkip = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, FileInfo> Collect(IReadOnlyList<string> codexHomes)
    {
        var selected = new Dictionary<string, FileInfo>(StringComparer.Ordinal);
        var roots = Roots(codexHomes);
        if (roots.Count == 0) return selected;

        var streams = new Dictionary<string, List<(string Folder, string Name)>>(roots.Count, StringComparer.OrdinalIgnoreCase);
        var fileCache = new Dictionary<(string Folder, string Name), FileInfo?>();
        foreach (var root in roots) streams[root] = Stream(root).ToList();

        var freshEach = Math.Max(1, (CodexTokenUsageReader.MaximumCandidateFiles / 2) / roots.Count);
        foreach (var root in roots)
            Walk(root, streams[root], fileCache, selected, skipNew: 0, takeNew: freshEach, advanceCursor: false);
        foreach (var root in roots)
        {
            var room = CodexTokenUsageReader.MaximumCandidateFiles - selected.Count;
            if (room <= 0) break;
            Walk(root, streams[root], fileCache, selected, _olderSkip.GetValueOrDefault(root), room, advanceCursor: true);
        }

        return selected;
    }

    private void Walk(
        string root,
        IReadOnlyList<(string Folder, string Name)> entries,
        Dictionary<(string Folder, string Name), FileInfo?> fileCache,
        Dictionary<string, FileInfo> selected,
        int skipNew,
        int takeNew,
        bool advanceCursor)
    {
        if (takeNew <= 0) return;
        var passed = 0;
        var taken = 0;
        foreach (var (folder, name) in entries)
        {
            var key = CodexTokenUsageReader.SessionFileKey(name);
            if (selected.TryGetValue(key, out var existing))
            {
                if (TryReadCached(folder, name, fileCache, out var duplicate)
                    && (duplicate.Length > existing.Length
                        || duplicate.Length == existing.Length && duplicate.LastWriteTimeUtc > existing.LastWriteTimeUtc))
                {
                    selected[key] = duplicate;
                }

                continue;
            }

            if (!TryReadCached(folder, name, fileCache, out var file)) continue;
            if (passed < skipNew)
            {
                passed++;
                continue;
            }

            if (taken >= takeNew || selected.Count >= CodexTokenUsageReader.MaximumCandidateFiles) break;
            selected[key] = file;
            taken++;
            passed++;
        }

        if (!advanceCursor) return;
        _olderSkip[root] = taken == 0 && skipNew > 0 ? 0 : passed;
    }

    private static bool TryReadCached(
        string folder,
        string name,
        Dictionary<(string Folder, string Name), FileInfo?> cache,
        out FileInfo file)
    {
        var cacheKey = (folder, name);
        if (cache.TryGetValue(cacheKey, out var cached))
        {
            file = cached!;
            return cached is not null;
        }
        if (TryRead(folder, name, out file))
        {
            cache[cacheKey] = file;
            return true;
        }
        cache[cacheKey] = null;
        return false;
    }

    private static bool TryRead(string folder, string name, out FileInfo file)
    {
        file = new FileInfo(Path.Combine(folder, name));
        try
        {
            return file.Exists && file.Length > 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static List<string> Roots(IReadOnlyList<string> homes)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var home in homes)
        {
            if (string.IsNullOrWhiteSpace(home)) continue;
            foreach (var name in new[] { "sessions", "archived_sessions" })
            {
                var root = Path.Combine(home, name);
                if (seen.Add(root) && Directory.Exists(root)) roots.Add(root);
            }
        }

        return roots;
    }

    private static IEnumerable<(string Folder, string Name)> Stream(string root)
    {
        foreach (var folder in FoldersNewestFirst(root))
        {
            List<string> names;
            try
            {
                names = Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .Select(name => name!)
                    .ToList();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            names.Reverse();
            foreach (var name in names) yield return (folder, name);
        }
    }

    private static IEnumerable<string> FoldersNewestFirst(string root)
    {
        var folders = new List<string> { root };
        try
        {
            folders.AddRange(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A missing child is skipped; the root itself may still contain logs.
        }

        folders.Sort(StringComparer.OrdinalIgnoreCase);
        folders.Reverse();
        return folders;
    }
}
