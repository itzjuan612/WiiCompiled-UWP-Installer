using WiiCompiledInstaller.Pages;
using WiiCompiledInstaller.Services;

namespace WiiCompiledInstaller;

/// <summary>Shell: a TabControl of workflow pages over a shared streaming log pane.</summary>
public sealed class MainForm : Form
{
    private readonly InstallerSession _session;
    private readonly LogPane _log;

    public MainForm()
    {
        Text = "WiiCompiled UWP Installer";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1040;
        Height = 760;
        MinimumSize = new Size(860, 600);

        _session = new InstallerSession();
        _log = new LogPane { Dock = DockStyle.Fill, Height = 220 };

        var tabs = new TabControl { Dock = DockStyle.Fill };
        TabsSetup(tabs);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel2,
        };
        split.Panel1.Controls.Add(tabs);
        split.Panel2.Controls.Add(_log);

        var menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("&File");
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("&Save settings", null, (_, _) => SaveSettings()));
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close()));
        var actions = new ToolStripMenuItem("&Actions");
        actions.DropDownItems.Add(new ToolStripMenuItem("&Preflight (detect toolchain)", null, async (_, _) => await PreflightAsync()));
        menu.Items.AddRange(new ToolStripItem[] { fileMenu, actions });

        MainMenuStrip = menu;
        Controls.Add(split);
        Controls.Add(menu);

        Load += (_, _) =>
        {
            // Size the splitter only after the form has its final (DPI-scaled) size.
            var splitContainer = (SplitContainer)Controls[0];
            splitContainer.SplitterDistance = Math.Max(splitContainer.Panel1MinSize,
                splitContainer.Height - splitContainer.Panel2MinSize - 240);
            _log.Info($"Installer root: {_session.Store.InstallerRoot}");
        };
    }

    private void TabsSetup(TabControl tabs)
    {
        var setup = new SetupPage(_session, _log) { Dock = DockStyle.Fill };
        tabs.TabPages.Add(new TabPage("Setup") { Tag = setup });
        tabs.TabPages[0].Controls.Add(setup);
        var rr = new RetroRewindPage(_session, _log) { Dock = DockStyle.Fill };
            var rrTab = new TabPage("Retro Rewind");
            rrTab.Controls.Add(rr);
            tabs.TabPages.Add(rrTab);
        var build = new BuildPage(_session, _log) { Dock = DockStyle.Fill };
        var buildTab = new TabPage("Build");
        buildTab.Controls.Add(build);
        tabs.TabPages.Add(buildTab);
        var config = new ConfigPage(_session, _log) { Dock = DockStyle.Fill };
            var configTab = new TabPage("Config");
            configTab.Controls.Add(config);
            tabs.TabPages.Add(configTab);
        var deploy = new DeployPage(_session, _log) { Dock = DockStyle.Fill };
            var deployTab = new TabPage("Deploy");
            deployTab.Controls.Add(deploy);
            tabs.TabPages.Add(deployTab);
    }

    private void SaveSettings()
    {
        _session.Store.Save();
        _log.Info("Settings saved.");
    }

    private async Task PreflightAsync()
    {
        var progress = new Progress<string>(s => _log.Info(s));
        _log.Info("Preflight: locating MSVC + Windows SDK...");
        _session.Toolchain = await Task.Run(() => ToolchainLocator.Locate(progress));
        _log.Info(_session.Toolchain.IsComplete
            ? "Preflight OK — UWP toolchain found."
            : "Preflight INCOMPLETE — install Visual Studio 2022 (C++ desktop) and the Windows 10/11 SDK with UWP components.");
    }
}
