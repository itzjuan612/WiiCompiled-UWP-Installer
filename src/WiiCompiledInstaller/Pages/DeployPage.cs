using WiiCompiledInstaller.Services;

namespace WiiCompiledInstaller.Pages;

public sealed class DeployPage : UserControl
{
    private readonly InstallerSession _session;
    private readonly LogPane _log;
    private readonly TextBox _portal;
    private readonly TextBox _user;
    private readonly TextBox _password;
    private bool _busy;

    public DeployPage(InstallerSession session, LogPane log)
    {
        _session = session; _log = log;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        int r = 0;
        void Row(string label, Control c)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) }, 0, r);
            c.Anchor = AnchorStyles.Left;
            grid.Controls.Add(c, 1, r++);
        }
        _portal = new TextBox { Text = session.Settings.XboxPortalUrl ?? "", Width = 320, PlaceholderText = "https://192.168.1.50:11443" };
        Row("Dev Portal URL", _portal);
        _user = new TextBox { Text = session.Settings.XboxPortalUser ?? "", Width = 200 };
        Row("Portal user", _user);
        _password = new TextBox { Text = session.Settings.XboxPortalPassword ?? "", Width = 200, UseSystemPasswordChar = true };
        Row("Portal password", _password);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(MakeButton("Build + sign appx", PackageAsync));
        buttons.Controls.Add(MakeButton("Install certificate on console", CertAsync));
        buttons.Controls.Add(MakeButton("DEPLOY to Xbox", DeployAsync));
        buttons.Controls.Add(MakeButton("Install on this PC", PcInstallAsync));
        grid.Controls.Add(buttons, 0, r);
        grid.SetColumnSpan(buttons, 2);

        var note = new Label
        {
            AutoSize = true, ForeColor = SystemColors.GrayText,
            Text = "The console must be in Dev Mode with the Developer Portal running. The first deploy to a new" +
                   Environment.NewLine +
                   "console also needs the certificate (button above, or it is sent automatically before deploy).",
            Margin = new Padding(3, 16, 3, 3),
        };
        grid.Controls.Add(note, 0, r + 1);
        grid.SetColumnSpan(note, 2);
        Controls.Add(grid);
    }

    private static Button MakeButton(string text, Func<Task> onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(4) };
        b.Click += async (_, _) => await onClick();
        return b;
    }

    private bool Guard()
    {
        if (_busy) return false;
        _busy = true;
        return true;
    }

    private void Unguard() => _busy = false;
    private IProgress<string> Progress() => new Progress<string>(s => _log.Info(s));

    private void CommitSettings()
    {
        var s = _session.Settings;
        s.XboxPortalUrl = _portal.Text.Trim();
        s.XboxPortalUser = _user.Text.Trim();
        s.XboxPortalPassword = _password.Text;
        _session.Store.Save();
    }

    private async Task<bool> ToolchainReadyAsync()
    {
        if (_session.Toolchain is not null) return true;
        _log.Step("Preflight: locating toolchain");
        _session.Toolchain = await Task.Run(() => ToolchainLocator.Locate(Progress()));
        return _session.Toolchain.IsComplete;
    }

    private async Task PackageAsync()
    {
        if (!Guard()) return;
        try
        {
            if (!await ToolchainReadyAsync()) { _log.Error("Toolchain incomplete."); return; }
            var ok = await new PackagingService(_session.Layout).PackageAsync(_session.Settings, _session.Toolchain!, Progress(), CancellationToken.None);
            if (ok) _log.Success("appx ready: " + new PackagingService(_session.Layout).AppxPath);
            else _log.Error("Packaging failed (see log).");
        }
        finally { Unguard(); }
    }

    private async Task CertAsync()
    {
        if (!Guard()) return;
        try
        {
            CommitSettings();
            var cer = Path.ChangeExtension(_session.Settings.PfxPath, ".cer");
            if (!File.Exists(cer)) { _log.Error($"Certificate file not found: {cer}"); return; }
            using var svc = new XboxDeployService(_session.Settings.XboxPortalUrl!, _session.Settings.XboxPortalUser ?? "", _session.Settings.XboxPortalPassword ?? "");
            var ok = await svc.InstallCertificateAsync(cer, Progress(), CancellationToken.None);
            if (ok) _log.Success("Console trusts the dev certificate.");
        }
        finally { Unguard(); }
    }

    private async Task DeployAsync()
    {
        if (!Guard()) return;
        try
        {
            CommitSettings();
            var s = _session.Settings;
            if (string.IsNullOrWhiteSpace(s.XboxPortalUrl)) { _log.Error("Enter the Dev Portal URL first."); return; }
            var packager = new PackagingService(_session.Layout);
            if (!File.Exists(packager.AppxPath)) { _log.Error("appx not built yet - use 'Build + sign appx'."); return; }
            var version = packager.Version();
            using var svc = new XboxDeployService(s.XboxPortalUrl, s.XboxPortalUser ?? "", s.XboxPortalPassword ?? "");
            var cer = Path.ChangeExtension(s.PfxPath, ".cer");
            if (File.Exists(cer))
                await svc.InstallCertificateAsync(cer, Progress(), CancellationToken.None);
            var ok = await svc.DeployAppxAsync(packager.AppxPath, version, Progress(), CancellationToken.None);
            if (ok) _log.Success($"Deployed {version} to the console.");
            else _log.Error("Deploy failed (see log).");
        }
        finally { Unguard(); }
    }

    private async Task PcInstallAsync()
    {
        if (!Guard()) return;
        try
        {
            var packager = new PackagingService(_session.Layout);
            if (!File.Exists(packager.AppxPath)) { _log.Error("appx not built yet - use 'Build + sign appx'."); return; }
            var r = await _session.Runner.RunAsync("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -Command \"Add-AppxPackage -Path '{packager.AppxPath}'\"",
                null, null, Progress(), TimeSpan.FromMinutes(5), CancellationToken.None);
            if (r.Success) _log.Success("Installed on this PC (Start menu: WiiCompiled / Retro Rewind).");
        }
        finally { Unguard(); }
    }
}
