using Microsoft.Win32;

namespace AsteriaScale.Services;

public sealed class StartupService
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AsteriaScale";

    public bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
        return !string.IsNullOrWhiteSpace(key?.GetValue(ValueName) as string);
    }

    public void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, true);
        if (!enabled)
        {
            key.DeleteValue(ValueName, false);
            return;
        }

        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь приложения.");
        key.SetValue(ValueName, string.Format("\"{0}\" --minimized", executable), RegistryValueKind.String);
    }
}
