using System.Text.RegularExpressions;
using WiiCompiledInstaller.Models;

namespace WiiCompiledInstaller.Services;

/// <summary>
/// Drives the translator CLI the way Launcher/LocalBuild.ps1 does, with the
/// same cache discipline: the base game translation is reused when the
/// provenance fingerprint matches and check-base-mod-awareness still vouches
/// for it against the currently staged Code.pul; otherwise it retranslates.
/// </summary>
public sealed class TranslationService
{
    private readonly ProcessRunner _runner = new();
    private readonly WorkspaceLayout _layout;
    private readonly string _translator;

    public TranslationService(ProcessRunner runner, WorkspaceLayout layout, string translatorExe)
    {
        _runner = runner;
        _layout = layout;
        _translator = translatorExe;
    }

    public static string? ReadEntryPoint(string projectYaml)
    {
        var text = File.ReadAllText(projectYaml);
        var m = Regex.Match(text, @"entry_points:\s*\r?\n\s*-\s*(0x[0-9A-Fa-f]+|\d+)", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    private string Project => Path.Combine(_layout.Root, "projects", "mkwii", "recomp.yml");
    private string BaseMetadata => Path.Combine(_layout.Generated, "base_translation_output.json");
    private string BaseManifest => Path.Combine(_layout.Root, "build", "base", "mkwii_base_manifest.json");
    private string CodePul => Path.Combine(_layout.PulsarPacks, "Binaries", "Code.pul");

    /// <summary>The base translation may be reused only if everything it produced is present.</summary>
    public bool BaseTranslationPresent() =>
        File.Exists(BaseMetadata) && File.Exists(BaseManifest) &&
        File.Exists(Path.Combine(_layout.Generated, "base_translation_sources.bin")) &&
        Directory.Exists(Path.Combine(_layout.Generated, "functions"));

    /// <summary>True when the recorded base translation already covers this Code.pul.</summary>
    public async Task<bool> BaseCoversCodePulAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (!File.Exists(BaseMetadata))
        {
            progress.Report("No base translation metadata yet; a full translation is required.");
            return false;
        }
        if (!File.Exists(CodePul)) return false;
        if (File.Exists(BaseMetadata))
        {
            var sha = Sha256(CodePul);
            try
            {
                using var sr = new StreamReader(BaseMetadata);
                var buffer = new char[1 << 16];
                var tail = string.Empty;
                int read;
                while ((read = await sr.ReadBlockAsync(buffer, 0, buffer.Length)) > 0)
                {
                    var window = tail + new string(buffer, 0, read);
                    if (window.Contains(sha, StringComparison.Ordinal)) return true;
                    tail = window.Length >= sha.Length
                        ? window[(window.Length - sha.Length + 1)..]
                        : window;
                }
            }
            catch (IOException) { /* fall through to the translator question */ }
        }
        progress.Report("Asking the translator whether the existing base translation covers this Code.pul...");
        var result = await _runner.RunAsync(_translator,
            $"check-base-mod-awareness --project \"{Project}\" --profile retro-rewind " +
            $"--translation-output-metadata \"{BaseMetadata}\" --code-pul \"{CodePul}\"",
            _layout.Root, null, progress, null, ct);
        return result.Success;
    }

    public async Task<bool> TranslateAsync(AppSettings settings, bool includeRetro, bool reuseBase,
        IProgress<string> progress, CancellationToken ct)
    {
        var entry = ReadEntryPoint(Project);
        if (entry is null) { progress.Report("Could not read entry_point from recomp.yml"); return false; }
        var threads = Math.Max(1, Math.Min(Environment.ProcessorCount, 16));

        if (!reuseBase || !BaseTranslationPresent())
        {
            progress.Report("== Translating the base game (this is the long step on first run) ==");
            if (!await Run($"translate-recursive {entry} --project \"{Project}\" --outdir \"{Path.Combine(_layout.Generated, "functions")}\" " +
                $"--output-metadata \"{BaseMetadata}\" --production-source-bundle \"{Path.Combine(_layout.Generated, "base_translation_sources.bin")}\" " +
                $"--no-function-files --prune-stale --threads {threads}", "translate-recursive", progress, ct)) return false;
            if (!await Run($"emit-base-manifest --project \"{Project}\" --out \"{Path.Combine(_layout.Root, "build", "base")}\" " +
                $"--functions-dir \"{Path.Combine(_layout.Generated, "functions")}\" --translation-output-metadata \"{BaseMetadata}\" --region P",
                "emit-base-manifest", progress, ct)) return false;
        }
        else progress.Report("Reusing the recorded base translation (mod-awareness check passed).");

        if (includeRetro)
        {
            var modOut = Path.Combine(_layout.Root, "build", "mods", "retro_rewind_full_cpp");
            if (!await Run($"translate-mod --project \"{Project}\" --profile retro-rewind --base-manifest \"{BaseManifest}\" " +
                $"--base-translation-output-metadata \"{BaseMetadata}\" --code-pul \"{CodePul}\" --mod-root \"{_layout.PulsarPacks}\" " +
                $"--mod-name 'Retro Rewind' --region P --out \"{modOut}\" --prefer-cached-inputs --emit-cpp --threads {threads}",
                "translate-mod", progress, ct)) return false;
        }

        if (!await Run($"generate-data-init --project \"{Project}\"", "generate-data-init", progress, ct)) return false;
        var shards = new List<string>
        {
            "emit-build-shards", "--project", $"\"{Project}\"", "--base-metadata", $"\"{BaseMetadata}\"",
            "--base-functions-dir", $"\"{Path.Combine(_layout.Generated, "functions")}\"",
            "--native-source-dir", $"\"{Path.Combine(_layout.Root, "runtime", "src")}\"",
            "--out", $"\"{Path.Combine(_layout.Generated, "build_shards")}\"",
        };
        if (includeRetro)
        {
            var modOut = Path.Combine(_layout.Root, "build", "mods", "retro_rewind_full_cpp");
            shards.Add("--resolved-profile"); shards.Add($"\"{Path.Combine(modOut, "resolved_dispatch_profile.json")}\"");
            shards.Add("--retro-cpp-dir"); shards.Add($"\"{Path.Combine(modOut, "cpp")}\"");
        }
        return await Run(string.Join(' ', shards), "emit-build-shards", progress, ct);
    }

    private async Task<bool> Run(string args, string step, IProgress<string> progress, CancellationToken ct)
    {
        progress.Report($"== translator: {step} ==");
        var result = await _runner.RunAsync(_translator, args, _layout.Root, null, progress, null, ct);
        if (!result.Success) progress.Report($"{step} failed (exit {result.ExitCode})");
        return result.Success;
    }

    private static string Sha256(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}
