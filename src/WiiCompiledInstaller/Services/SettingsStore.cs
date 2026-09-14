using System.Text.Json;
using System.Text.Json.Serialization;
using WiiCompiledInstaller.Models;

namespace WiiCompiledInstaller.Services;

/// <summary>
/// Reads and writes the portable settings.json that lives next to the executable,
/// and exposes the installer root (that same folder) so all other paths can be
/// resolved relative to it. Keeping settings beside the exe is what makes the whole
/// tool folder-drop portable.
/// </summary>
public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string InstallerRoot { get; }
    public string SettingsPath { get; }

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? Saved;

    public SettingsStore()
    {
        InstallerRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        // When running from bin\...\net8.0-windows under a dev checkout, walk up to the repo root
        // that actually holds certs\ and workspace\. In a published folder this is already the root.
        InstallerRoot = ResolvePortableRoot(InstallerRoot);
        SettingsPath = Path.Combine(InstallerRoot, "settings.json");
        Current = AppSettings.LoadOrReuse(SettingsPath, InstallerRoot);
        NormalizeWorkspace();
    }

    /// <summary>
    /// The configured workspace folder may point at the clone root while the buildable
    /// WiiCompiled tree lives one level below it (a clone of WiiCompiled-Xbox-UWP carries
    /// a wiicompiled\ subfolder). Resolve that once here so every service - GUI and
    /// headless - operates on the project root.
    /// </summary>
    private void NormalizeWorkspace() =>
        Current.WorkspaceDir = WorkspaceLayout.ProjectRootIn(Current.WorkspaceDir);

    private static string ResolvePortableRoot(string start)
    {
        // Published layout: the exe sits directly beside certs\ and settings.json.
        // Dev layout: the exe is under bin\Debug\...\win-x64, where the csproj also
        // copies a certs\ for local runs — so the solution file is the only marker
        // that reliably says "repo root". Look for it up the chain first.
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WiiCompiledInstaller.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        dir = new DirectoryInfo(start);
        while (dir?.Parent is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "certs")) &&
                !dir.FullName.Contains("bin" + Path.DirectorySeparatorChar))
                return dir.FullName;
            dir = dir.Parent;
        }
        return start;
    }

    public void Save()
    {
        Directory.CreateDirectory(InstallerRoot);
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        // Write atomically so a crash mid-save never leaves a half-written settings.json.
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, SettingsPath, overwrite: true);
        Saved?.Invoke(Current);
    }

    public void Reload()
    {
        Current = AppSettings.LoadOrReuse(SettingsPath, InstallerRoot);
        NormalizeWorkspace();
    }
}
