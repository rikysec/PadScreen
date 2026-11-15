using System.Text.Json;

namespace PadScreen;

/// <summary>Impostazioni salvate in %APPDATA%\PadScreen\settings.json.</summary>
internal sealed class Settings
{
    public bool VirtualDisplay { get; set; } = true;
    public string? MirrorDevice { get; set; }
    public bool HalfResolution { get; set; }
    public int Fps { get; set; } = 60;
    public int BitrateMbps { get; set; } = 20;
    public string Encoder { get; set; } = "auto";
    public bool MouseMode { get; set; }
    public string? FfmpegPath { get; set; }
    public string? LastTarget { get; set; }
    public string? ManualAddress { get; set; }
    public bool AutoConnect { get; set; }
    /// <summary>Encoder funzionante per scheda video + preferenza (evita di riprovarli ogni volta).</summary>
    public Dictionary<string, string> EncoderCache { get; set; } = new();

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PadScreen");
    private static string FilePath => Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    private static readonly object SaveLock = new();

    public void Save()
    {
        lock (SaveLock)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
            }
            catch { }
        }
    }
}
