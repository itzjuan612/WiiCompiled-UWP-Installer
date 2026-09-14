using WiiCompiledInstaller.Models;
using WiiCompiledInstaller.Services;

namespace WiiCompiledInstaller.Pages;

/// <summary>
/// First-run + settings page: workspace location, repo clone/open, the user's own
/// disc dump + Retro Rewind pack (verified, never bundled), the signing cert, and the
/// Xbox Dev Portal endpoint. Drives Setup -> Preflight.
/// </summary>
public sealed class SetupPage : UserControl
{
    private readonly InstallerSession _session;
    private readonly LogPane _log;

    private readonly TextBox _repoUrl = BrowseField();
    private readonly TextBox _workspace = BrowseField();
    private readonly TextBox _dump = BrowseField();
    private readonly TextBox _rr = BrowseField();
    private readonly TextBox _pfx = BrowseField();
    private readonly TextBox _pfxPw = new() { Width = 200, UseSystemPasswordChar = true };
    private readonly TextBox _portal = BrowseField();
    private readonly TextBox _portalUser = BrowseField();
    private readonly TextBox _portalPw = new() { Width = 200, UseSystemPasswordChar = true };
    private readonly TextBox _certSubject = new() { Width = 200 };
    private readonly NumericUpDown _parallel = new() { Minimum = 0, Maximum = 128, Width = 80 };
    private readonly Label _toolchainStatus = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    private CancellationTokenSource? _cts;

    public SetupPage(InstallerSession session, LogPane log)
    {
        _session = session;
        _log = log;
        Build();
        LoadFrom(_session.Settings);
        _session.Store.Saved += s => LoadFrom(s, onlyRefresh: true);
    }

    private static TextBox BrowseField() => new() { Dock = DockStyle.Fill, MinimumSize = new Size(320, 0) };

