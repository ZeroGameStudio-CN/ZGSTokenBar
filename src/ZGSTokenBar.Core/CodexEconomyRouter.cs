using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZGSTokenBar.Core;

public enum CodexEconomyMode
{
    // Off/Ask/On are read-only migration states from previous installations.
    Unconfigured,
    Off,
    Ask,
    On,
    Task,
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
    string? Diagnostic = null)
{
    public bool Ready => Mode == CodexEconomyMode.Task && SkillInstalled;
}

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
    public const string PolicyName = "task-scoped-confirmation";

    internal const string AgentBegin = "# BEGIN sol-luna-delegation economy agent defaults";
    internal const string AgentEnd = "# END sol-luna-delegation economy agent defaults";
    internal const string SkillBegin = "# BEGIN sol-luna-delegation economy skill switch";
    internal const string SkillEnd = "# END sol-luna-delegation economy skill switch";
    internal const string TaskPolicy = "# policy: " + PolicyName;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

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
        if (mode == CodexEconomyMode.Unconfigured && installed) mode = CodexEconomyMode.Task;
        var hasNamedLayers = HasNamedConfigLayers(profile.HomeDirectory);
        var diagnostic = mode == CodexEconomyMode.Inconsistent
            ? "managed_configuration_inconsistent"
            : mode is CodexEconomyMode.Off or CodexEconomyMode.Ask or CodexEconomyMode.On
                ? "legacy_economy_configuration"
                : "externally_managed_read_only";
        return new(mode, profile, installed, hasNamedLayers, diagnostic);
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
            var externalEntries = unmanagedConfig.SkillEntries.Where(entry => EntryTargetsSkill(entry, skillPath)).ToArray();
            if (externalEntries.Length > 1 || (externalEntries.Length > 0 && skillBlock is not null))
                return CodexEconomyMode.Inconsistent;
            if (agentBlock is null && skillBlock is null)
            {
                if (unmanagedConfig.AgentsEnabled == false || externalEntries.FirstOrDefault()?.Enabled == false)
                    return CodexEconomyMode.Off;
                return externalEntries.Length == 0 ? CodexEconomyMode.Unconfigured : CodexEconomyMode.Task;
            }
            if (skillBlock is null) return CodexEconomyMode.Inconsistent;
            if (!HasOnlyManagedFields(skillBlock.Body, skill: true)
                || (agentBlock is not null && !HasOnlyManagedFields(agentBlock.Body, skill: false)))
            {
                return CodexEconomyMode.Inconsistent;
            }

            var managedSkill = ParseRelevant(skillBlock.Body);
            if (managedSkill.SkillEntries.Count != 1
                || !SamePath(managedSkill.SkillEntries[0].Path, skillPath)
                || (managedSkill.SkillEntries[0].Name is { } name
                    && !string.Equals(name.Trim(), SkillName, StringComparison.Ordinal)))
            {
                return CodexEconomyMode.Inconsistent;
            }

            var skillEntry = managedSkill.SkillEntries[0];
            if (agentBlock is null)
            {
                if (skillEntry.Enabled == false) return CodexEconomyMode.Off;
                if (skillEntry.Enabled == true && unmanagedConfig.AgentsEnabled != false)
                {
                    return LexTomlLines(skillBlock.Body).Any(line => line.OutsideMultilineAtStart
                            && string.Equals(line.Text.Trim(), TaskPolicy, StringComparison.Ordinal))
                        ? CodexEconomyMode.Task
                        : CodexEconomyMode.Ask;
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

    private static bool HasOnlyManagedFields(string body, bool skill)
    {
        var allowed = skill ? new[] { "path", "name", "enabled" }
            : new[] { "default_subagent_model", "default_subagent_reasoning_effort" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var headerSeen = false;
        foreach (var physical in LexTomlLines(body))
        {
            if (!physical.OutsideMultilineAtStart) return false;
            var line = StripComment(physical.Text).Trim();
            if (line.Length == 0) continue;
            if (skill && line.StartsWith("[[", StringComparison.Ordinal) && line.EndsWith("]]", StringComparison.Ordinal))
            {
                if (headerSeen || !PathEquals(ParseKeyPath(line[2..^2], "managed header"), "skills", "config")) return false;
                headerSeen = true;
                continue;
            }
            var equals = IndexOfEquals(line);
            if (equals <= 0) return false;
            var key = ParseKeyPath(line[..equals], "managed key");
            if (key.Length != 1 || !allowed.Contains(key[0], StringComparer.Ordinal) || !seen.Add(key[0])) return false;
        }
        return !skill || headerSeen;
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

    // Observation only: ownership and content validation belong to the skill source.
    private static bool IsSkillInstalled(CodexEconomyProfile profile) => File.Exists(profile.SkillPath);

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
