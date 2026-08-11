using System.Text.Json;
using System.Text.Json.Serialization;
using QdlScan.Models;

namespace QdlScan.Services;

/// <summary>
/// Impostazioni persistite in %AppData%\QdlScan\settings.json
/// (sostituisce il vecchio Properties.Settings di WinForms).
/// </summary>
public sealed class AppSettings
{
    public int Port { get; set; } = 8787;

    /// <summary>Origini extra ammesse oltre a quelle locali (es. "https://app.miosito.it").</summary>
    public List<string> AllowedOrigins { get; set; } = new() { "https://app.quellideilibri.it" };

    public int DefaultDpi { get; set; } = 300;

    public ColorMode DefaultColorMode { get; set; } = ColorMode.Color;

    // --- persistenza ---

    [JsonIgnore]
    private static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QdlScan");

    [JsonIgnore]
    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null) return loaded;
            }
        }
        catch { /* file corrotto/illeggibile: si riparte dai default */ }

        // Primo avvio (o file illeggibile): genera il template ai valori predefiniti,
        // così l'utente ha un file da editare per porta/allowlist.
        var defaults = new AppSettings();
        defaults.Save();
        return defaults;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { /* salvataggio best effort */ }
    }
}
