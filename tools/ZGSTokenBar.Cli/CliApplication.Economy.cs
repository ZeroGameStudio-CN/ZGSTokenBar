using ZGSTokenBar.Core;

namespace ZGSTokenBar.Cli;

internal static partial class CliApplication
{
    private static int EconomyCommand(string[] commandLine, bool asJson)
    {
        var subcommand = Subcommand(commandLine, "status");
        var offset = commandLine.Length == 0 ? 0 : 1;
        if (subcommand is "set" or "install")
        {
            return CliOutput.Invalid(asJson, $"economy {subcommand}",
                "TokenBar is read-only. Install, update or configure this skill through your skill source repository.");
        }
        if (subcommand != "status")
        {
            return CliOutput.Unknown($"economy {subcommand}", asJson);
        }

        if (!TryEconomyHome(commandLine, offset, out var codexHome, out var argumentError))
        {
            return CliOutput.Invalid(asJson, $"economy {subcommand}", argumentError!);
        }

        var command = $"economy {subcommand}";
        try
        {
            var profile = CodexEconomyRouter.ResolveProfile(codexHome);
            var router = new CodexEconomyRouter();
            var status = router.Inspect(profile);
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
            ("management", "external"),
            ("readOnly", true),
            ("ready", status.Ready),
            ("codexHome", status.Profile.HomeDirectory),
            ("configPath", status.Profile.ConfigPath),
            ("skillPath", status.Profile.SkillPath),
            ("skillInstalled", status.SkillInstalled),
            ("hasNamedConfigLayers", status.HasNamedConfigLayers),
            ("diagnostic", status.Diagnostic));

    private static string EconomyText(CodexEconomyStatus status)
    {
        var installed = status.SkillInstalled ? "installed" : "not installed";
        var readiness = status.Ready ? "detected" : "not ready";
        return $"{readiness}{Environment.NewLine}Codex home: {status.Profile.HomeDirectory}"
            + $"{Environment.NewLine}Skill: {installed}"
            + $"{Environment.NewLine}Read-only. Managed by your skill source repository; current task loading is not verified.";
    }
}
