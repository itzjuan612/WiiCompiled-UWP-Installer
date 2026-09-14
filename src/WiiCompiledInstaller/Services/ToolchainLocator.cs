using System.Xml;
using Microsoft.Win32;

namespace WiiCompiledInstaller.Services;

/// <summary>Discovered MSVC + Windows SDK locations needed to compile the UWP targets.</summary>
public sealed record UwpToolchain(
    string? VsInstallPath,
    string? MsvcRoot,      // ...\VC\Tools\MSVC\<version>
    string? MsvcVersion,
    string? SdkRoot,       // ...\Windows Kits\10 (or a custom partial SDK dir)
    string? SdkVersion,    // 10.0.xxxxx.0
    string? MakeappxPath,
    string? SigntoolPath)
{
    public bool IsComplete =>
        !string.IsNullOrEmpty(MsvcRoot) &&
        !string.IsNullOrEmpty(SdkRoot) &&
        !string.IsNullOrEmpty(SdkVersion) &&
        !string.IsNullOrEmpty(MakeappxPath);

    /// <summary>Builds the INCLUDE/LIB/LIBPATH/PATH environment the UWP/MSVC scripts rely on.</summary>
    public Dictionary<string, string?> BuildEnvironment()
    {
        var env = new Dictionary<string, string?>();
        if (!IsComplete) return env;

        var msvcBin = Path.Combine(MsvcRoot!, "bin", "Hostx64", "x64");
        var msvcInc = Path.Combine(MsvcRoot!, "include");
        var msvcLib = Path.Combine(MsvcRoot!, "lib", "x64");
        var sdkInc  = Path.Combine(SdkRoot!, "Include", SdkVersion!);
        var sdkLib  = Path.Combine(SdkRoot!, "Lib", SdkVersion!);
        var sdkBin  = Path.Combine(SdkRoot!, "bin", SdkVersion!, "x64");
        var unimeta = Path.Combine(SdkRoot!, "UnionMetadata", SdkVersion!);

        env["INCLUDE"] = string.Join(';',
            msvcInc,
            Path.Combine(sdkInc, "ucrt"),
            Path.Combine(sdkInc, "um"),
            Path.Combine(sdkInc, "winrt"),
            Path.Combine(sdkInc, "shared"));
        env["LIB"] = string.Join(';',
            msvcLib,
            Path.Combine(sdkLib, "ucrt", "x64"),
            Path.Combine(sdkLib, "um", "x64"));
        env["LIBPATH"] = string.Join(';', unimeta, msvcLib,
            Path.Combine(sdkLib, "ucrt", "x64"), Path.Combine(sdkLib, "um", "x64"));
        env["WindowsSDKDir"] = SdkRoot + "\\";
        env["WindowsSdkVersion"] = SdkVersion;
        // Prepend rather than replace so the CRT/system toolchain stays reachable.
        env["PATH_PREPEND"] = msvcBin + ";" + sdkBin;
        return env;
    }
}

/// <summary>
/// Finds the Visual Studio MSVC toolchain and a UWP-capable Windows SDK on the machine,
/// replacing the hardcoded "E:\Program Files\...\14.44.35207" and "C:\WindowsSDK\10.0.28000.0"
/// paths that the repo's UWP scripts bake in.
/// </summary>
public static class ToolchainLocator
{
    public static UwpToolchain Locate(IProgress<string>? log = null)
    {
        var vsPath = FindVsInstallPath();
        var (msvcRoot, msvcVersion) = FindMsvc(vsPath);
        // MKW_MSVC_ROOT points straight at a ...\VC\Tools\MSVC\<ver> toolset
        // (same knob the UWP toolchain file honours); wins over the scan.
        var forced = Environment.GetEnvironmentVariable("MKW_MSVC_ROOT");
        if (!string.IsNullOrWhiteSpace(forced) &&
            File.Exists(Path.Combine(forced, "include", "vcruntime.h")))
        {
            msvcRoot = Path.GetFullPath(forced);
            msvcVersion = Path.GetFileName(msvcRoot.TrimEnd('\\', '/'));
        }
        var (sdkRoot, sdkVersion) = FindWindowsSdk();
        var makeappx = FindSdkTool(sdkRoot, sdkVersion, "makeappx.exe");
        var signtool = FindSdkTool(sdkRoot, sdkVersion, "signtool.exe");

        log?.Report($"VS:      {vsPath ?? "(not found)"}");
        log?.Report($"MSVC:    {msvcVersion ?? "(not found)"}");
        log?.Report($"SDK:     {(sdkRoot is null ? "(not found)" : $"{sdkRoot} @ {sdkVersion}")}");
        log?.Report($"makeappx:{makeappx ?? "(not found)"}");
        log?.Report($"signtool:{signtool ?? "(not found)"}");

        return new UwpToolchain(vsPath, msvcRoot, msvcVersion, sdkRoot, sdkVersion, makeappx, signtool);
    }

