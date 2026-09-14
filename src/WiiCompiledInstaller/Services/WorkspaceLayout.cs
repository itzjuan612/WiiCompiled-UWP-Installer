using WiiCompiledInstaller.Models;

namespace WiiCompiledInstaller.Services;

/// <summary>
/// Well-known paths inside a WiiCompiled workspace (the cloned repo), so every service
/// resolves them the same way. Mirrors the layout the repo's Launcher scripts expect.
/// </summary>
public sealed class WorkspaceLayout
{
    /// <summary>
    /// The WiiCompiled tree inside a clone of itzjuan612/WiiCompiled-Xbox-UWP:
    /// the repo root carries a wiicompiled/ subfolder (plus README/certs), so a
    /// clone's buildable root is <clone>\wiicompiled unless the repo itself is
    /// the project root.
    /// </summary>
    public static string ProjectRootIn(string cloneRoot)
    {
        if (LooksLikeRepoRoot(cloneRoot)) return cloneRoot;
        var nested = Path.Combine(cloneRoot, "wiicompiled");
        return LooksLikeRepoRoot(nested) ? nested : cloneRoot;
    }

    private static bool LooksLikeRepoRoot(string dir) =>
        Directory.Exists(Path.Combine(dir, ".git")) ||
        File.Exists(Path.Combine(dir, "Launcher", "build-uwp-msvc.ps1"));

    public string Root { get; }
    public WorkspaceLayout(string root) { Root = Path.GetFullPath(root); }

    public string Launcher   => Path.Combine(Root, "Launcher");
    public string Assets     => Path.Combine(Root, "Assets");
    public string Generated  => Path.Combine(Root, "generated");
    public string PulsarPacks=> Path.Combine(Root, "PulsarPacks", "completed", "RetroRewind", "RetroRewind6");
    public string NativeBuildUwp => Path.Combine(Root, "native-build-uwp");
    public string UwpAppx    => Path.Combine(Launcher, "uwp-appx");
    public string PackageDir => Path.Combine(UwpAppx, "package");
    public string Manifest   => Path.Combine(PackageDir, "AppxManifest.xml");
    public string ConfigDefault => Path.Combine(PackageDir, "Config.default.toml");
    public string PortableTools => Path.Combine(Launcher, "artifacts", "portable-tools");
    public string CMakeExe   => Path.Combine(PortableTools, "CMake", "bin", "cmake.exe");
    public string NinjaExe   => Path.Combine(PortableTools, "ninja", "ninja.exe");
    public bool IsClone => Directory.Exists(Path.Combine(Root, ".git"));
    public bool HasLauncher => File.Exists(Path.Combine(Launcher, "build-uwp-msvc.ps1"));
}

/// <summary>
/// Clones the WiiCompiled-UWP-Xbox repo into the workspace (auto-clone if missing,
/// reuse if present) and keeps it up to date. A shallow single-branch clone is enough:
/// the pipeline only ever builds the tip of main.
/// </summary>
public sealed class RepoService
{
    private readonly ProcessRunner _runner;
    private readonly AppSettings _settings;

    public RepoService(ProcessRunner runner, AppSettings settings)
    {
        _runner = runner;
        _settings = settings;
    }

    public static bool LooksLikeRepo(string dir) =>
        Directory.Exists(dir) &&
        File.Exists(Path.Combine(dir, ".git", "HEAD")) &&
        File.Exists(Path.Combine(dir, "Launcher", "build-uwp-msvc.ps1"));

    public async Task<bool> EnsureCloneAsync(string targetDir, IProgress<string> log, CancellationToken ct)
    {
        if (LooksLikeRepo(targetDir))
        {
            log.Report($"Workspace already has the repo: {targetDir}");
            return true;
        }

        if (Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
        {
            log.Report($"ERROR: target is not an empty folder and is not a WiiCompiled repo:\n  {targetDir}");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
        var parent = Path.GetDirectoryName(targetDir)!;
        var leaf = Path.GetFileName(targetDir);
        log.Report($"Cloning {_settings.RepoUrl} -> {targetDir}");
        var r = await _runner.RunAsync("git",
            $"clone --depth 1 --progress \"{_settings.RepoUrl}\" \"{leaf}\"", parent,
            onLine: log, timeout: TimeSpan.FromMinutes(20), ct: ct).ConfigureAwait(false);
        if (!r.Success)
        {
            log.Report($"git clone failed (exit {r.ExitCode}). Check the URL and that git is on PATH.");
            return false;
        }
        return LooksLikeRepo(targetDir);
    }

    public async Task<bool> PullAsync(WorkspaceLayout layout, IProgress<string> log, CancellationToken ct)
    {
        log.Report("Pulling latest main...");
        var r = await _runner.RunAsync("git", "pull --ff-only origin main", layout.Root,
            onLine: log, timeout: TimeSpan.FromMinutes(5), ct: ct).ConfigureAwait(false);
        if (!r.Success)
        {
            log.Report($"git pull failed (exit {r.ExitCode}) — continuing with the current checkout.");
            return false;
        }
        return true;
    }
}
