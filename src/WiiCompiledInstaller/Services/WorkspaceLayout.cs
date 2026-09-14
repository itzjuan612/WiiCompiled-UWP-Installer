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
        // The buildable root is whichever level actually carries Launcher\build-uwp-msvc.ps1.
        // (.git alone is not proof: a clone of this repo puts it at the clone root while
        // Launcher lives under wiicompiled\.)
        if (File.Exists(Path.Combine(cloneRoot, "Launcher", "build-uwp-msvc.ps1"))) return cloneRoot;
        var nested = Path.Combine(cloneRoot, "wiicompiled");
        if (File.Exists(Path.Combine(nested, "Launcher", "build-uwp-msvc.ps1"))) return nested;
        if (Directory.Exists(Path.Combine(cloneRoot, ".git"))) return cloneRoot;
        if (Directory.Exists(Path.Combine(nested, ".git"))) return nested;
        return cloneRoot;
    }

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

    /// <summary>
    /// Ensures <paramref name="projectDir"/> holds a usable WiiCompiled tree and returns the
    /// resolved project root (null on failure). Handles the repo's nested layout: a clone of
    /// WiiCompiled-Xbox-UWP carries its buildable tree under wiicompiled\, so the clone may
    /// land in the parent folder and the project root is one level below the clone root.
    /// </summary>
    public async Task<string?> EnsureCloneAsync(string projectDir, IProgress<string> log, CancellationToken ct)
    {
        projectDir = Path.GetFullPath(projectDir);

        var ready = WorkspaceLayout.ProjectRootIn(projectDir);
        if (File.Exists(Path.Combine(ready, "Launcher", "build-uwp-msvc.ps1")))
        {
            log.Report($"Workspace already has the repo: {ready}");
            return ready;
        }

        // A fresh workspace named ...\wiicompiled should clone into its parent, so the
        // checkout's own wiicompiled\ subfolder becomes the project root instead of nesting.
        var target = projectDir;
        if (!Directory.Exists(target) &&
            string.Equals(Path.GetFileName(target), "wiicompiled", StringComparison.OrdinalIgnoreCase))
        {
            target = Path.GetDirectoryName(target)!;
        }

        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            log.Report($"ERROR: target is not an empty folder and is not a WiiCompiled repo:\n  {target}");
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var parent = Path.GetDirectoryName(target)!;
        var leaf = Path.GetFileName(target);
        log.Report($"Cloning {_settings.RepoUrl} -> {target}");
        var r = await _runner.RunAsync("git",
            $"clone --depth 1 --progress \"{_settings.RepoUrl}\" \"{leaf}\"", parent,
            onLine: log, timeout: TimeSpan.FromMinutes(20), ct: ct).ConfigureAwait(false);
        if (!r.Success)
        {
            log.Report($"git clone failed (exit {r.ExitCode}). Check the URL and that git is on PATH.");
            return null;
        }

        var root = WorkspaceLayout.ProjectRootIn(target);
        if (!File.Exists(Path.Combine(root, "Launcher", "build-uwp-msvc.ps1")))
        {
            log.Report("Clone finished, but the expected layout is missing: no Launcher\\build-uwp-msvc.ps1 " +
                       $"was found under {target} (or its wiicompiled\\ subfolder).");
            return null;
        }
        return root;
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
