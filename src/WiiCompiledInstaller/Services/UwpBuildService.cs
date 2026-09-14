using WiiCompiledInstaller.Models;

namespace WiiCompiledInstaller.Services;

/// <summary>
/// The C# counterpart of Launcher/seed-cache-uwp-msvc.ps1 + configure-uwp-msvc.ps1
/// + build-uwp-msvc.ps1: seeds the UWP CMake cache with the prepared dependency
/// trees, configures with the MSVC toolchain file, and compiles the products.
/// Portable by construction — every path comes from the settings or the
/// ToolchainLocator, and the repo's UWPToolchainMSVC.cmake honours the MKW_*
/// overrides this service exports.
/// </summary>
public sealed class UwpBuildService
{
    private readonly ProcessRunner _runner = new();
    private readonly WorkspaceLayout _layout;
    private readonly BootstrapService _bootstrap;

    public UwpBuildService(WorkspaceLayout layout, BootstrapService bootstrap)
    {
        _layout = layout;
        _bootstrap = bootstrap;
    }

    public string CMakeCache => Path.Combine(_layout.NativeBuildUwp, "CMakeCache.txt");

    /// <summary>Seed the fresh UWP/MSVC cache (mirrors seed-cache-uwp-msvc.ps1).</summary>
    public void SeedCache(AppSettings settings, UwpToolchain toolchain)
    {
        var depsRoot = Path.Combine(_layout.Launcher, "artifacts", "dependencies").Replace('\\', '/');
        Directory.CreateDirectory(_layout.NativeBuildUwp);
        var lines = new List<string>
        {
            "# CMake cache file (seeded by WiiCompiled-UWP-Installer for UWP/MSVC: SDL-uwp fork + local Dawn, offline deps)",
            $"CMAKE_HOME_DIRECTORY:INTERNAL={Path.Combine(_layout.Root, "runtime").Replace('\\', '/')}",
            $"FETCHCONTENT_SOURCE_DIR_SDL:PATH={depsRoot}/SDL-uwp",
            $"FETCHCONTENT_SOURCE_DIR_ABSEIL-CPP:PATH={depsRoot}/abseil-cpp",
            $"FETCHCONTENT_SOURCE_DIR_FMT:PATH={depsRoot}/fmt",
            $"FETCHCONTENT_SOURCE_DIR_FREETYPE:PATH={depsRoot}/freetype",
            $"FETCHCONTENT_SOURCE_DIR_IMGUI:PATH={depsRoot}/imgui",
            $"FETCHCONTENT_SOURCE_DIR_LIBUSB:PATH={depsRoot}/libusb",
            $"FETCHCONTENT_SOURCE_DIR_PNG:PATH={depsRoot}/png",
            $"FETCHCONTENT_SOURCE_DIR_SQLITE3:PATH={depsRoot}/sqlite3",
            $"FETCHCONTENT_SOURCE_DIR_TRACY:PATH={depsRoot}/tracy",
            $"FETCHCONTENT_SOURCE_DIR_XXHASH:PATH={depsRoot}/xxhash",
            $"FETCHCONTENT_SOURCE_DIR_ZLIB:PATH={depsRoot}/zlib",
            $"FETCHCONTENT_SOURCE_DIR_ZSTD:PATH={depsRoot}/zstd",
            $"MKW_CPPWINRT_INCLUDE_DIR:PATH={depsRoot}/cppwinrt",
            "FETCHCONTENT_FULLY_DISCONNECTED:BOOL=OFF",
        };
        File.WriteAllLines(CMakeCache, lines);
    }

