using Microsoft.Win32;

namespace ZGSTokenBar.App;

internal static class StartupManager
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ZGSTokenBar";

    public static void Apply(bool openAtLogin, bool keepRunning)
    {
        var command = BuildCommand(Application.ExecutablePath, openAtLogin, keepRunning);
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, true);
            if (command is not null)
            {
                key.SetValue(ValueName, command);
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch
        {
            // Startup registration is optional and must never prevent the bar from running.
        }
        WatchdogManager.Apply(keepRunning);
    }

    internal static string? BuildCommand(string executablePath, bool openAtLogin, bool keepRunning)
    {
        if (!openAtLogin && !keepRunning) return null;
        var command = $"\"{executablePath}\"";
        return keepRunning ? $"{command} --watchdog" : command;
    }
}
