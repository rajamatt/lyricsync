using System.Text.Json;

namespace LyricSync.Services;

public sealed class AppSettings
{
    /// <summary>
    /// Opacity of the overlay background panel only (text is always fully opaque).
    /// Defaults to 0 — no backing panel, just the outlined lyrics floating over whatever
    /// is behind, which reads cleanest over games. Raise it for a dark subtitle box.
    /// </summary>
    public double OverlayOpacity { get; set; }
    public double OverlayFontSize { get; set; } = 30;
    /// <summary>Hex color of the current lyric line. Never pure black (#000000 is the transparency key).</summary>
    public string LyricColor { get; set; } = "#FFEB3B";
    public bool OverlayVisible { get; set; } = true;
    public int? OverlayX { get; set; }
    public int? OverlayY { get; set; }
    public int? OverlayWidth { get; set; }
    public int? OverlayHeight { get; set; }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LyricSync", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
        }
        catch
        {
            // Fall through to defaults on corrupt settings.
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Settings persistence is best-effort.
        }
    }
}