    private Dictionary<string, string?> BuildEnv(AppSettings settings, UwpToolchain toolchain, string msvcIncJunction, string msvcLibJunction)
    {
        var env = toolchain.BuildEnvironment();
        env["MKW_MSVC_ROOT"] = toolchain.MsvcRoot!.Replace('\\', '/');
        env["MKW_SDK_ROOT"] = toolchain.SdkRoot!.Replace('\\', '/');
        env["MKW_SDK_VER"] = toolchain.SdkVersion;
        env["MKW_MSCVINC"] = msvcIncJunction.Replace('\\', '/');
        env["MKW_MSCVLIB"] = msvcLibJunction.Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(settings.PythonPath))
            env["MKW_PYTHON"] = settings.PythonPath.Replace('\\', '/');
        return env;
    }

    public async Task<bool> ConfigureAsync(AppSettings settings, UwpToolchain toolchain,
        string msvcIncJunction, string msvcLibJunction, bool force, IProgress<string> progress, CancellationToken ct)
    {
        if (File.Exists(CMakeCache) && !force &&
            File.Exists(Path.Combine(_layout.NativeBuildUwp, "build.ninja")))
        {
            progress.Report("Native build directory already configured — keeping it (incremental).");
            return true;
        }
        if (force && Directory.Exists(_layout.NativeBuildUwp))
        {
            progress.Report("Force reconfigure: clearing native-build-uwp");
            Directory.Delete(_layout.NativeBuildUwp, true);
        }
        SeedCache(settings, toolchain);

        var translatedJobs = TranslatedJobs(settings);
        var args = string.Join(' ',
            "-S", $"\"{Path.Combine(_layout.Root, "runtime")}\"",
            "-B", $"\"{_layout.NativeBuildUwp}\"",
            "-G", "Ninja",
            $"-DCMAKE_MAKE_PROGRAM=\"{Path.Combine(_bootstrap.PortableTools, "ninja", "ninja.exe").Replace('\\', '/')}\"",
            $"-DCMAKE_TOOLCHAIN_FILE=\"{Path.Combine(_layout.Launcher, "cmake", "UWPToolchainMSVC.cmake").Replace('\\', '/')}\"",
            "-DCMAKE_BUILD_TYPE=Release",
            "-DCMAKE_SYSTEM_PROCESSOR=x86_64",
            "-DAURORA_DAWN_PROVIDER=vendor",
            "-DAURORA_DAWN_LINKAGE=shared",
            "-DAURORA_SDL3_PROVIDER=vendor",
            "-DCMAKE_POLICY_DEFAULT_CMP0168=NEW",
            $"-DMKW_TRANSLATED_COMPILE_JOBS={translatedJobs}",
            "-DFETCHCONTENT_FULLY_DISCONNECTED=OFF");
        progress.Report("== Configuring the UWP build (MSVC) ==");
        var result = await _runner.RunAsync(_bootstrap.CMakeExe, args, _layout.Root,
            BuildEnv(settings, toolchain, msvcIncJunction, msvcLibJunction), progress, TimeSpan.FromMinutes(30), ct);
        if (!result.Success) progress.Report($"configure failed (exit {result.ExitCode})");
        return result.Success;
    }

    /// <summary>
    /// Parallelism for the generated-shard compile, mirroring LocalBuild.ps1:
    /// min(cores, RAM-GiB/2) — the shard objects are huge and link-time memory,
    /// not core count, is the real ceiling. settings.ParallelJobs (0 = auto) pins it.
    /// </summary>
    private static int TranslatedJobs(AppSettings settings)
    {
        if (settings.ParallelJobs > 0) return settings.ParallelJobs;
        var cores = Math.Max(1, Environment.ProcessorCount);
        long ramBytes = 0;
        try { ramBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; } catch { }
        var ramGiB = ramBytes > 0 ? (int)(ramBytes / (1024L * 1024 * 1024)) : 8;
        return Math.Max(1, Math.Min(cores, ramGiB / 2));
    }

    public async Task<bool> BuildTargetAsync(string target, AppSettings settings, UwpToolchain toolchain,
        string msvcIncJunction, string msvcLibJunction, IProgress<string> progress, CancellationToken ct)
    {
        progress.Report($"== Compiling {target} (this takes a while on first run) ==");
        var args = $"--build \"{_layout.NativeBuildUwp}\" --config Release --target {target}";
        var result = await _runner.RunAsync(_bootstrap.CMakeExe, args, _layout.Root,
            BuildEnv(settings, toolchain, msvcIncJunction, msvcLibJunction), progress, null, ct);
        if (!result.Success) progress.Report($"{target} failed (exit {result.ExitCode})");
        return result.Success;
    }
}
