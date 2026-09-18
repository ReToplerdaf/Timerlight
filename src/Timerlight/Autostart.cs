using Microsoft.Win32;

namespace Timerlight;

/// <summary>Registers the widget under the per-user Run key so it comes back after a reboot.</summary>
internal static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Timerlight";

    internal static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string stored && stored.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Returns true when the registry actually accepted the change.</summary>
    internal static bool SetEnabled(bool enabled)
    {
        string? executablePath = Environment.ProcessPath;
        if (enabled && string.IsNullOrEmpty(executablePath))
        {
            return false;
        }

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new IOException("The Run key could not be opened.");

            if (enabled)
            {
                key.SetValue(ValueName, "\"" + executablePath + "\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