    private void Build()
    {
        Padding = new Padding(12);
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 12 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        int r = 0;

        void Row(string label, Control input, Button? browse = null)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) }, 0, r);
            grid.Controls.Add(input, 1, r);
            if (browse is not null) { browse.Height = 28; grid.Controls.Add(browse, 2, r); }
            r++;
        }

        Row("Repo URL", _repoUrl);
        Row("Workspace folder", _workspace, FolderButton(_workspace));
        Row("Disc dump folder", _dump, FolderButton(_dump));
        Row("RetroRewind6 folder", _rr, FolderButton(_rr));
        Row("Signing .pfx", _pfx, FileButton(_pfx, "PFX|*.pfx"));
        Row(".pfx password", _pfxPw);
        Row("Cert subject", _certSubject);
        Row("Xbox portal", _portal);
        Row("Portal user", _portalUser);
        Row("Portal password", _portalPw);
        Row("Build jobs (0=auto)", _parallel);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
        var cloneBtn = new Button { Text = "Clone / Open repo", Height = 30 };
        cloneBtn.Click += async (_, _) => await CloneAsync();
        var saveBtn = new Button { Text = "Save settings", Height = 30 };
        saveBtn.Click += (_, _) => { CommitTo(_session.Settings); _session.Store.Save(); _log.Success("Settings saved."); };
        var preBtn = new Button { Text = "Preflight toolchain", Height = 30 };
        preBtn.Click += async (_, _) => await PreflightAsync();
        var certBtn = new Button { Text = "Create dev cert", Height = 30 };
        certBtn.Click += async (_, _) => await CreateCertAsync();
        var cancelBtn = new Button { Text = "Cancel", Height = 30, Enabled = false };
        cancelBtn.Click += (_, _) => _cts?.Cancel();
        actions.Controls.AddRange(new Control[] { cloneBtn, saveBtn, preBtn, certBtn, cancelBtn });
        _cancelButton = cancelBtn;

        grid.Controls.Add(actions, 1, r);
        grid.Controls.Add(_toolchainStatus, 2, r);
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40) { SizeType = SizeType.AutoSize });

        Controls.Add(grid);
    }

    private Button? _cancelButton;

    private static Button FolderButton(TextBox target)
    {
        var b = new Button { Text = "Browse..." };
        b.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = target.Text };
            if (d.ShowDialog() == DialogResult.OK) target.Text = d.SelectedPath;
        };
        return b;
    }

    private static Button FileButton(TextBox target, string filter)
    {
        var b = new Button { Text = "Browse..." };
        b.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog { Filter = filter };
            if (ofd.ShowDialog() == DialogResult.OK) target.Text = ofd.FileName;
        };
        return b;
    }

    private void LoadFrom(AppSettings s, bool onlyRefresh = false)
    {
        _repoUrl.Text = s.RepoUrl;
        _workspace.Text = s.WorkspaceDir;
        _dump.Text = s.DiscDumpDirectory;
        _rr.Text = s.RetroRewindDirectory;
        _pfx.Text = s.PfxPath;
        _pfxPw.Text = s.PfxPassword;
        _portal.Text = s.XboxPortalUrl;
        _portalUser.Text = s.XboxPortalUser;
        _portalPw.Text = s.XboxPortalPassword;
        if (string.IsNullOrWhiteSpace(_certSubject.Text)) _certSubject.Text = "CN=MKWii";
        _parallel.Value = Math.Min(_parallel.Maximum, s.ParallelJobs);
    }

    private void CommitTo(AppSettings s)
    {
        s.RepoUrl = _repoUrl.Text.Trim();
        s.WorkspaceDir = Path.GetFullPath(_workspace.Text.Trim());
        s.DiscDumpDirectory = _dump.Text.Trim();
        s.RetroRewindDirectory = _rr.Text.Trim();
        s.PfxPath = _pfx.Text.Trim();
        s.PfxPassword = _pfxPw.Text;
        s.XboxPortalUrl = _portal.Text.Trim();
        s.XboxPortalUser = _portalUser.Text.Trim();
        s.XboxPortalPassword = _portalPw.Text;
        s.ParallelJobs = (int)_parallel.Value;
    }

    private async Task CloneAsync()
    {
        CommitTo(_session.Settings);
        _session.Store.Save();
        var layout = new WorkspaceLayout(_session.Settings.WorkspaceDir);
        _cts = new CancellationTokenSource();
        _cancelButton!.Enabled = true;
        try
        {
            _log.Step("Clone / open workspace repo");
            var ok = await _session.Repos.EnsureCloneAsync(layout.Root, new Progress<string>(s => _log.Info(s)), _cts.Token);
            if (ok) _log.Success($"Workspace ready: {layout.Root}");
            else _log.Error("Workspace not ready.");
        }
        finally
        {
            _cancelButton.Enabled = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private async Task CreateCertAsync()
    {
        CommitTo(_session.Settings);
        if (CertificateService.Exists(_session.Settings) &&
            MessageBox.Show("A signing certificate is already configured. " + _session.Settings.PfxPath + "  Create a new one and replace it?", "Create dev cert", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        var pw = string.IsNullOrEmpty(_pfxPw.Text) ? "mkwii" : _pfxPw.Text;
        _log.Step("Create dev certificate");
        var ok = await CertificateService.CreateAsync(_session.Settings, _session.Store.InstallerRoot,
            _certSubject.Text.Trim(), pw, s => _log.Info(s), CancellationToken.None);
        if (ok)
        {
            _session.Store.Save();
            _pfx.Text = _session.Settings.PfxPath;
            _pfxPw.Text = _session.Settings.PfxPassword;
            _log.Success("Signing certificate ready.");
        }
        else
        {
            _log.Error("Could not create the certificate.");
        }
    }

    private async Task PreflightAsync()
    {
        CommitTo(_session.Settings);
        _session.Store.Save();
        _log.Step("Preflight");
        var progress = new Progress<string>(s => _log.Info(s));
        _session.Toolchain = await Task.Run(() => ToolchainLocator.Locate(progress));
        var t = _session.Toolchain;
        _toolchainStatus.Text = t.IsComplete ? "Toolchain: OK" : "Toolchain: MISSING";
        _toolchainStatus.ForeColor = t.IsComplete ? SystemColors.ControlText : SystemColors.GrayText;
    }
}
