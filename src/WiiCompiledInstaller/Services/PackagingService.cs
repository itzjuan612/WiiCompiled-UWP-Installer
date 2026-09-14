using System.Text;
using System.Text.RegularExpressions;
using WiiCompiledInstaller.Models;

namespace WiiCompiledInstaller.Services;

/// <summary>
/// Stages the freshly built executables into the appx payload, applies the
/// configured version + [video] defaults to the packaged manifest and
/// Config.default.toml, then packs and signs with the bundled dev certificate.
/// Mirrors the manual B8 loop from the port handoff.
/// </summary>
public sealed class PackagingService
{
    private readonly ProcessRunner _runner = new();
    private readonly WorkspaceLayout _layout;

    public PackagingService(WorkspaceLayout layout) => _layout = layout;

    public string AppxPath => Path.Combine(_layout.UwpAppx,
        $"WiiCompiled_{NormalizeVersion(Version())}_x64.appx");

    public string Version()
    {
        var match = Regex.Match(File.ReadAllText(_layout.Manifest),
            "<Identity[^>]*Version=\"([0-9.]+)\"", RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : "0.0.0.0";
    }

    private static string NormalizeVersion(string v) => v.TrimEnd('.');

    public async Task<bool> PackageAsync(AppSettings settings, UwpToolchain toolchain,
        IProgress<string> progress, CancellationToken ct)
    {
        // 1. stage build outputs (Dawn links shared, so its DLL ships too)
        foreach (var name in new[] { "WiiCompiled.exe", "RetroRewind.exe", "webgpu_dawn.dll", "libpng16.dll", "z.dll" })
        {
            var built = Path.Combine(_layout.NativeBuildUwp, name);
            if (!File.Exists(built))
            {
                progress.Report($"Missing build output: {built} (compile first)");
                return false;
            }
            File.Copy(built, Path.Combine(_layout.PackageDir, name), true);
        }
        progress.Report($"Staged build outputs -> {_layout.PackageDir}");

        // 2. DXC compiler pair from the Windows SDK (Dawn's use_dxc toggle picks
        // it up from the app directory; without it Dawn falls back to legacy FXC).
        var sdkBin = Path.Combine(toolchain.SdkRoot, "bin", toolchain.SdkVersion, "x64");
        foreach (var name in new[] { "dxcompiler.dll", "dxil.dll" })
        {
            var src = Path.Combine(sdkBin, name);
            if (File.Exists(src)) File.Copy(src, Path.Combine(_layout.PackageDir, name), true);
            else progress.Report($"warning: {name} not found under {sdkBin} - the app will use FXC.");
        }

        // 3. VC++ runtime (the dynamic-CRT link the Xbox package relies on).
        var crtDir = FindVcRedistDir(toolchain.VsInstallPath);
        if (crtDir != null)
        {
            foreach (var name in new[]
                     {
                         "msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll", "msvcp140_atomic_wait.dll",
                         "vccorlib140.dll", "vcruntime140.dll", "vcruntime140_1.dll",
                     })
            {
                var src = Path.Combine(crtDir, name);
                if (File.Exists(src)) File.Copy(src, Path.Combine(_layout.PackageDir, name), true);
                else progress.Report($"warning: CRT {name} missing from {crtDir}");
            }
        }
        else
        {
            progress.Report("warning: no VC\\Redist\\MSVC\\*\\x64\\Microsoft.VC143.CRT found - the appx may not launch.");
        }

        // 2. manifest version
        if (!string.IsNullOrWhiteSpace(settings.PackageVersion))
            SetManifestVersion(settings.PackageVersion, progress);

        // 3. pack
        var appx = AppxPath;
        progress.Report($"== makeappx pack ({Path.GetFileName(appx)}) ==");
        var pack = await _runner.RunAsync(toolchain.MakeappxPath,
            $"pack /d \"{_layout.PackageDir}\" /p \"{appx}\" /o",
            _layout.UwpAppx, null, progress, TimeSpan.FromMinutes(10), ct);
        if (!pack.Success) return false;

        // 4. sign
        if (string.IsNullOrWhiteSpace(settings.PfxPath) || !File.Exists(settings.PfxPath))
        {
            progress.Report("No signing certificate configured (Settings > .pfx) — appx left unsigned.");
            return false;
        }
        progress.Report("== signtool sign ==");
        var sign = await _runner.RunAsync(toolchain.SigntoolPath,
            $"sign /fd SHA256 /f \"{settings.PfxPath}\" /p \"{settings.PfxPassword}\" " +
            $"/t http://timestamp.digicert.com \"{appx}\"",
            _layout.UwpAppx, null, progress, TimeSpan.FromMinutes(5), ct);
        if (sign.Success)
        {
            var size = new FileInfo(appx).Length;
            progress.Report($"Signed {Path.GetFileName(appx)} ({size / 1024 / 1024} MB).");
        }
        return sign.Success;
    }

    private static string? FindVcRedistDir(string vsInstallPath)
    {
        var root = Path.Combine(vsInstallPath, "VC", "Redist", "MSVC");
        if (!Directory.Exists(root)) return null;
        foreach (var dir in Directory.EnumerateDirectories(root).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var crt = Path.Combine(dir, "x64", "Microsoft.VC143.CRT");
            if (File.Exists(Path.Combine(crt, "vcruntime140.dll"))) return crt;
        }
        return null;
    }

    /// <summary>Bump the packaged AppxManifest version (3-part versions get a .0 build).</summary>
    public void SetVersion(string version) => SetManifestVersion(version, new Progress<string>(_ => { }));

    private void SetManifestVersion(string version, IProgress<string> progress)
    {
        version = NormalizeVersion(version);
        var text = File.ReadAllText(_layout.Manifest);
        var next = Regex.Replace(text, "(<Identity[^>]*Version=\")[0-9.]+(\")",
            m => m.Groups[1].Value + version + m.Groups[2].Value);
        if (next != text)
        {
            File.WriteAllText(_layout.Manifest, next, new UTF8Encoding(false));
            progress.Report($"AppxManifest Version -> {version}");
        }
    }
}
