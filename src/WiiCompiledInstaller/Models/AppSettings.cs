using System.Text.Json.Serialization;
using WiiCompiledInstaller.Services;

namespace WiiCompiledInstaller.Models;

/// <summary>
/// Portable application settings, stored as settings.json next to the executable.
/// Paths default to being relative to the installer root (the folder containing this exe).
/// </summary>
public sealed class AppSettings
{
    // --- Workspace / repo ---------------------------------------------------
    /// <summary>Folder that contains (or will contain) the cloned WiiCompiled repo.</summary>
    [JsonIgnore]
    public string WorkspaceDir { get; set; } = "";

    /// <summary>The upstream repository to clone when the workspace is empty.</summary>
    public string RepoUrl { get; set; } = "https://github.com/itzjuan612/WiiCompiled-Xbox-UWP.git";

    // --- Signing ------------------------------------------------------------
    /// <summary>PFX used to sign the .appx (default: bundled certs\mkwii-dev.pfx).</summary>
    public string PfxPath { get; set; } = "";
    public string PfxPassword { get; set; } = "";

    // --- User inputs (never bundled or distributed) -------------------------
    /// <summary>Folder that holds main.dol / StaticR.rel from the user's own PAL RMCP01 dump.</summary>
    public string DiscDumpDirectory { get; set; } = "";
    /// <summary>Path to the Retro Rewind pack root (the RetroRewind6 folder).</summary>
    public string RetroRewindDirectory { get; set; } = "";
    /// <summary>Python interpreter (with jinja2+markupsafe) for Dawn/SDL codegen. Empty = auto-detect.</summary>
    public string PythonPath { get; set; } = "";

    // --- Xbox Dev Portal ------------------------------------------------------
    public string XboxPortalUrl { get; set; } = "";
    public string XboxPortalUser { get; set; } = "";
    public string XboxPortalPassword { get; set; } = "";

    // --- Build ---------------------------------------------------------------
    public int ParallelJobs { get; set; } = 0; // 0 = auto (RAM-based, mirrors LocalBuild.ps1)

    // --- Package versioning ----------------------------------------------------
    /// <summary>Current package version (major.MINOR.PATCH.build) written to AppxManifest.xml.</summary>
    public string PackageVersion { get; set; } = "1.0.0.0";

    /// <summary>[video] defaults written into the packaged Config.default.toml.</summary>
    public ConfigDefaults Defaults { get; set; } = new();

    public static AppSettings LoadOrReuse(string path, string installerRoot)
    {
        AppSettings? settings = null;
        try
        {
            if (File.Exists(path))
                settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(path), SettingsStore.JsonOptions);
        }
        catch
        {
            // Corrupt settings fall back to defaults; the file is rewritten on next save.
        }

        settings ??= new AppSettings();

        if (string.IsNullOrWhiteSpace(settings.WorkspaceDir))
            settings.WorkspaceDir = Path.Combine(installerRoot, "workspace", "repo", "wiicompiled");
        if (string.IsNullOrWhiteSpace(settings.PfxPath))
        {
            var bundled = Path.Combine(installerRoot, "certs", "mkwii-dev.pfx");
            settings.PfxPath = File.Exists(bundled) ? bundled : Path.Combine(installerRoot, "certs", "mkwii-dev.pfx");
        }

        return settings;
    }
}

/// <summary>The [video] section values the installer writes into the packaged Config.default.toml.</summary>
public sealed class ConfigDefaults
{
    public bool Widescreen { get; set; } = true;
    public int ResolutionMultiplier { get; set; } = 2;
    public int FrameInterpolationFps { get; set; } = 0;   // 0 = off (60 fps native)
    public bool SkipUnreadyPipelines { get; set; } = true;
    public int PipelineCompileWorkers { get; set; } = 0;  // 0 = platform default
    public int PrewarmMinFreeMb { get; set; } = 0;        // 0 = driver default (900 on Xbox)
    public string OverlayHotkey { get; set; } = "left_shoulder+right_shoulder+north";
}
