using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ZGSTokenBar.PluginSdk;

public static partial class PluginValidation
{
    private const int MaximumIdLength = 128;
    private const int MaximumDisplayCodePoints = 512;
    private static readonly HashSet<string> AllowedValueKinds =
        ["text", "decimal", "number", "integer", "boolean", "timestamp", "currency", "percent", "duration"];
    private static readonly HashSet<string> AllowedSettingsKinds =
        ["toggle", "number", "choice", "text", "secret"];

    [GeneratedRegex("^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex StableIdPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    public static IReadOnlyList<string> ValidateCatalog(IReadOnlyList<PluginManifest> manifests)
    {
        var errors = new List<string>();
        var byId = new Dictionary<string, PluginManifest>(StringComparer.Ordinal);
        var commandNamespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manifest in manifests)
        {
            errors.AddRange(ValidateManifest(manifest));
            if (IsStableId(manifest.Id)
                && !byId.TryAdd(manifest.Id, manifest))
            {
                errors.Add($"duplicate_plugin_id:{manifest.Id}");
            }
            if (IsStableId(manifest.CommandNamespace)
                && !commandNamespaces.Add(manifest.CommandNamespace))
            {
                errors.Add($"duplicate_command_namespace:{manifest.CommandNamespace}");
            }
        }

        foreach (var manifest in manifests)
        {
            foreach (var dependency in manifest.Requires ?? [])
            {
                if (!byId.ContainsKey(dependency)) errors.Add($"missing_dependency:{manifest.Id}:{dependency}");
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string id)
        {
            if (!visiting.Add(id)) return false;
            if (visited.Contains(id))
            {
                visiting.Remove(id);
                return true;
            }
            if (byId.TryGetValue(id, out var manifest))
            {
                foreach (var dependency in manifest.Requires ?? [])
                {
                    if (byId.ContainsKey(dependency) && !Visit(dependency))
                    {
                        errors.Add($"dependency_cycle:{id}");
                        return false;
                    }
                }
            }
            visiting.Remove(id);
            visited.Add(id);
            return true;
        }

        foreach (var id in byId.Keys) Visit(id);
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<string> ValidateManifest(PluginManifest manifest)
    {
        var errors = new List<string>();
        if (manifest.SchemaVersion != 1) errors.Add("manifest_schema_unsupported");
        if (!IsStableId(manifest.Id)) errors.Add($"invalid_plugin_id:{manifest.Id}");
        if (!IsStableId(manifest.CommandNamespace)) errors.Add($"invalid_command_namespace:{manifest.CommandNamespace}");
        if (!Enum.IsDefined(manifest.Runtime)) errors.Add($"invalid_plugin_runtime:{manifest.Id}");
        if (manifest.HostApiMajor != ZgsHostApi.Major
            || manifest.HostApiMinMinor < 0
            || manifest.HostApiMinMinor > ZgsHostApi.Minor)
        {
            errors.Add($"host_api_incompatible:{manifest.Id}");
        }
        if (!Version.TryParse(manifest.Version, out _)) errors.Add($"invalid_plugin_version:{manifest.Id}");
        if (manifest.Order is < -100_000 or > 100_000) errors.Add($"invalid_plugin_order:{manifest.Id}");
        if (manifest.Requires is null
            || manifest.Requires.Count > 32
            || manifest.Requires.Any(value => !IsStableId(value))
            || manifest.Requires.Distinct(StringComparer.Ordinal).Count() != manifest.Requires.Count
            || manifest.Requires.Contains(manifest.Id, StringComparer.Ordinal))
        {
            errors.Add($"invalid_dependency:{manifest.Id}");
        }
        if (manifest.Capabilities is null
            || manifest.Capabilities.Count > 32
            || manifest.Capabilities.Any(value => !IsStableId(value))
            || manifest.Capabilities.Distinct(StringComparer.Ordinal).Count() != manifest.Capabilities.Count)
        {
            errors.Add($"invalid_capabilities:{manifest.Id}");
        }
        if (manifest.Files is null
            || manifest.Files.Count > 256
            || manifest.Files.Any(file => file is null))
        {
            errors.Add($"invalid_files:{manifest.Id}");
        }
        if (manifest.Locales is null || manifest.Locales.Count > 2)
        {
            errors.Add($"invalid_locales:{manifest.Id}");
        }
        if (manifest.CredentialSlots is null
            || manifest.CredentialSlots.Count > 16
            || manifest.CredentialSlots.Any(value => !IsStableId(value))
            || manifest.CredentialSlots.Distinct(StringComparer.Ordinal).Count()
                != manifest.CredentialSlots.Count)
        {
            errors.Add($"invalid_credential_slots:{manifest.Id}");
        }
        return errors;
    }

    public static IReadOnlyList<string> ValidateSnapshot(
        PluginManifest manifest,
        PluginDataSnapshot snapshot,
        Func<string, bool>? resourceExists = null,
        Func<string, bool>? localizationExists = null)
    {
        var errors = new List<string>();
        if (!string.Equals(manifest.Id, snapshot.PluginId, StringComparison.Ordinal))
        {
            errors.Add("snapshot_plugin_mismatch");
        }
        if (snapshot.MiniCards.Count > 64) errors.Add("too_many_mini_cards");
        if (snapshot.Details.Count > 64) errors.Add("too_many_details");
        if (snapshot.Radar.Count > 16) errors.Add("too_many_radar_contributions");

        var contributionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in snapshot.MiniCards)
        {
            ValidateContributionId(card.Id, contributionIds, errors);
            if (!string.Equals(card.PluginId, manifest.Id, StringComparison.Ordinal)) errors.Add("card_plugin_mismatch");
            if (!IsStableId(card.GroupId)) errors.Add($"invalid_group_id:{card.Id}");
            if (card.Summary.Count > 8) errors.Add($"too_many_summary_items:{card.Id}");
            ValidateKey(card.TitleKey, localizationExists, errors);
            ValidateKey(card.IconResourceKey, resourceExists, errors);
            foreach (var item in card.Summary)
            {
                ValidateKey(item.LabelKey, localizationExists, errors);
                ValidateValue(item.Value, errors);
            }
        }

        foreach (var detail in snapshot.Details)
        {
            ValidateContributionId(detail.Id, contributionIds, errors);
            if (!string.Equals(detail.PluginId, manifest.Id, StringComparison.Ordinal)) errors.Add("detail_plugin_mismatch");
            if (detail.Sections.Count > 8) errors.Add($"too_many_detail_sections:{detail.Id}");
            foreach (var section in detail.Sections)
            {
                if (section.Rows.Count > 32) errors.Add($"too_many_detail_rows:{detail.Id}:{section.Id}");
                ValidateKey(section.TitleKey, localizationExists, errors);
                foreach (var row in section.Rows)
                {
                    ValidateKey(row.LabelKey, localizationExists, errors);
                    ValidateValue(row.Value, errors);
                }
            }
        }

        foreach (var radar in snapshot.Radar)
        {
            ValidateContributionId(radar.Id, contributionIds, errors);
            if (!string.Equals(radar.PluginId, manifest.Id, StringComparison.Ordinal)) errors.Add("radar_plugin_mismatch");
            if (radar.Rows.Count > 256) errors.Add($"too_many_radar_rows:{radar.Id}");
            foreach (var row in radar.Rows)
            {
                if (CodePointCount(row.Model) > MaximumDisplayCodePoints) errors.Add($"radar_model_too_long:{radar.Id}");
            }
        }
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<string> ValidateCommands(
        PluginManifest manifest,
        IReadOnlyList<CommandDescriptor>? commands)
    {
        var errors = new List<string>();
        if (commands is null || commands.Count > 64) return ["invalid_command_count"];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            if (!string.Equals(command.PluginId, manifest.Id, StringComparison.Ordinal)
                || !string.Equals(command.Namespace, manifest.CommandNamespace, StringComparison.Ordinal)
                || !IsStableId(command.Id)
                || !IsStableId(command.Name)
                || !ids.Add(command.Id)
                || !names.Add(command.Name)
                || CodePointCount(command.Summary ?? string.Empty) > MaximumDisplayCodePoints
                || command.SecretSlots is null
                || command.SecretSlots.Distinct(StringComparer.Ordinal).Count() != command.SecretSlots.Count
                || command.SecretSlots.Any(slot =>
                    !manifest.CredentialSlots.Contains(slot, StringComparer.Ordinal)))
            {
                errors.Add($"invalid_command:{command.Id}");
            }
        }
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<string> ValidateSettings(
        PluginManifest manifest,
        IReadOnlyList<SettingsContribution>? settings)
    {
        var errors = new List<string>();
        if (settings is null || settings.Count > 16) return ["invalid_settings_count"];
        var contributionIds = new HashSet<string>(StringComparer.Ordinal);
        var fieldIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contribution in settings)
        {
            if (!string.Equals(contribution.PluginId, manifest.Id, StringComparison.Ordinal)
                || !IsStableId(contribution.Id)
                || !contributionIds.Add(contribution.Id)
                || contribution.Fields is null
                || contribution.Fields.Count > 32)
            {
                errors.Add($"invalid_settings:{contribution.Id}");
                continue;
            }
            foreach (var field in contribution.Fields)
            {
                if (!IsStableId(field.Id) || !fieldIds.Add(field.Id))
                {
                    errors.Add($"invalid_settings_field:{field.Id}");
                }
                ValidateKey(field.LabelKey, null, errors);
                if (!AllowedSettingsKinds.Contains(field.Kind)
                    || field.Minimum > field.Maximum
                    || field.AllowedValues is { Count: > 64 }
                    || field.AllowedValues?.Any(value => CodePointCount(value) > 128) == true
                    || field.SecretSlot is not null
                        && (!string.Equals(field.Kind, "secret", StringComparison.Ordinal)
                            || !manifest.CredentialSlots.Contains(field.SecretSlot, StringComparer.Ordinal))
                    || string.Equals(field.Kind, "secret", StringComparison.Ordinal)
                        && field.SecretSlot is null
                    || field.DefaultValue is { } defaultValue
                        && defaultValue.GetRawText().Length > 4096)
                {
                    errors.Add($"invalid_settings_field:{field.Id}");
                }
            }
        }
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static bool IsStableId(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumIdLength
        && StableIdPattern().IsMatch(value);

    public static bool IsRequestId(string? value) =>
        value is { Length: >= 1 and <= 64 }
        && value.All(character => character is >= '!' and <= '~');

    private static void ValidateContributionId(
        string id,
        HashSet<string> ids,
        List<string> errors)
    {
        if (!IsStableId(id)) errors.Add($"invalid_contribution_id:{id}");
        if (!ids.Add(id)) errors.Add($"duplicate_contribution_id:{id}");
    }

    private static void ValidateKey(
        string key,
        Func<string, bool>? exists,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(key)
            || key.Length > MaximumIdLength
            || !KeyPattern().IsMatch(key))
        {
            errors.Add($"invalid_resource_key:{key}");
            return;
        }
        if (exists is not null && !exists(key)) errors.Add($"unknown_resource_key:{key}");
    }

    private static void ValidateValue(ContributionValue value, List<string> errors)
    {
        if (!AllowedValueKinds.Contains(value.Kind)) errors.Add($"invalid_value_kind:{value.Kind}");
        if (value.Text is not null && CodePointCount(value.Text) > MaximumDisplayCodePoints)
        {
            errors.Add("display_value_too_long");
        }
        if (value.Number is double number && (double.IsNaN(number) || double.IsInfinity(number)))
        {
            errors.Add("non_finite_number");
        }
    }

    private static int CodePointCount(string value) =>
        value.EnumerateRunes().Count();
}
