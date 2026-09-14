using System.Security.Cryptography;

namespace WiiCompiledInstaller.Services;

/// <summary>
/// Acquires the UWP-only dependency state that the repo's own prepare scripts
/// do not cover, from the port patches/overlays bundled with this installer:
///  - the SternXD SDL3-uwp fork (CoreWindow video + pointer drivers), pinned
///    revision + the audio/gamepad port patch;
///  - the pinned google/dawn source tree + its UWP port patch;
///  - hand-edited non-git trees (tracy, abseil) reproduced as file overlays.
/// Detection is deterministic: git-status probes for the git trees (fresh
/// checkouts are clean; only our patch dirties them at bootstrap time) and
/// hash comparison for overlays. Re-runs are a no-op.
/// </summary>
public sealed class UwpDependencyService
{
    // Revisions the shipped port was built and validated against.
    public const string SdlUwpRepository = "https://github.com/SternXD/SDL3-uwp.git";
    public const string SdlUwpRevision = "ddd5d4f99a26ac95d4620ccf489c07fcf5f08636";
    public const string SdlUwpPatch = "sdl-uwp-audio-gamepad.port.patch";
    public const string DawnRepository = "https://github.com/google/dawn.git";
    public const string DawnRevision = "43759363534b6a96b2326ce1085c7f5e6876a88c";
    public const string DawnPatch = "dawn-uwp.port.patch";

    private readonly ProcessRunner _runner = new();
    private readonly string _patchesDir;

    public string DependenciesDir { get; }
    public string SdlUwpDir => Path.Combine(DependenciesDir, "SDL-uwp");
    public string DawnDir => Path.Combine(DependenciesDir, "dawn");

    public UwpDependencyService(string workspaceRoot, string? patchesDir = null)
    {
        DependenciesDir = Path.Combine(workspaceRoot, "Launcher", "artifacts", "dependencies");
        _patchesDir = patchesDir ?? ResolvePatchesDir(workspaceRoot);
    }

