using WiiCompiledInstaller.Models;
using WiiCompiledInstaller.Services;

namespace WiiCompiledInstaller;

/// <summary>
/// Console driver for the same pipeline the GUI exposes — used by CI and by
/// users who prefer scripting. Progress lines stream to stdout.
/// </summary>
public static class Headless
{
    private static readonly string[] Known =
    { "preflight", "bootstrap", "translate", "configure", "build", "package", "deploy", "all" };

    public static int Run(string[] steps)
    {
        if (steps.Length == 0)
        {
            Console.Error.WriteLine("usage: WiiCompiled-Installer --headless <preflight|bootstrap|translate|configure|build|all> [more steps...]");
            return 2;
        }
        foreach (var s in steps)
            if (!Known.Contains(s))
            {
                Console.Error.WriteLine($"unknown step '{s}' (known: {string.Join(", ", Known)})");
                return 2;
            }

        // "all" = the full local chain; deploy stays explicit (it touches a console).
        steps = steps.SelectMany(s => s == "all"
            ? new[] { "preflight", "bootstrap", "translate", "configure", "build", "package" }
            : new[] { s }).ToArray();

        var store = new SettingsStore();
        var settings = store.Current;
        var layout = new WorkspaceLayout(settings.WorkspaceDir);
        var bootstrap = new BootstrapService(layout.Root);
        var progress = new Progress<string>(Console.WriteLine);

        var toolchain = ToolchainLocator.Locate(progress);
        if (!toolchain.IsComplete)
        {
            Console.Error.WriteLine("Toolchain incomplete — run 'preflight' for details");
            return 1;
        }

        var ok = true;
        foreach (var step in steps)
        {
            if (step == "preflight") { ok = toolchain.IsComplete; continue; }
            if (step == "bootstrap") { ok &= bootstrap.RunAsync(progress, CancellationToken.None).Result; continue; }
            if (step == "translate") { ok &= TranslateAsync(settings, layout, bootstrap, progress).Result; continue; }
            if (step == "configure")
            {
                var junctions = MsvcJunctionService.JunctionRoot(settings);
                var j = new MsvcJunctionService().Ensure(settings, toolchain);
                if (!j.Ok) { Console.Error.WriteLine(j.Message); return 1; }
                ok &= new UwpBuildService(layout, bootstrap)
                    .ConfigureAsync(settings, toolchain, j.Inc, j.Lib, force: false, progress, CancellationToken.None).Result;
                continue;
            }
            if (step == "package")
            {
                ok &= new PackagingService(layout).PackageAsync(settings, toolchain, progress, CancellationToken.None).Result;
                continue;
            }
            if (step == "deploy")
            {
                ok &= DeployAsync(settings, layout, progress).Result;
                continue;
            }
            if (step == "build")
            {
                var junctions = MsvcJunctionService.JunctionRoot(settings);
                var j = new MsvcJunctionService().Ensure(settings, toolchain);
                if (!j.Ok) { Console.Error.WriteLine(j.Message); return 1; }
                var service = new UwpBuildService(layout, bootstrap);
                ok &= service.BuildTargetAsync("WiiCompiled", settings, toolchain, j.Inc, j.Lib, progress, CancellationToken.None).Result;
                ok &= service.BuildTargetAsync("RetroRewind", settings, toolchain, j.Inc, j.Lib, progress, CancellationToken.None).Result;
                continue;
            }
        }
        return ok ? 0 : 1;
    }

    private static async Task<bool> DeployAsync(AppSettings settings, WorkspaceLayout layout, IProgress<string> progress)
    {
        if (string.IsNullOrWhiteSpace(settings.XboxPortalUrl) || string.IsNullOrWhiteSpace(settings.XboxPortalPassword))
        {
            Console.Error.WriteLine("Xbox portal URL/password not configured (settings.json or the Deploy page).");
            return false;
        }
        var appx = new PackagingService(layout).AppxPath;
        if (!File.Exists(appx))
        {
            Console.Error.WriteLine($"appx not found: {appx} (run 'package' first)");
            return false;
        }
        var version = new PackagingService(layout).Version();
        using var svc = new XboxDeployService(settings.XboxPortalUrl, settings.XboxPortalUser ?? "", settings.XboxPortalPassword ?? "");
        // A console that has never trusted the dev cert rejects the install; uploading it is harmless when already present.
        var cer = string.IsNullOrWhiteSpace(settings.PfxPath) ? null : Path.ChangeExtension(settings.PfxPath, ".cer");
        if (cer != null && File.Exists(cer))
        {
            var certOk = await svc.InstallCertificateAsync(cer, progress, CancellationToken.None);
            progress.Report(certOk ? "Dev certificate accepted by console." : "Certificate upload refused (continuing; may already be installed).");
        }
        return await svc.DeployAppxAsync(appx, version, progress, CancellationToken.None);
    }

    private static async Task<bool> TranslateAsync(AppSettings settings, WorkspaceLayout layout,
        BootstrapService bootstrap, IProgress<string> progress)
    {
        if (!File.Exists(bootstrap.TranslatorExe))
        {
            Console.Error.WriteLine($"Translator missing: {bootstrap.TranslatorExe} (run 'bootstrap' first)");
            return false;
        }
        var stage = new RetroRewindStage(settings, s => Console.WriteLine(s));
        if (!stage.Prepare(out var msg))
        {
            Console.Error.WriteLine($"Retro Rewind not staged: {msg}");
            return false;
        }
        var translation = new TranslationService(new ProcessRunner(), layout, bootstrap.TranslatorExe);
        var reuse = await translation.BaseCoversCodePulAsync(progress, CancellationToken.None);
        progress.Report($"Base translation reuse: {reuse}");
        return await translation.TranslateAsync(settings, includeRetro: true, reuseBase: reuse, progress, CancellationToken.None);
    }
}
