using ZGSTokenBar.Core;

namespace ZGSTokenBar.Cli;

internal static partial class CliApplication
{
    private static int EconomyCommand(string[] commandLine, bool asJson)
    {
        var subcommand = Subcommand(commandLine, "status");
        var offset = commandLine.Length == 0 ? 0 : 1;
        CodexEconomyMode? requestedMode = null;
        if (subcommand == "set")
        {
            if (commandLine.Length <= offset)
            {
                return CliOutput.Invalid(asJson, "economy set", "economy set requires off, ask, or on.");
            }
            requestedMode = commandLine[offset].ToLowerInvariant() switch
            {
                "off" => CodexEconomyMode.Off,
                "ask" => CodexEconomyMode.Ask,
                "on" => CodexEconomyMode.On,
                _ => null,
            };
            if (requestedMode is null)
            {
                return CliOutput.Invalid(asJson, "economy set", "economy set requires off, ask, or on.");
            }
            offset++;
        }
        else if (subcommand is not ("status" or "install"))
        {
            return CliOutput.Unknown($"economy {subcommand}", asJson);
        }

        if (!TryEconomyHome(commandLine, offset, out var codexHome, out var argumentError))
        {
            return CliOutput.Invalid(asJson, $"economy {subcommand}", argumentError!);
        }

        var command = requestedMode is { } mode
            ? $"economy set {mode.ToString().ToLowerInvariant()}"
            : $"economy {subcommand}";
        try
        {
            var profile = CodexEconomyRouter.ResolveProfile(codexHome);
            var router = new CodexEconomyRouter();
            var status = subcommand switch
            {
                "install" => router.Install(profile),
                "set" => router.SetMode(profile, requestedMode!.Value),
                _ => router.Inspect(profile),
            };
            CliOutput.Write(
                asJson,
                command,
                EconomyResult(status),
                null,
                EconomyText(status));
            return status.Mode == CodexEconomyMode.Inconsistent ? 4 : 0;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            CliOutput.Write(
                asJson,
                command,
                null,
                new("codex_economy_conflict", exception.Message, true));
            return 4;
        }
    }

    private static bool TryEconomyHome(
        string[] commandLine,
        int offset,
        out string? codexHome,
        out string? error)
    {
        codexHome = null;
        error = null;
        while (offset < commandLine.Length)
        {
            if (!string.Equals(commandLine[offset], "--codex-home", StringComparison.OrdinalIgnoreCase))
            {
                error = $"Unknown economy option: {commandLine[offset]}.";
                return false;
            }
            if (codexHome is not null)
            {
                error = "--codex-home can be specified only once.";
                return false;
            }
            if (++offset >= commandLine.Length || string.IsNullOrWhiteSpace(commandLine[offset]))
            {
                error = "--codex-home requires a directory.";
                return false;
            }
            codexHome = commandLine[offset++];
        }
        return true;
    }

    private static System.Text.Json.JsonElement EconomyResult(CodexEconomyStatus status) =>
        CliOutput.ObjectElement(
            ("mode", status.Mode.ToString().ToLowerInvariant()),
            ("codexHome", status.Profile.HomeDirectory),
            ("configPath", status.Profile.ConfigPath),
            ("skillPath", status.Profile.SkillPath),
            ("skillInstalled", status.SkillInstalled),
            ("hasNamedConfigLayers", status.HasNamedConfigLayers),
            ("diagnostic", status.Diagnostic));

    private static string EconomyText(CodexEconomyStatus status)
    {
        var mode = status.Mode.ToString().ToLowerInvariant();
        var installed = status.SkillInstalled ? "installed" : "not installed";
        var warning = status.HasNamedConfigLayers
            ? $"{Environment.NewLine}Warning: named config layers may override this base configuration."
            : string.Empty;
        return $"{mode}{Environment.NewLine}Codex home: {status.Profile.HomeDirectory}"
            + $"{Environment.NewLine}Skill: {installed}{warning}";
    }
}
