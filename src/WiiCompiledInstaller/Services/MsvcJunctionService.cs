using System.Diagnostics;
using WiiCompiledInstaller.Models;

namespace WiiCompiledInstaller.Services;

/// <summary>
/// Provisions the space-free MSVC junctions the UWP toolchain needs (Ninja
/// word-splits flags, so paths with spaces are unusable in CMAKE_*_FLAGS).
/// Mirrors Launcher/prepare-msvc-uwp.ps1 but is idempotent and parameterised.
/// </summary>
public sealed class MsvcJunctionService
{
    public static (string Inc, string Lib) JunctionRoot(AppSettings settings)
    {
        var root = Path.Combine(Path.GetDirectoryName(settings.WorkspaceDir) ?? ".", "msvc-junctions");
        return (Path.Combine(root, "inc"), Path.Combine(root, "lib"));
    }

    public record struct Result(bool Ok, string Inc, string Lib, string Message);

    public Result Ensure(AppSettings settings, UwpToolchain toolchain, Action<string>? log = null)
    {
        var (inc, lib) = JunctionRoot(settings);
        try
        {
            var srcInc = toolchain.MsvcRoot.Replace('\\', '/') + "/include";
            var srcLib = toolchain.MsvcRoot.Replace('\\', '/') + "/lib/x64";
            if (!File.Exists(Path.Combine(toolchain.MsvcRoot, "include", "vcruntime.h")))
                return new(false, inc, lib, $"MSVC include dir missing under {toolchain.MsvcRoot}");

            Directory.CreateDirectory(Path.GetDirectoryName(inc)!);
            Link(inc, toolchain.MsvcRoot + "\\include", log);
            Link(lib, toolchain.MsvcRoot + "\\lib\\x64", log);
            return new(true, inc, lib, "Junctions ready");
        }
        catch (Exception ex)
        {
            return new(false, inc, lib, ex.Message);
        }
    }

    private static void Link(string link, string target, Action<string>? log)
    {
        if (Directory.Exists(link) || File.Exists(link))
        {
            if (IsReparsePoint(link))
            {
                log?.Invoke($"Junction exists: {link}");
                return;
            }
            throw new InvalidOperationException($"{link} exists but is not a junction; remove it first");
        }
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"mklink /J failed for {link}: {proc.StandardOutput.ReadToEnd().Trim()}");
        log?.Invoke($"Created junction: {link} -> {target}");
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