    private static string? FindVsInstallPath()
    {
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vswhere))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = vswhere,
                    Arguments = "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                var output = p?.StandardOutput.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(output) && Directory.Exists(output)) return output;
            }
            catch { /* fall through to manual scan */ }
        }

        // Fallback scan of the common install roots, across every fixed drive
        // (VS is happily installed on non-system drives).
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives().Where(d =>
                     d.DriveType == DriveType.Fixed && d.IsReady))
        {
            foreach (var pf in new[] { "Program Files", "Program Files (x86)" })
                roots.Add(Path.Combine(drive.RootDirectory.FullName, pf,
                    "Microsoft Visual Studio", "2022"));
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio", "2022"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "2022"));
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var candidate in Directory.GetDirectories(root))
            {
                if (Directory.Exists(Path.Combine(candidate, "VC", "Tools", "MSVC"))) return candidate;
            }
        }
        return null;
    }

    private static (string? root, string? version) FindMsvc(string? vsPath)
    {
        if (vsPath is null) return (null, null);
        var msvcBase = Path.Combine(vsPath, "VC", "Tools", "MSVC");
        if (!Directory.Exists(msvcBase)) return (null, null);
        // Highest version wins; each dir is like 14.44.35207.
        var versions = Directory.GetDirectories(msvcBase)
            .Select(Path.GetFileName)
            .Where(v => v is not null && Version.TryParse(Normalize(v), out _))
            .OrderByDescending(v => new Version(Normalize(v!)))
            .ToList();
        if (versions.Count == 0) return (null, null);
        return (Path.Combine(msvcBase, versions[0]!), versions[0]);
    }

    // "14.44.35207" is already a valid Version; pad anything with fewer dots.
    private static string Normalize(string v) => v.Split('.').Length switch
    {
        1 => v + ".0.0",
        2 => v + ".0",
        _ => v,
    };

    private static (string? root, string? version) FindWindowsSdk()
    {
        var candidates = new List<string>();

        // Registry-discovered SDKs.
        foreach (var hive in new[] { RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Wow6432Node\Microsoft\Microsoft SDKs\Windows\v10.0")
                             ?? baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SDKs\Windows\v10.0");
                if (key?.GetValue("InstallationFolder") is string folder && Directory.Exists(folder))
                    candidates.Add(folder.TrimEnd('\\'));
            }
            catch { }
        }

        // Standard install location + the repo's own partial-SDK convention.
        var extras = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10"),
            @"C:\WindowsSDK",
            Environment.GetEnvironmentVariable("WindowsSDKDir") ?? "",
        };
        // MKW_SDK_ROOT points straight at a Windows SDK root (...\Windows Kits\10 or a
        // partial-SDK dir); wins over the scan, same knob as MKW_MSVC_ROOT for the toolset.
        var forcedSdk = Environment.GetEnvironmentVariable("MKW_SDK_ROOT");
        if (!string.IsNullOrWhiteSpace(forcedSdk))
            extras.Insert(0, forcedSdk);
        // The SDK is happily relocated to non-system drives (the registry still points at
        // the original install path); probe every fixed drive root. The folder may carry any
        // name, so recognise it by content: SDKManifest.xml at its root (plus the standard
        // ...\Windows Kits\10 layout directly under the drive root).
        foreach (var drive in DriveInfo.GetDrives().Where(d =>
                     d.DriveType == DriveType.Fixed && d.IsReady))
        {
            extras.Add(Path.Combine(drive.RootDirectory.FullName, "Windows Kits", "10"));
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(drive.RootDirectory.FullName))
                {
                    if (File.Exists(Path.Combine(dir, "SDKManifest.xml")))
                        extras.Add(dir);
                }
            }
            catch { /* unreadable drive root: nothing to probe there */ }
        }
        foreach (var extra in extras)
        {
            if (!string.IsNullOrEmpty(extra) && Directory.Exists(extra))
                candidates.Add(extra.TrimEnd('\\'));
        }

        // Pick the highest SDK version that actually carries a UWP toolset (UnionMetadata + makeappx).
        string? bestRoot = null, bestVer = null;
        foreach (var root in candidates.Distinct())
        {
            var binRoot = Path.Combine(root, "bin");
            if (!Directory.Exists(binRoot)) continue;
            foreach (var ver in Directory.GetDirectories(binRoot).Select(Path.GetFileName).Where(SdkVersionOk).OrderByDescending(SdkSortKey).ToList())
            {
                if (File.Exists(Path.Combine(root, "UnionMetadata", ver!, "Windows.winmd")))
                {
                    bestRoot = root; bestVer = ver; break;
                }
            }
            if (bestRoot is not null) break;
        }
        return (bestRoot, bestVer);
    }

    private static bool SdkVersionOk(string? v) => v is not null && System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d+\.\d+\.\d+\.\d+$");
    private static Version SdkSortKey(string? v) => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0);

    private static string? FindSdkTool(string? sdkRoot, string? sdkVersion, string tool)
    {
        if (sdkRoot is null) return null;
        if (sdkVersion is not null)
        {
            var exact = Path.Combine(sdkRoot, "bin", sdkVersion, "x64", tool);
            if (File.Exists(exact)) return exact;
        }
        // Search every installed SDK version, newest first.
        var binRoot = Path.Combine(sdkRoot, "bin");
        if (!Directory.Exists(binRoot)) return null;
        foreach (var ver in Directory.GetDirectories(binRoot).Select(Path.GetFileName).Where(SdkVersionOk).OrderByDescending(SdkSortKey))
        {
            var p = Path.Combine(sdkRoot, "bin", ver!, "x64", tool);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
