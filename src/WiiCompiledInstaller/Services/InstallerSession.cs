using WiiCompiledInstaller.Models;

namespace WiiCompiledInstaller.Services;

/// <summary>Shared services handed to each page: settings, the process runner, and cached preflight.</summary>
public sealed class InstallerSession
{
    public SettingsStore Store { get; }
    public ProcessRunner Runner { get; }
    public AppSettings Settings => Store.Current;
    public RepoService Repos { get; }
    public UwpToolchain? Toolchain { get; set; }

    public InstallerSession()
    {
        Store = new SettingsStore();
        Runner = new ProcessRunner();
        Repos = new RepoService(Runner, Settings);
    }

    public WorkspaceLayout Layout => new(Store.Current.WorkspaceDir);
}
