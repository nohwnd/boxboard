using Microsoft.Win32;

namespace Boxboard.Services;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Boxboard";

    internal static string Command(string executable, string settingsPath)
    {
        if (!Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(settingsPath) ||
            executable.Contains('"') || settingsPath.Contains('"'))
            throw new ArgumentException("Startup requires absolute, unquoted executable and settings paths.");
        return $"\"{executable}\" --settings \"{settingsPath}\"";
    }

    internal static void Apply(bool enabled, string settingsPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true) ??
            throw new InvalidOperationException("The current user's Windows startup key is unavailable.");
        if (enabled)
            key.SetValue(ValueName, Command(Environment.ProcessPath ??
                throw new InvalidOperationException("Boxboard's executable path is unavailable."), settingsPath),
                RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
