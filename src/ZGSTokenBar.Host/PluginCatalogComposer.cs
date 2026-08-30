using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Host;

public sealed record OptionalPluginSelection(
    IReadOnlyList<IZgsPlugin> Accepted,
    IReadOnlyList<IZgsPlugin> Rejected);

public static class PluginCatalogComposer
{
    public static OptionalPluginSelection SelectOptional(
        IReadOnlyList<IZgsPlugin> basePlugins,
        IReadOnlyList<IZgsPlugin> optionalPlugins)
    {
        ArgumentNullException.ThrowIfNull(basePlugins);
        ArgumentNullException.ThrowIfNull(optionalPlugins);
        var acceptedCatalog = basePlugins.ToList();
        var baseErrors = PluginValidation.ValidateCatalog(
            acceptedCatalog.Select(plugin => plugin.Manifest).ToArray());
        if (baseErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Invalid base plugin catalog: {string.Join(", ", baseErrors)}");
        }

        var accepted = new List<IZgsPlugin>();
        var pending = optionalPlugins
            .OrderBy(plugin => plugin.Manifest.Order)
            .ThenBy(plugin => plugin.Manifest.Id, StringComparer.Ordinal)
            .ToList();
        while (pending.Count > 0)
        {
            var changed = false;
            for (var index = 0; index < pending.Count;)
            {
                var candidate = pending[index];
                var manifests = acceptedCatalog
                    .Select(plugin => plugin.Manifest)
                    .Append(candidate.Manifest)
                    .ToArray();
                if (PluginValidation.ValidateCatalog(manifests).Count > 0)
                {
                    index++;
                    continue;
                }
                acceptedCatalog.Add(candidate);
                accepted.Add(candidate);
                pending.RemoveAt(index);
                changed = true;
            }
            if (!changed) break;
        }
        return new(accepted, pending);
    }
}
