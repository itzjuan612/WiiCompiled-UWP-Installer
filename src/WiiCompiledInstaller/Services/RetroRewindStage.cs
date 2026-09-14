using System.IO.Compression;
using WiiCompiledInstaller.Models;

namespace WiiCompiledInstaller.Services;

/// <summary>
/// Places a Retro Rewind pack where the translator's retro-rewind profile
/// expects it (workspace\PulsarPacks\completed\RetroRewind\RetroRewind6, which
/// recomp.yml pins as mod_root/code_pul). Accepts a folder, a .zip, or a bare
/// Code.pul — the pack tree itself never ships inside the appx; only Code.pul
/// is consumed at build time (statically translated into RetroRewind.exe).
/// </summary>
public sealed class RetroRewindStage
{
    private readonly AppSettings _settings;
    private readonly Action<string>? _log;

    public RetroRewindStage(AppSettings settings, Action<string>? log = null)
    {
        _settings = settings;
        _log = log;
    }

    public string StagedRoot => new WorkspaceLayout(_settings.WorkspaceDir).PulsarPacks;

    /// <summary>Reads RetroRewind6\version.txt if present (e.g. "6.12.8").</summary>
    public string? DetectedVersion()
    {
        var vf = Path.Combine(StagedRoot, "version.txt");
        return File.Exists(vf) ? File.ReadAllText(vf).Trim() : null;
    }

    public bool CodePulPresent() =>
        File.Exists(Path.Combine(StagedRoot, "Binaries", "Code.pul"));

    /// <summary>
    /// Ensures the pack is staged from settings.RetroRewindDirectory (a
    /// RetroRewind6 folder, a zip containing one, or a path to Code.pul).
    /// Returns true with a status token in <paramref name="message"/>.
    /// </summary>
    public bool Prepare(out string message)
    {
        if (CodePulPresent())
        {
            message = "staged";
            return true;
        }
        var source = _settings.RetroRewindDirectory?.Trim().Trim('"') ?? "";
        if (string.IsNullOrEmpty(source) || !File.Exists(source) && !Directory.Exists(source))
        {
            message = "missing";
            return false;
        }
        try
        {
            if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ExtractFromZip(source);
            else if (File.Exists(source))
                StageCodePulOnly(source);
            else
                StageDirectory(source);
            message = CodePulPresent() ? "staged" : "no Code.pul in source";
            return CodePulPresent();
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private void StageDirectory(string retroRewind6)
    {
        var dir = new DirectoryInfo(retroRewind6);
        var root = dir.Name.Equals("RetroRewind6", StringComparison.OrdinalIgnoreCase) ? dir : FindPackRoot(dir);
        var pul = Path.Combine(root.FullName, "Binaries", "Code.pul");
        if (!File.Exists(pul)) throw new FileNotFoundException("Binaries\\Code.pul not found in the pack", pul);
        if (!PathsEqual(root.FullName, StagedRoot))
        {
            // Junction first (cheap, no copy); fall back to a real copy.
            TryCreateJunction(StagedRoot, root.FullName, out bool linked);
            if (!linked) CopyDirectory(root.FullName, StagedRoot);
        }
        _log?.Invoke($"Retro Rewind staged from folder: {root.FullName}");
    }

    private void StageCodePulOnly(string codePulPath)
    {
        var binaries = Path.Combine(StagedRoot, "Binaries");
        Directory.CreateDirectory(binaries);
        File.Copy(codePulPath, Path.Combine(binaries, "Code.pul"), overwrite: true);
        _log?.Invoke($"Code.pul staged from: {codePulPath}");
    }

    private void ExtractFromZip(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        var entry = archive.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith("Binaries/Code.pul", StringComparison.OrdinalIgnoreCase) ||
            e.FullName.EndsWith("Code.pul", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("No Code.pul inside the zip");
        var marker = "Binaries/Code.pul";
        var packRootInZip = entry.FullName.EndsWith(marker, StringComparison.OrdinalIgnoreCase)
            ? entry.FullName[..^marker.Length]
            : entry.FullName[..^"Code.pul".Length];
        // Extract the whole pack subtree if the zip carries it; Code.pul otherwise.
        Directory.CreateDirectory(StagedRoot);
        foreach (var e in archive.Entries.Where(e =>
                     e.FullName.StartsWith(packRootInZip, StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrEmpty(e.Name)))
        {
            var target = Path.Combine(StagedRoot, e.FullName[packRootInZip.Length..].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
        }
        _log?.Invoke($"Retro Rewind extracted from {Path.GetFileName(zip)} ({packRootInZip}).");
    }

    private static DirectoryInfo FindPackRoot(DirectoryInfo start)
    {
        if (File.Exists(Path.Combine(start.FullName, "Binaries", "Code.pul"))) return start;
        foreach (var child in start.EnumerateDirectories())
        {
            if (child.Name.Equals("RetroRewind6", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(Path.Combine(child.FullName, "Binaries", "Code.pul")))
                return child;
            var deeper = FindPackRoot(child);
            if (File.Exists(Path.Combine(deeper.FullName, "Binaries", "Code.pul"))) return deeper;
        }
        return start;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    private static void TryCreateJunction(string link, string target, out bool created)
    {
        created = false;
        try
        {
            if (Directory.Exists(link)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
            created = p.ExitCode == 0;
        }
        catch { created = false; }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, target));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, target), overwrite: true);
    }
}
