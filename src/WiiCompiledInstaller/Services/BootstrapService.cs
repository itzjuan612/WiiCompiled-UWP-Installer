using WiiCompiledInstaller.Models;

namespace WiiCompiledInstaller.Services;

/// <summary>
/// Turns a fresh clone into a buildable tree: portable tools (CMake/Ninja/
/// llvm-mingw), pinned native dependencies (SDL-uwp/Dawn fork trees + the
/// C++/WinRT header set) and the translator CLI. All three are the repo's own
/// PowerShell/`dotnet` entry points — no logic is re-implemented here.
/// Safe to re-run: every step is idempotent and self-skipping.
/// </summary>
public sealed class BootstrapService
{
    private readonly ProcessRunner _runner = new();

    public string Workspace { get; }
    public string PortableTools => Path.Combine(Workspace, "Launcher", "artifacts", "portable-tools");
    public string TranslatorExe => Path.Combine(PortableTools, "Translator", "Translator.Cli.exe");
    public string CMakeExe => Path.Combine(PortableTools, "CMake", "bin", "cmake.exe");
    public string NinjaExe => Path.Combine(PortableTools, "ninja", "ninja.exe");

    public BootstrapService(string workspace) => Workspace = workspace;

    public async Task<bool> RunAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (!await RunPs1("Prepare-PortableTools.ps1", progress, ct)) return false;
        if (!await RunPs1("Prepare-Dependencies.ps1", progress, ct)) return false;
        if (!await new UwpDependencyService(Workspace).EnsureAsync(progress, ct)) return false;
        return await PublishTranslatorAsync(progress, ct);
    }

    private async Task<bool> RunPs1(string script, IProgress<string> progress, CancellationToken ct)
    {
        var path = Path.Combine(Workspace, "Launcher", script);
        progress.Report($"== {script} ==");
        var result = await _runner.RunAsync("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\"", Workspace, null, progress,
            TimeSpan.FromMinutes(30), ct);
        if (!result.Success) progress.Report($"{script} failed (exit {result.ExitCode})");
        return result.Success;
    }

    private async Task<bool> PublishTranslatorAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (File.Exists(TranslatorExe))
        {
            progress.Report($"Translator already published: {TranslatorExe}");
            return true;
        }
        progress.Report("== Building the translator (dotnet publish) ==");
        var project = Path.Combine(Workspace, "translator", "src", "Translator.Cli", "Translator.Cli.csproj");
        var result = await _runner.RunAsync("dotnet",
            $"publish \"{project}\" -c Release -o \"{Path.GetDirectoryName(TranslatorExe)}\"",
            Workspace, null, progress, TimeSpan.FromMinutes(15), ct);
        if (!result.Success) progress.Report($"translator publish failed (exit {result.ExitCode})");
        return result.Success;
    }
}

