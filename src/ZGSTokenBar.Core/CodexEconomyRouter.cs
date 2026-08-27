using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZGSTokenBar.Core;

public enum CodexEconomyMode
{
    Unconfigured,
    Off,
    Ask,
    On,
    Inconsistent,
}

public sealed record CodexEconomyProfile(
    string DisplayName,
    string HomeDirectory,
    bool Recommended,
    string Source)
{
    public string ConfigPath => Path.Combine(HomeDirectory, "config.toml");
    public string SkillDirectory => Path.Combine(HomeDirectory, "skills", CodexEconomyRouter.SkillName);
    public string SkillPath => Path.Combine(SkillDirectory, "SKILL.md");
}

public sealed record CodexEconomyStatus(
    CodexEconomyMode Mode,
    CodexEconomyProfile Profile,
    bool SkillInstalled,
    bool HasNamedConfigLayers,
    string? Diagnostic = null);

public sealed class CodexEconomyException : IOException
{
    public CodexEconomyException(string message) : base(message) { }
    public CodexEconomyException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class CodexEconomyRouter
{
    public const string SkillName = "sol-luna-delegation";
    public const string EconomyModel = "gpt-5.6-luna";
    public const string EconomyEffort = "max";

    internal const string AgentBegin = "# BEGIN sol-luna-delegation economy agent defaults";
    internal const string AgentEnd = "# END sol-luna-delegation economy agent defaults";
    internal const string SkillBegin = "# BEGIN sol-luna-delegation economy skill switch";
    internal const string SkillEnd = "# END sol-luna-delegation economy skill switch";

    private const string OwnershipManifestName = ".zgstokenbar-skill.json";
    private const int LockAttempts = 40;
    private const int LockDelayMilliseconds = 25;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly SkillAsset[] SkillAssets =
    [
        new("SKILL.md", "ZGSTokenBar.Core.Skills.sol-luna-delegation.SKILL.md"),
        new("agents/openai.yaml", "ZGSTokenBar.Core.Skills.sol-luna-delegation.agents.openai.yaml"),
        new("scripts/set_economy_mode.py", "ZGSTokenBar.Core.Skills.sol-luna-delegation.scripts.set_economy_mode.py"),
        new("scripts/verify_subagent_runtime.py", "ZGSTokenBar.Core.Skills.sol-luna-delegation.scripts.verify_subagent_runtime.py"),
    ];

    public static CodexEconomyProfile ResolveProfile(string? codexHome = null)
    {
        var source = "default";
        var selected = codexHome;
        if (string.IsNullOrWhiteSpace(selected))
        {
            selected = Environment.GetEnvironmentVariable("CODEX_HOME");
            source = string.IsNullOrWhiteSpace(selected) ? "default" : "environment";
        }
        if (string.IsNullOrWhiteSpace(selected))
        {
            selected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        var home = NormalizeHome(selected);
        return new CodexEconomyProfile(ProfileDisplayName(home, source), home, true, source);
    }

    public static IReadOnlyList<CodexEconomyProfile> DiscoverProfiles(
        string? userProfileDirectory = null,
        string? cockpitManifestPath = null)
    {
        var result = new List<CodexEconomyProfile>();
        var seen = new HashSet<string>(PathComparer);
        var environmentHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(environmentHome))
        {
            AddProfile(result, seen, environmentHome, "Current CODEX_HOME", true, "environment");
        }

        var userProfile = string.IsNullOrWhiteSpace(userProfileDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userProfileDirectory;
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            AddProfile(
                result,
                seen,
                Path.Combine(userProfile, ".codex"),
                "Codex default",
                result.Count == 0,
                "default");
        }

        var manifest = cockpitManifestPath;
        if (string.IsNullOrWhiteSpace(manifest) && !string.IsNullOrWhiteSpace(userProfile))
        {
            manifest = Path.Combine(userProfile, ".antigravity_cockpit", "codex_instances.json");
        }
        AddCockpitProfiles(result, seen, manifest);
        return result;
    }

    public CodexEconomyStatus Inspect(CodexEconomyProfile profile)
    {
        var snapshot = ReadSnapshot(profile.ConfigPath);
        var mode = InspectMode(DecodeConfig(snapshot.Bytes).Text, profile.SkillPath);
        var installed = IsSkillInstalled(profile);
        var hasNamedLayers = HasNamedConfigLayers(profile.HomeDirectory);
        var diagnostic = mode == CodexEconomyMode.Inconsistent
            ? "managed_configuration_inconsistent"
            : hasNamedLayers
                ? "named_config_layers_may_override_base"
                : null;
        return new(mode, profile, installed, hasNamedLayers, diagnostic);
    }

    public CodexEconomyStatus Install(CodexEconomyProfile profile)
    {
        InstallSkill(profile);
        return Inspect(profile);
    }

    public CodexEconomyStatus SetMode(CodexEconomyProfile profile, CodexEconomyMode mode)
    {
        if (mode is not (CodexEconomyMode.Off or CodexEconomyMode.Ask or CodexEconomyMode.On))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), "Only Off, Ask, or On can be applied.");
        }
        var snapshot = ReadSnapshot(profile.ConfigPath);
        var decoded = DecodeConfig(snapshot.Bytes);
        var updated = UpdateText(decoded.Text, profile.SkillPath, mode);
        var candidateMode = InspectMode(updated, profile.SkillPath);
        if (candidateMode != mode)
        {
            throw new CodexEconomyException(
                $"Codex economy mode preflight mismatch: expected {mode}, got {candidateMode}.");
        }
        if (mode is CodexEconomyMode.Ask or CodexEconomyMode.On)
        {
            InstallSkill(profile);
        }
        var payload = EncodeConfig(updated, decoded.Newline, decoded.HasBom);
        if (!snapshot.Bytes.AsSpan().SequenceEqual(payload))
        {
            AtomicWrite(profile.ConfigPath, payload, snapshot);
        }

        var status = Inspect(profile);
        if (status.Mode != mode)
        {
            throw new CodexEconomyException(
                $"Codex economy mode read-back mismatch: expected {mode}, got {status.Mode}.");
        }
        return status;
    }

    internal static CodexEconomyMode InspectMode(string text, string skillPath)
    {
        try
        {
            _ = ParseRelevant(text);
            var agentBlock = FindOwnedBlock(text, AgentBegin, AgentEnd);
            var skillBlock = FindOwnedBlock(text, SkillBegin, SkillEnd);
            if (agentBlock is not null
                && skillBlock is not null
                && agentBlock.Start < skillBlock.End
                && skillBlock.Start < agentBlock.End)
            {
                return CodexEconomyMode.Inconsistent;
            }

            var unmanaged = RemoveOwnedBlock(text, AgentBegin, AgentEnd);
            unmanaged = RemoveOwnedBlock(unmanaged, SkillBegin, SkillEnd);
            var unmanagedConfig = ParseRelevant(unmanaged);
            if (unmanagedConfig.SkillEntries.Any(entry => EntryTargetsSkill(entry, skillPath)))
            {
                return CodexEconomyMode.Inconsistent;
            }
            if (agentBlock is null && skillBlock is null) return CodexEconomyMode.Unconfigured;
            if (skillBlock is null) return CodexEconomyMode.Inconsistent;

            var managedSkill = ParseRelevant(skillBlock.Body);
            if (managedSkill.SkillEntries.Count != 1
                || !EntryTargetsSkill(managedSkill.SkillEntries[0], skillPath))
            {
                return CodexEconomyMode.Inconsistent;
            }

            var skillEntry = managedSkill.SkillEntries[0];
            if (agentBlock is null)
            {
                if (skillEntry.Enabled == false) return CodexEconomyMode.Off;
                if (skillEntry.Enabled == true && unmanagedConfig.AgentsEnabled != false)
                {
                    return CodexEconomyMode.Ask;
                }
                return CodexEconomyMode.Inconsistent;
            }

            var managedAgents = ParseRelevant($"[agents]\n{agentBlock.Body}");
            if (skillEntry.Enabled == true
                && !managedAgents.AgentsEnabledSeen
                && string.Equals(managedAgents.DefaultModel, EconomyModel, StringComparison.Ordinal)
                && string.Equals(managedAgents.DefaultEffort, EconomyEffort, StringComparison.Ordinal)
                && unmanagedConfig.AgentsEnabled != false)
            {
                return CodexEconomyMode.On;
            }
            return CodexEconomyMode.Inconsistent;
        }
        catch (CodexEconomyException)
        {
            return CodexEconomyMode.Inconsistent;
        }
    }

    internal static string UpdateText(string text, string skillPath, CodexEconomyMode mode)
    {
        var current = InspectMode(text, skillPath);
        if (current == CodexEconomyMode.Inconsistent)
        {
            throw new CodexEconomyException(
                "Current economy mode configuration is inconsistent; inspect it before changing modes.");
        }

        var unmanaged = RemoveOwnedBlock(text, AgentBegin, AgentEnd);
        unmanaged = RemoveOwnedBlock(unmanaged, SkillBegin, SkillEnd);
        var parsed = ParseRelevant(unmanaged);
        CheckUnmanagedConflicts(parsed, skillPath, mode);

        var updated = unmanaged;
        if (mode == CodexEconomyMode.On)
        {
            updated = AddAgentDefaults(updated);
        }
        return AddSkillSwitch(updated, skillPath, mode is CodexEconomyMode.Ask or CodexEconomyMode.On);
    }

    internal static void AtomicWriteForTesting(string path, byte[] bytes, byte[]? expectedBytes) =>
        AtomicWrite(path, bytes, new(expectedBytes is not null, expectedBytes ?? []));

    private static void CheckUnmanagedConflicts(
        RelevantConfig parsed,
        string skillPath,
        CodexEconomyMode mode)
    {
        if ((mode is CodexEconomyMode.Ask or CodexEconomyMode.On)
            && parsed.AgentsEnabled == false)
        {
            throw new CodexEconomyException(
                "Native agents are disabled by unmanaged [agents].enabled = false.");
        }
        if (mode == CodexEconomyMode.On)
        {
            if (parsed.AgentsDottedSeen)
            {
                throw new CodexEconomyException(
                    "Dotted agents configuration cannot be safely extended; use an [agents] table.");
            }
            if (parsed.DefaultModel is not null)
            {
                throw new CodexEconomyException("Unmanaged [agents].default_subagent_model already exists.");
            }
            if (parsed.DefaultEffort is not null)
            {
                throw new CodexEconomyException(
                    "Unmanaged [agents].default_subagent_reasoning_effort already exists.");
            }
        }
        if (parsed.SkillEntries.Any(entry => EntryTargetsSkill(entry, skillPath)))
        {
            throw new CodexEconomyException(
                $"An unmanaged skills.config entry already targets {SkillName}.");
        }
    }

    private static RelevantConfig ParseRelevant(string text)
    {
        var result = new RelevantConfig();
        string[]? section = null;
        SkillEntryBuilder? skillEntry = null;
        foreach (var physicalLine in LexTomlLines(text))
        {
            if (!physicalLine.OutsideMultilineAtStart) continue;
            var rawLine = physicalLine.Text;
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                if (!line.EndsWith("]]", StringComparison.Ordinal))
                {
                    throw new CodexEconomyException("Malformed TOML array table header.");
                }
                FinishSkillEntry(result, ref skillEntry);
                section = ParseKeyPath(line[2..^2], "TOML array table header");
                if (PathEquals(section, "skills", "config"))
                {
                    skillEntry = new SkillEntryBuilder();
                }
                continue;
            }
            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                if (!line.EndsWith("]", StringComparison.Ordinal) || line.StartsWith("[[", StringComparison.Ordinal))
                {
                    throw new CodexEconomyException("Malformed TOML table header.");
                }
                FinishSkillEntry(result, ref skillEntry);
                section = ParseKeyPath(line[1..^1], "TOML table header");
                if (PathEquals(section, "skills", "config"))
                {
                    throw new CodexEconomyException(
                        "[skills.config] cannot be safely managed; use [[skills.config]] entries.");
                }
                if (PathEquals(section, "agents"))
                {
                    if (result.AgentsTableSeen)
                    {
                        throw new CodexEconomyException("Duplicate [agents] table.");
                    }
                    result.AgentsTableSeen = true;
                }
                continue;
            }

            var separator = IndexOfEquals(line);
            if (separator <= 0) continue;
            var key = ParseKeyPath(line[..separator], "TOML key");
            var value = line[(separator + 1)..].Trim();
            if (section is null && key.Length == 1
                && string.Equals(key[0], "agents", StringComparison.Ordinal))
            {
                throw new CodexEconomyException(
                    "Inline agents configuration cannot be safely managed; use an [agents] table.");
            }
            if ((section is null
                    && key.Length >= 1
                    && string.Equals(key[0], "skills", StringComparison.Ordinal))
                || (PathEquals(section, "skills")
                    && key.Length >= 1
                    && string.Equals(key[0], "config", StringComparison.Ordinal)))
            {
                throw new CodexEconomyException(
                    "Inline skills configuration cannot be safely managed; use [[skills.config]] entries.");
            }
            if (PathEquals(section, "agents") && key.Length == 1)
            {
                ParseAgentValue(result, key[0], value);
            }
            else if (PathEquals(section, "skills", "config")
                     && key.Length == 1
                     && skillEntry is not null)
            {
                ParseSkillValue(skillEntry, key[0], value);
            }
            else if (section is null
                     && key.Length == 2
                     && string.Equals(key[0], "agents", StringComparison.Ordinal))
            {
                result.AgentsDottedSeen = true;
                ParseAgentValue(result, key[1], value);
            }
        }
        FinishSkillEntry(result, ref skillEntry);
        return result;
    }

    private static string[] ParseKeyPath(string expression, string name)
    {
        var result = new List<string>();
        var index = 0;
        while (true)
        {
            while (index < expression.Length && expression[index] is ' ' or '\t') index++;
            if (index >= expression.Length)
            {
                if (result.Count == 0) throw new CodexEconomyException($"{name} cannot be empty.");
                throw new CodexEconomyException($"{name} cannot end with a dot.");
            }

            string segment;
            if (expression[index] == '"')
            {
                var start = index++;
                var escaped = false;
                var closed = false;
                while (index < expression.Length)
                {
                    var character = expression[index++];
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    if (character == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (character != '"') continue;
                    closed = true;
                    break;
                }
                if (!closed) throw new CodexEconomyException($"{name} contains an unterminated quoted key.");
                segment = ParseString(expression[start..index], name);
            }
            else if (expression[index] == '\'')
            {
                var start = index++;
                while (index < expression.Length && expression[index] != '\'') index++;
                if (index >= expression.Length)
                {
                    throw new CodexEconomyException($"{name} contains an unterminated literal key.");
                }
                index++;
                segment = ParseString(expression[start..index], name);
            }
            else
            {
                var start = index;
                while (index < expression.Length && IsBareKeyCharacter(expression[index])) index++;
                if (start == index) throw new CodexEconomyException($"{name} contains an invalid key.");
                segment = expression[start..index];
            }
            result.Add(segment);

            while (index < expression.Length && expression[index] is ' ' or '\t') index++;
            if (index == expression.Length) return [.. result];
            if (expression[index] != '.') throw new CodexEconomyException($"{name} contains invalid key syntax.");
            index++;
        }
    }

    private static bool IsBareKeyCharacter(char character) =>
        character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '_' or '-';

    private static bool PathEquals(string[]? actual, params string[] expected) =>
        actual is not null && actual.AsSpan().SequenceEqual(expected);

    private static int FindTableContentStart(string text, params string[] expected)
    {
        foreach (var physicalLine in LexTomlLines(text))
        {
            if (!physicalLine.OutsideMultilineAtStart) continue;
            var line = StripComment(physicalLine.Text).Trim();
            if (line.StartsWith("[", StringComparison.Ordinal)
                && !line.StartsWith("[[", StringComparison.Ordinal)
                && line.EndsWith(']'))
            {
                var path = ParseKeyPath(line[1..^1], "TOML table header");
                if (PathEquals(path, expected)) return physicalLine.NextStart;
            }
        }
        return -1;
    }

    private static IReadOnlyList<TomlPhysicalLine> LexTomlLines(string text)
    {
        var result = new List<TomlPhysicalLine>();
        var state = TomlMultilineState.None;
        var start = 0;
        while (start <= text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline;
            var outsideAtStart = state == TomlMultilineState.None;
            var line = text[start..end];
            AdvanceTomlMultilineState(line, ref state);
            result.Add(new(start, newline < 0 ? end : end + 1, line, outsideAtStart));
            if (newline < 0) break;
            start = newline + 1;
        }
        if (state != TomlMultilineState.None)
        {
            throw new CodexEconomyException("config.toml contains an unterminated multiline string.");
        }
        return result;
    }

    private static void AdvanceTomlMultilineState(string line, ref TomlMultilineState state)
    {
        var inBasic = false;
        var inLiteral = false;
        var index = 0;
        while (index < line.Length)
        {
            if (state == TomlMultilineState.Basic)
            {
                if (StartsWithTriple(line, index, '"'))
                {
                    state = TomlMultilineState.None;
                    index += 3;
                }
                else if (line[index] == '\\')
                {
                    index = Math.Min(line.Length, index + 2);
                }
                else
                {
                    index++;
                }
                continue;
            }
            if (state == TomlMultilineState.Literal)
            {
                if (StartsWithTriple(line, index, '\''))
                {
                    state = TomlMultilineState.None;
                    index += 3;
                }
                else
                {
                    index++;
                }
                continue;
            }
            if (inBasic)
            {
                if (line[index] == '\\') index = Math.Min(line.Length, index + 2);
                else if (line[index++] == '"') inBasic = false;
                continue;
            }
            if (inLiteral)
            {
                if (line[index++] == '\'') inLiteral = false;
                continue;
            }
            if (line[index] == '#') break;
            if (StartsWithTriple(line, index, '"'))
            {
                state = TomlMultilineState.Basic;
                index += 3;
            }
            else if (StartsWithTriple(line, index, '\''))
            {
                state = TomlMultilineState.Literal;
                index += 3;
            }
            else if (line[index] == '"')
            {
                inBasic = true;
                index++;
            }
            else if (line[index] == '\'')
            {
                inLiteral = true;
                index++;
            }
            else
            {
                index++;
            }
        }
    }

    private static bool StartsWithTriple(string text, int offset, char quote) =>
        offset + 2 < text.Length
        && text[offset] == quote
        && text[offset + 1] == quote
        && text[offset + 2] == quote;

    private static void ParseAgentValue(RelevantConfig result, string key, string value)
    {
        switch (key)
        {
            case "enabled":
                if (result.AgentsEnabledSeen) throw new CodexEconomyException("Duplicate agents.enabled.");
                result.AgentsEnabled = ParseBoolean(value, "agents.enabled");
                result.AgentsEnabledSeen = true;
                break;
            case "default_subagent_model":
                if (result.DefaultModel is not null) throw new CodexEconomyException("Duplicate agent model default.");
                result.DefaultModel = ParseString(value, "agents.default_subagent_model");
                break;
            case "default_subagent_reasoning_effort":
                if (result.DefaultEffort is not null) throw new CodexEconomyException("Duplicate agent effort default.");
                result.DefaultEffort = ParseString(value, "agents.default_subagent_reasoning_effort");
                break;
        }
    }

    private static void ParseSkillValue(SkillEntryBuilder entry, string key, string value)
    {
        switch (key)
        {
            case "name":
                if (entry.Name is not null) throw new CodexEconomyException("Duplicate skills.config name.");
                entry.Name = ParseString(value, "skills.config.name");
                break;
            case "path":
                if (entry.Path is not null) throw new CodexEconomyException("Duplicate skills.config path.");
                entry.Path = ParseString(value, "skills.config.path");
                break;
            case "enabled":
                if (entry.EnabledSeen) throw new CodexEconomyException("Duplicate skills.config enabled.");
                entry.Enabled = ParseBoolean(value, "skills.config.enabled");
                entry.EnabledSeen = true;
                break;
        }
    }

    private static void FinishSkillEntry(RelevantConfig result, ref SkillEntryBuilder? builder)
    {
        if (builder is null) return;
        result.SkillEntries.Add(new(builder.Name, builder.Path, builder.EnabledSeen ? builder.Enabled : null));
        builder = null;
    }

    private static bool EntryTargetsSkill(SkillEntry entry, string skillPath) =>
        string.Equals(entry.Name?.Trim(), SkillName, StringComparison.Ordinal)
        || SamePath(entry.Path, skillPath);

    private static bool SamePath(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try
        {
            return PathComparer.Equals(
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(left)),
                Path.GetFullPath(right));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string AddAgentDefaults(string text)
    {
        var block = $"{AgentBegin}\n"
            + $"default_subagent_model = \"{EconomyModel}\"\n"
            + $"default_subagent_reasoning_effort = \"{EconomyEffort}\"\n"
            + AgentEnd;
        var insertion = FindTableContentStart(text, "agents");
        if (insertion < 0) return AppendSection(text, $"[agents]\n{block}");
        var prefix = insertion > 0 && text[insertion - 1] == '\n' ? string.Empty : "\n";
        return text.Insert(insertion, $"{prefix}{block}\n");
    }

    private static string AddSkillSwitch(string text, string skillPath, bool enabled)
    {
        var encodedPath = EncodeTomlBasicString(Path.GetFullPath(skillPath));
        var block = $"{SkillBegin}\n"
            + "[[skills.config]]\n"
            + $"path = {encodedPath}\n"
            + $"enabled = {(enabled ? "true" : "false")}\n"
            + SkillEnd;
        return AppendSection(text, block);
    }

    private static string EncodeTomlBasicString(string value)
    {
        var result = new StringBuilder(value.Length + 2);
        result.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\b': result.Append("\\b"); break;
                case '\t': result.Append("\\t"); break;
                case '\n': result.Append("\\n"); break;
                case '\f': result.Append("\\f"); break;
                case '\r': result.Append("\\r"); break;
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                default:
                    if (char.IsControl(character)) result.Append($"\\u{(int)character:X4}");
                    else result.Append(character);
                    break;
            }
        }
        return result.Append('"').ToString();
    }

    private static string AppendSection(string text, string section)
    {
        if (text.Length == 0) return $"{section}\n";
        var separator = text.EndsWith("\n\n", StringComparison.Ordinal)
            ? string.Empty
            : text.EndsWith('\n') ? "\n" : "\n\n";
        return $"{text}{separator}{section}\n";
    }

    private static string RemoveOwnedBlock(string text, string begin, string end)
    {
        var block = FindOwnedBlock(text, begin, end);
        return block is null ? text : text.Remove(block.Start, block.End - block.Start);
    }

    private static OwnedBlock? FindOwnedBlock(string text, string begin, string end)
    {
        var lines = LexTomlLines(text);
        var beginMatches = lines
            .Where(line => line.OutsideMultilineAtStart && string.Equals(line.Text.Trim(), begin, StringComparison.Ordinal))
            .ToArray();
        var endMatches = lines
            .Where(line => line.OutsideMultilineAtStart && string.Equals(line.Text.Trim(), end, StringComparison.Ordinal))
            .ToArray();
        if (beginMatches.Length != endMatches.Length || beginMatches.Length > 1)
        {
            throw new CodexEconomyException($"Malformed managed block: {begin}");
        }
        if (beginMatches.Length == 0) return null;

        var beginMatch = beginMatches[0];
        var endMatch = endMatches[0];
        if (endMatch.Start < beginMatch.NextStart)
        {
            throw new CodexEconomyException($"Malformed managed block: {begin}");
        }
        return new(beginMatch.Start, endMatch.NextStart, text[beginMatch.NextStart..endMatch.Start]);
    }

    private void InstallSkill(CodexEconomyProfile profile)
    {
        var payload = LoadSkillPayload();
        var manifestPath = Path.Combine(profile.SkillDirectory, OwnershipManifestName);
        var manifest = ReadSnapshot(manifestPath);
        if (manifest.Exists && !IsOwnedManifest(manifest.Bytes))
        {
            throw new CodexEconomyException(
                $"Existing Skill ownership manifest is invalid: {manifestPath}");
        }

        if (!manifest.Exists)
        {
            foreach (var asset in payload)
            {
                var target = Path.Combine(profile.SkillDirectory, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var existing = ReadSnapshot(target);
                if (existing.Exists && !existing.Bytes.AsSpan().SequenceEqual(asset.Bytes))
                {
                    throw new CodexEconomyException(
                        $"Existing unmanaged Skill file differs and cannot be adopted: {target}");
                }
            }
        }

        foreach (var asset in payload)
        {
            var target = Path.Combine(profile.SkillDirectory, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var existing = ReadSnapshot(target);
            if (!existing.Bytes.AsSpan().SequenceEqual(asset.Bytes))
            {
                AtomicWrite(target, asset.Bytes, existing);
            }
        }

        var manifestBytes = BuildOwnershipManifest(payload);
        var currentManifest = ReadSnapshot(manifestPath);
        if (!currentManifest.Bytes.AsSpan().SequenceEqual(manifestBytes))
        {
            AtomicWrite(manifestPath, manifestBytes, currentManifest);
        }
        if (!IsSkillInstalled(profile))
        {
            throw new CodexEconomyException("Skill installation read-back failed.");
        }
    }

    private static bool IsSkillInstalled(CodexEconomyProfile profile)
    {
        try
        {
            var payload = LoadSkillPayload();
            if (!IsOwnedManifest(ReadSnapshot(Path.Combine(profile.SkillDirectory, OwnershipManifestName)).Bytes))
            {
                return false;
            }
            return payload.All(asset => ReadSnapshot(
                    Path.Combine(profile.SkillDirectory, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar)))
                .Bytes.AsSpan().SequenceEqual(asset.Bytes));
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<LoadedSkillAsset> LoadSkillPayload()
    {
        var assembly = typeof(CodexEconomyRouter).Assembly;
        var result = new List<LoadedSkillAsset>(SkillAssets.Length);
        foreach (var asset in SkillAssets)
        {
            using var stream = assembly.GetManifestResourceStream(asset.ResourceName)
                ?? throw new CodexEconomyException($"Embedded Skill asset is missing: {asset.RelativePath}");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            result.Add(new(asset.RelativePath, memory.ToArray()));
        }
        return result;
    }

    private static byte[] BuildOwnershipManifest(IReadOnlyList<LoadedSkillAsset> payload)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("skill", SkillName);
            writer.WriteStartObject("files");
            foreach (var asset in payload.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
            {
                writer.WriteString(asset.RelativePath, Convert.ToHexString(SHA256.HashData(asset.Bytes)).ToLowerInvariant());
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return [.. stream.ToArray(), (byte)'\n'];
    }

    private static bool IsOwnedManifest(byte[] bytes)
    {
        if (bytes.Length == 0) return false;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("schemaVersion", out var schema)
                && schema.ValueKind == JsonValueKind.Number
                && schema.TryGetInt32(out var schemaVersion)
                && schemaVersion == 1
                && root.TryGetProperty("skill", out var skill)
                && skill.ValueKind == JsonValueKind.String
                && string.Equals(skill.GetString(), SkillName, StringComparison.Ordinal)
                && root.TryGetProperty("files", out var files)
                && files.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static FileSnapshot ReadSnapshot(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(false, []);
            var bytes = File.ReadAllBytes(path);
            UnixFileMode? unixMode = null;
            if (!OperatingSystem.IsWindows()) unixMode = File.GetUnixFileMode(path);
            return new(true, bytes, unixMode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CodexEconomyException($"Could not read {path}.", exception);
        }
    }

    private static void AtomicWrite(string path, byte[] bytes, FileSnapshot expected)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new CodexEconomyException("Atomic write requires a parent directory.");
        }
        Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, $".{Path.GetFileName(path)}.wmt.lock");
        using var writeLock = AcquireWriteLock(lockPath)
            ?? throw new CodexEconomyException($"File is busy: {path}");
        if (!SnapshotMatches(path, expected))
        {
            throw new CodexEconomyException($"File changed during update; retry: {path}");
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    temporaryPath,
                    expected.UnixMode ?? (UnixFileMode.UserRead | UnixFileMode.UserWrite));
            }
            if (!SnapshotMatches(path, expected))
            {
                throw new CodexEconomyException($"File changed during update; retry: {path}");
            }
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static FileStream? AcquireWriteLock(string lockPath)
    {
        for (var attempt = 0; attempt < LockAttempts; attempt++)
        {
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                return stream;
            }
            catch (IOException) when (attempt < LockAttempts - 1)
            {
                Thread.Sleep(LockDelayMilliseconds);
            }
        }
        return null;
    }

    private static bool SnapshotMatches(string path, FileSnapshot expected)
    {
        if (!File.Exists(path)) return !expected.Exists;
        if (!expected.Exists) return false;
        try
        {
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(expected.Bytes);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static DecodedConfig DecodeConfig(byte[] bytes)
    {
        if (bytes.Length == 0) return new(string.Empty, Environment.NewLine, false);
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        try
        {
            var offset = hasBom ? Encoding.UTF8.Preamble.Length : 0;
            var text = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
            var crlf = Regex.Matches(text, "\r\n").Count;
            var bareLf = text.Count(character => character == '\n') - crlf;
            var newline = crlf > bareLf ? "\r\n" : "\n";
            return new(NormalizeNewlines(text), newline, hasBom);
        }
        catch (DecoderFallbackException exception)
        {
            throw new CodexEconomyException("config.toml must be valid UTF-8.", exception);
        }
    }

    private static byte[] EncodeConfig(string text, string newline, bool bom)
    {
        var normalized = NormalizeNewlines(text);
        if (!normalized.EndsWith('\n')) normalized += "\n";
        var rendered = normalized.Replace("\n", newline, StringComparison.Ordinal);
        var body = Encoding.UTF8.GetBytes(rendered);
        return bom ? [.. Encoding.UTF8.Preamble, .. body] : body;
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string StripComment(string line)
    {
        var inBasic = false;
        var inLiteral = false;
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (inBasic && character == '\\')
            {
                escaped = true;
                continue;
            }
            if (!inLiteral && character == '"') inBasic = !inBasic;
            else if (!inBasic && character == '\'') inLiteral = !inLiteral;
            else if (!inBasic && !inLiteral && character == '#') return line[..index];
        }
        return line;
    }

    private static int IndexOfEquals(string line)
    {
        var inBasic = false;
        var inLiteral = false;
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (inBasic && character == '\\')
            {
                escaped = true;
                continue;
            }
            if (!inLiteral && character == '"') inBasic = !inBasic;
            else if (!inBasic && character == '\'') inLiteral = !inLiteral;
            else if (!inBasic && !inLiteral && character == '=') return index;
        }
        return -1;
    }

    private static string ParseString(string value, string name)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') return value[1..^1];
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try
            {
                using var document = JsonDocument.Parse(value);
                if (document.RootElement.ValueKind != JsonValueKind.String)
                {
                    throw new CodexEconomyException($"{name} must be a string.");
                }
                return document.RootElement.GetString()!;
            }
            catch (JsonException exception)
            {
                throw new CodexEconomyException($"{name} must be a valid TOML basic string.", exception);
            }
        }
        throw new CodexEconomyException($"{name} must be a string.");
    }

    private static bool ParseBoolean(string value, string name) => value switch
    {
        "true" => true,
        "false" => false,
        _ => throw new CodexEconomyException($"{name} must be true or false."),
    };

    private static bool HasNamedConfigLayers(string home)
    {
        try
        {
            return Directory.Exists(home)
                && Directory.EnumerateFiles(home, "*.config.toml", SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void AddCockpitProfiles(
        List<CodexEconomyProfile> result,
        HashSet<string> seen,
        string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath)) return;
        try
        {
            var file = new FileInfo(manifestPath);
            if (file.Length is <= 0 or > 1024 * 1024) return;
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("instances", out var instances)
                || instances.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            foreach (var instance in instances.EnumerateArray())
            {
                if (instance.ValueKind != JsonValueKind.Object
                    || !instance.TryGetProperty("userDataDir", out var homeProperty)
                    || homeProperty.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var home = homeProperty.GetString();
                if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home)) continue;
                var name = instance.TryGetProperty("name", out var nameProperty)
                    && nameProperty.ValueKind == JsonValueKind.String
                    ? nameProperty.GetString()
                    : null;
                var id = instance.TryGetProperty("id", out var idProperty)
                    && idProperty.ValueKind == JsonValueKind.String
                    ? idProperty.GetString()
                    : null;
                var label = !string.IsNullOrWhiteSpace(name)
                    ? name.Trim()
                    : $"Codex Desktop {ShortId(id)}";
                AddProfile(result, seen, home, label, false, "cockpit");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Discovery is best-effort. Explicit paths and the default profile remain available.
        }
    }

    private static void AddProfile(
        List<CodexEconomyProfile> result,
        HashSet<string> seen,
        string home,
        string displayName,
        bool recommended,
        string source)
    {
        try
        {
            var normalized = NormalizeHome(home);
            if (!seen.Add(normalized)) return;
            result.Add(new(displayName, normalized, recommended, source));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Ignore malformed manifest entries without scanning for alternatives.
        }
    }

    private static string NormalizeHome(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (expanded == "~")
        {
            expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else if (expanded.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                 || expanded.StartsWith($"~{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded[2..]);
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
    }

    private static string ProfileDisplayName(string home, string source) => source switch
    {
        "environment" => "Current CODEX_HOME",
        "default" => "Codex default",
        _ => Path.GetFileName(home),
    };

    private static string ShortId(string? id) => string.IsNullOrWhiteSpace(id)
        ? "profile"
        : id.Length <= 8 ? id : id[..8];

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed class RelevantConfig
    {
        public bool AgentsTableSeen { get; set; }
        public bool AgentsDottedSeen { get; set; }
        public bool AgentsEnabledSeen { get; set; }
        public bool? AgentsEnabled { get; set; }
        public string? DefaultModel { get; set; }
        public string? DefaultEffort { get; set; }
        public List<SkillEntry> SkillEntries { get; } = [];
    }

    private sealed class SkillEntryBuilder
    {
        public string? Name { get; set; }
        public string? Path { get; set; }
        public bool EnabledSeen { get; set; }
        public bool Enabled { get; set; }
    }

    private sealed record SkillEntry(string? Name, string? Path, bool? Enabled);
    private sealed record SkillAsset(string RelativePath, string ResourceName);
    private sealed record LoadedSkillAsset(string RelativePath, byte[] Bytes);
    private sealed record OwnedBlock(int Start, int End, string Body);
    private sealed record TomlPhysicalLine(
        int Start,
        int NextStart,
        string Text,
        bool OutsideMultilineAtStart);
    private sealed record DecodedConfig(string Text, string Newline, bool HasBom);
    private sealed record FileSnapshot(bool Exists, byte[] Bytes, UnixFileMode? UnixMode = null);
    private enum TomlMultilineState
    {
        None,
        Basic,
        Literal,
    }
}
