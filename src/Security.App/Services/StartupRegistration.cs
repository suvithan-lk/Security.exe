using System;
using Microsoft.Win32;

namespace Security.App.Services;

/// <summary>
/// Manages the per-user "start with Windows" entry.
///
/// This only adds/removes a value under the current user's Run key. It does not
/// touch sign-in behaviour, winlogon, credentials, or any Windows
/// authentication mechanism.
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Security";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (key is null)
            throw new InvalidOperationException("Could not open the current user's startup registry key.");

        if (enabled)
        {
            var path = Environment.ProcessPath
                       ?? throw new InvalidOperationException("Could not resolve the executable path.");

            key.SetValue(ValueName, $"\"{path}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }
}
