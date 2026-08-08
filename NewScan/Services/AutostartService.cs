using Microsoft.Win32;

namespace NewScan.Services;

/// <summary>
/// Avvio automatico all'accesso a Windows, ora <b>opt-in</b>.
///
/// La versione originale scriveva la chiave di Run in modo silenzioso allo splash
/// (SplashScreen.cs). Qui la scrittura/rimozione avviene solo su scelta esplicita
/// dell'utente tramite l'apposito ToggleSwitch.
/// </summary>
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ScanApp";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrEmpty(value);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null) return;

        if (enabled)
        {
            var exePath = System.Windows.Forms.Application.ExecutablePath;
            if (!string.IsNullOrEmpty(exePath))
                key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
