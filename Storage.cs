using System.Text.Encodings.Web;
using System.Text.Json;

namespace MuteOnBlur;

sealed class ConfigFile
{
    public List<string> Managed { get; set; } = [];
    // Mixer names of managed apps as first seen running, shown while the app is not running.
    public Dictionary<string, string> Names { get; set; } = [];
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
        // Keeps non-ASCII app names readable instead of escaping them.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ConfigFile LoadConfig()
    {
        var config = Read<ConfigFile>(ConfigPath) ?? new();
        config.Managed ??= [];
        config.Names ??= [];
        return config;
    }

    public static void SaveConfig(IEnumerable<string> managed, IReadOnlyDictionary<string, string> names) =>
        Write(ConfigPath, new ConfigFile
        {
            Managed = managed.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            Names = names.OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(),
        });

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