    public static string ResolvePatchesDir(string workspaceRoot)
    {
        var candidates = new List<string>();
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 5 && dir != null; i++, dir = Path.GetDirectoryName(dir))
            candidates.Add(Path.Combine(dir, "patches"));
        dir = Path.GetFullPath(workspaceRoot);
        for (var i = 0; i < 4 && dir != null; i++, dir = Path.GetDirectoryName(dir))
            candidates.Add(Path.Combine(dir, "patches"));
        foreach (var c in candidates)
            if (File.Exists(Path.Combine(c, DawnPatch))) return c;
        throw new FileNotFoundException(
            $"Port patches missing: could not find a 'patches' folder containing '{DawnPatch}'.");
    }

    public async Task<bool> EnsureAsync(IProgress<string> progress, CancellationToken ct)
    {
        return await EnsureSdlUwpAsync(progress, ct)
            && await EnsureDawnAsync(progress, ct)
            && ApplyOverlays(progress);
    }

    private async Task<bool> EnsureSdlUwpAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (!HasGitHead(SdlUwpDir))
        {
            progress.Report($"== Cloning SternXD/SDL3-uwp @ {SdlUwpRevision[..9]} ==");
            Directory.CreateDirectory(DependenciesDir);
            if (!await Git(progress, $"clone --quiet {SdlUwpRepository} \"{SdlUwpDir}\"", ct)) return false;
        }
        if (!await Git(progress, $"-C \"{SdlUwpDir}\" config core.longpaths true", ct)) return false;
        var head = (await GitCapture(SdlUwpDir, "rev-parse HEAD", ct)).Trim();
        if (!string.Equals(head, SdlUwpRevision, StringComparison.OrdinalIgnoreCase))
        {
            progress.Report($"Checking out SDL-uwp @ {SdlUwpRevision[..9]}");
            if (!await Git(progress, $"-C \"{SdlUwpDir}\" checkout --quiet {SdlUwpRevision}", ct)) return false;
        }
        // A fresh checkout at the pinned rev is clean; dirty tracked files here
        // can only be the port patch (the /ZW configure-time patches touch other
        // files and run later).
        var dirty = await GitCapture(SdlUwpDir,
            "status --porcelain --untracked-files=no -- src/audio/wasapi/SDL_wasapi.c src/audio/wasapi/SDL_wasapi_winrt.cpp src/joystick/SDL_gamepad.c", ct);
        return await ApplyPortPatchAsync(SdlUwpDir, SdlUwpPatch, dirty.Trim().Length > 0, progress, ct);
    }

    private async Task<bool> EnsureDawnAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (!HasGitHead(DawnDir))
        {
            progress.Report($"== Fetching google/dawn @ {DawnRevision[..9]} (single commit) ==");
            Directory.CreateDirectory(DawnDir);
            if (!await Git(progress, $"-C \"{DawnDir}\" init --quiet", ct)) return false;
            if (!await Git(progress, $"-C \"{DawnDir}\" remote add origin {DawnRepository}", ct)) return false;
        }
        // Must precede checkout: dawn's tint test data exceeds MAX_PATH.
        if (!await Git(progress, $"-C \"{DawnDir}\" config core.longpaths true", ct)) return false;
        var head = (await GitCapture(DawnDir, "rev-parse HEAD", ct)).Trim();
        if (!string.Equals(head, DawnRevision, StringComparison.OrdinalIgnoreCase))
        {
            if (!await Git(progress, $"-C \"{DawnDir}\" fetch --quiet --depth 1 origin {DawnRevision}", ct)) return false;
            if (!await Git(progress, $"-C \"{DawnDir}\" checkout -qf FETCH_HEAD", ct)) return false;
        }
        var spirvSrc = Path.Combine(DawnDir, "third_party", "spirv-headers", "src");
        if (!Directory.Exists(spirvSrc) || !Directory.EnumerateFileSystemEntries(spirvSrc).Any())
        {
            progress.Report("Initializing third_party/spirv-headers (tint needs it; DAWN_FETCH_DEPENDENCIES is OFF)");
            if (!await Git(progress, $"-C \"{DawnDir}\" submodule update --init --depth 1 third_party/spirv-headers", ct)) return false;
        }
        var dirty = await GitCapture(DawnDir, "status --porcelain --untracked-files=no", ct);
        return await ApplyPortPatchAsync(DawnDir, DawnPatch, dirty.Trim().Length > 0, progress, ct);
    }

    private async Task<bool> ApplyPortPatchAsync(string repoDir, string patchName, bool alreadyApplied,
        IProgress<string> progress, CancellationToken ct)
    {
        var patch = Path.Combine(_patchesDir, patchName);
        if (!File.Exists(patch))
        {
            progress.Report($"Patch not found: {patch}");
            return false;
        }
        if (alreadyApplied)
        {
            progress.Report($"{patchName}: already applied.");
            return true;
        }
        progress.Report($"Applying {patchName} ...");
        var apply = await _runner.RunAsync("git", $"-C \"{repoDir}\" apply \"{patch}\"",
            repoDir, null, progress, TimeSpan.FromMinutes(2), ct);
        if (!apply.Success) progress.Report($"{patchName} failed to apply (exit {apply.ExitCode}).");
        return apply.Success;
    }

    /// <summary>
    /// Copies the bundled verbatim replacements for hand-edited, non-git
    /// dependency trees (tracy's UWP guard, abseil's tz UWP fix) when they
    /// differ. patches\files\<dep>\<relative path> == dependencies layout.
    /// </summary>
    private bool ApplyOverlays(IProgress<string> progress)
    {
        var overlayRoot = Path.Combine(_patchesDir, "files");
        if (!Directory.Exists(overlayRoot)) return true;
        var applied = 0;
        foreach (var file in Directory.EnumerateFiles(overlayRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(overlayRoot, file);
            var target = Path.Combine(DependenciesDir, rel);
            if (File.Exists(target) && Md5(target) == Md5(file)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
                applied++;
                progress.Report($"Overlay applied: {rel}");
            }
            catch (Exception e)
            {
                progress.Report($"Overlay failed for {rel}: {e.Message}");
                return false;
            }
        }
        if (applied == 0) progress.Report("Dependency overlays up to date (tracy, abseil).");
        return true;
    }

    private static string Md5(string path)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(md5.ComputeHash(stream));
    }

    private static bool HasGitHead(string dir) => File.Exists(Path.Combine(dir, ".git", "HEAD"));

    private async Task<bool> Git(IProgress<string> progress, string args, CancellationToken ct)
    {
        var r = await _runner.RunAsync("git", args, null, null, progress, TimeSpan.FromMinutes(30), ct);
        if (!r.Success) progress.Report($"git {args.Split(' ')[0]} failed (exit {r.ExitCode})");
        return r.Success;
    }

    private async Task<string> GitCapture(string repo, string args, CancellationToken ct)
    {
        var r = await _runner.RunAsync("git", $"-C \"{repo}\" {args}", repo, null, null,
            TimeSpan.FromMinutes(1), ct);
        return r.StandardOutput;
    }
}
