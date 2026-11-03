using System.Text.Json;

namespace MuteOnBlur;

sealed class ConfigFile
{
    public List<string> Managed { get; set; } = [];
}

/// <summary>Files under %APPDATA%\MuteOnBlur.</summary>
static class Storage
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MuteOnBlur");
    static readonly string ConfigPath = Path.Combine(Dir, "config.json");
    // Session identifiers we muted and have not unmuted yet. Survives crashes, so a later run can undo them.
    static readonly string MutedByUsPath = Path.Combine(Dir, "muted-by-us.json");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static IEnumerable<string> LoadManaged() => Read<ConfigFile>(ConfigPath)?.Managed ?? [];

    public static void SaveManaged(IEnumerable<string> appIds) =>
        Write(ConfigPath, new ConfigFile { Managed = appIds.Order(StringComparer.OrdinalIgnoreCase).ToList() });

    public static IEnumerable<string> LoadMutedByUs() => Read<List<string>>(MutedByUsPath) ?? [];

    public static void SaveMutedByUs(IEnumerable<string> sessionIds) => Write(MutedByUsPath, sessionIds.Order().ToList());

    static T? Read<T>(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : default;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read {path}", ex);
            return default;
        }
    }

    static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not write {path}", ex);
        }
    }
}
