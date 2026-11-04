using System.Xml;
using System.Xml.Linq;

namespace MuteOnBlur;

/// <summary>
/// Finds the name the volume mixer shows for an app: the session's display name, else the display
/// name of the package the process belongs to, else the title of the process's main window.
/// Not thread-safe; only the audio thread calls it.
/// </summary>
static class AppNames
{
    // Keyed by package full name, which includes the version, so entries never go stale.
    static readonly Dictionary<string, string?> PackageNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The mixer name for one app's sessions, or null if no source has one.</summary>
    public static string? Find(IReadOnlyCollection<AudioSession> sessions)
    {
        var pids = sessions.Select(s => s.Pid).Distinct().ToList();
        return First(sessions.Select(s => s.DisplayName))
            ?? First(pids.Select(PackageName))
            ?? First(pids.Select(Win32.GetMainWindowTitle));
    }

    static string? First(IEnumerable<string?> names) =>
        names.Select(n => n?.Trim()).FirstOrDefault(n => !string.IsNullOrEmpty(n));

    static string? PackageName(uint pid)
    {
        if (Win32.GetPackageFullName(pid) is not { } fullName) return null;
        if (!PackageNames.TryGetValue(fullName, out var name)) PackageNames[fullName] = name = ReadPackageName(fullName);
        return name;
    }

    /// <summary>
    /// The package-level DisplayName from its manifest. The mixer uses this rather than the entry of
    /// the app that plays (Apple Music plays from a helper whose entry is named "AMPLibraryAgent").
    /// </summary>
    static string? ReadPackageName(string fullName)
    {
        try
        {
            if (Win32.GetPackagePath(fullName) is not { } dir) return null;
            var root = XDocument.Load(Path.Combine(dir, "AppxManifest.xml")).Root;
            var identity = Child(root, "Identity")?.Attribute("Name")?.Value;
            var name = Child(Child(root, "Properties"), "DisplayName")?.Value.Trim();
            if (string.IsNullOrEmpty(name) || identity == null) return null;
            const string prefix = "ms-resource:";
            return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? LoadResource(fullName, identity, name[prefix.Length..])
                : name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            Log.Warning($"Could not read the manifest of {fullName}: {ex.Message}");
            return null;
        }
    }

    // Manifests are namespaced differently across schema versions; match on the local name.
    static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    /// <summary>Resolves an "ms-resource:" reference. A bare key lives in the package's "resources" map.</summary>
    static string? LoadResource(string fullName, string packageName, string key)
    {
        string uri = key.StartsWith("//") ? "ms-resource:" + key
            : key.StartsWith('/') ? $"ms-resource://{packageName}{key}"
            : key.Contains('/') ? $"ms-resource://{packageName}/{key}"
            : $"ms-resource://{packageName}/resources/{key}";
        return Win32.LoadIndirectString($"@{{{fullName}?{uri}}}");
    }
}
