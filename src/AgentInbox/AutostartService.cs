using Microsoft.Win32;

namespace AgentInbox;

/// <summary>Optional per-user autostart via HKCU Run. No elevation required.</summary>
public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AgentInbox";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;
            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            var path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path)) return;
            key.SetValue(ValueName, $"\"{path}\" --silent");
        }
        catch (Exception ex)
        {
            Log.Warn($"autostart update failed: {ex.Message}");
        }
    }
}
