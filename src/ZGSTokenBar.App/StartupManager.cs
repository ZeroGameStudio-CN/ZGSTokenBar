using Microsoft.Win32;
using System.Diagnostics;

namespace ZGSTokenBar.App;

internal static class StartupManager
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ZGSTokenBar";

    public static void Apply(bool openAtLogin)
    {
        ReconcileRegistration(Application.ExecutablePath, openAtLogin);
    }

    public static void ReconcileRegistration(string executablePath, bool openAtLogin)
    {
        var command = BuildCommand(executablePath, openAtLogin);
        var intendedAction = command is null ? "remove" : "set";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, true);
            var currentCommand = key.GetValue(
                ValueName,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            switch (RequiredAction(currentCommand, command))
            {
                case StartupRegistrationAction.Set:
                    key.SetValue(ValueName, command!, RegistryValueKind.String);
                    break;
                case StartupRegistrationAction.Delete:
                    key.DeleteValue(ValueName, false);
                    break;
            }
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "ZGSTokenBar startup registration reconciliation failed while trying to {0} the command ({1}).",
                intendedAction,
                exception.GetType().Name);
        }
    }

    internal static string? BuildCommand(string executablePath, bool openAtLogin)
    {
        return openAtLogin ? $"\"{executablePath}\"" : null;
    }

    internal static StartupRegistrationAction RequiredAction(
        string? currentCommand,
        string? desiredCommand)
    {
        if (desiredCommand is null)
        {
            return currentCommand is null
                ? StartupRegistrationAction.None
                : StartupRegistrationAction.Delete;
        }

        return string.Equals(currentCommand, desiredCommand, StringComparison.Ordinal)
            ? StartupRegistrationAction.None
            : StartupRegistrationAction.Set;
    }
}

internal enum StartupRegistrationAction
{
    None,
    Set,
    Delete,
}
