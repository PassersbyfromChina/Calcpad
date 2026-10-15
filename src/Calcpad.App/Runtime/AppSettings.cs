using System.Text.Json;
using System.Text.Json.Serialization;
using Calcpad.Core.Hosting;

namespace Calcpad.App;

/// <summary>Small, portable shell settings file kept next to the workspace.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Theme { get; set; } = "Dark";

    public bool ShowGrid { get; set; } = true;

    public bool SnapToGrid { get; set; }

    public double GridSize { get; set; } = 40;

    public string? LastDocumentPath { get; set; }

    public List<string> RecentDocuments { get; set; } = [];

    public string Locale { get; set; } = "zh-Hans";

    public bool HasCompletedFirstRun { get; set; }

    [JsonIgnore]
    public string FilePath { get; private set; } = string.Empty;

    public static AppSettings Load(AppPaths paths)
    {
        var path = Path.Combine(paths.DataDirectory, "settings.json");
        AppSettings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception)
        {
            settings = new AppSettings();
        }

        settings.FilePath = path;
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception)
        {
            // Settings are best effort; never block the user.
        }
    }

    public void RememberDocument(string path)
    {
        LastDocumentPath = path;
        RecentDocuments.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentDocuments.Insert(0, path);
        if (RecentDocuments.Count > 12)
        {
            RecentDocuments.RemoveRange(12, RecentDocuments.Count - 12);
        }
    }
}
