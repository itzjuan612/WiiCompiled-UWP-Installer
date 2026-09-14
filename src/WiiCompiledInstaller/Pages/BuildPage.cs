using WiiCompiledInstaller.Models;
using WiiCompiledInstaller.Services;

namespace WiiCompiledInstaller.Pages;

/// <summary>
/// One-click "Translate + compile both products" driver with granular buttons:
/// bootstrap (tools/deps/translator), toolchain preflight, MSVC junctions,
/// configure, translate, compile WiiCompiled / RetroRewind.
/// </summary>
public sealed class BuildPage : UserControl
{
    private readonly InstallerSession _session;
    private readonly LogPane _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly TableLayoutPanel _grid;
    private readonly Button _bootstrapBtn = new();
    private readonly Button _preflightBtn = new();
    private readonly Button _translateBtn = new();
    private readonly Button _compileWiiBtn = new();
    private readonly Button _compileRrBtn = new();
    private readonly Button _fullBtn = new();
    private readonly Button _cancelBtn = new();
    private readonly Label _status = new();
    private bool _busy;

    public BuildPage(InstallerSession session, LogPane log)
    {
        _session = session;
        _log = log;
        Dock = DockStyle.Fill;
        Padding = new Padding(12);

        _grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(_grid);

        var row1 = Flow((_bootstrapBtn, "1. Bootstrap tools + deps", BootstrapAsync));
        var b = AddButton(row1, "2. Preflight toolchain", PreflightAsync);
        var row2 = new FlowLayoutPanel { Dock = DockStyle.Fill };
        b = AddButton(row2, "3. Translate (base + mod)", TranslateAsync);
        b = AddButton(row2, "4a. Compile WiiCompiled", () => CompileAsync("WiiCompiled"));
        b = AddButton(row2, "4b. Compile RetroRewind", () => CompileAsync("RetroRewind"));
        b = AddButton(row2, "Build all (3+4a+4b)", FullAsync);
        b.BackColor = Color.FromArgb(38, 74, 44);
        b.ForeColor = Color.White;
        _cancelBtn.Text = "Cancel"; _cancelBtn.AutoSize = true; _cancelBtn.Margin = new Padding(4); _cancelBtn.Enabled = false;
        _cancelBtn.Click += (_, _) => _cts.Cancel();
        row2.Controls.Add(_cancelBtn);
        row1.WrapContents = false; row2.WrapContents = false;

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Text = "Idle.";
        _grid.Controls.Add(row1, 0, 0);
        _grid.Controls.Add(row2, 0, 1);
        _grid.Controls.Add(_status, 0, 3);

        var steps = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Each step is incremental: re-running is cheap unless the toolchain changed.\n" +
                   "First full build translates ~29k functions and compiles Aurora/Dawn/SDL from source — expect 1–2 h.",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Gray,
        };
        _grid.Controls.Add(steps, 0, 2);
    }

    private FlowLayoutPanel Flow(params (Button button, string text, Func<Task> action)[] items)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill };
        foreach (var (button, text, action) in items)
        {
            button.Text = text;
            button.AutoSize = true;
            button.Margin = new Padding(4);
            button.Click += async (_, _) => await Guard(action);
            panel.Controls.Add(button);
        }
        return panel;
    }

    private Button AddButton(FlowLayoutPanel panel, string text, Func<Task> action)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(4) };
        b.Click += async (_, _) => await Guard(action);
        panel.Controls.Add(b);
        return b;
    }

    private async Task Guard(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; _cancelBtn.Enabled = true;
        try { await action(); }
        catch (OperationCanceledException) { _log.Warn("Cancelled."); }
        catch (Exception ex) { _log.Error(ex.Message); }
        finally { _busy = false; _cancelBtn.Enabled = false; _status.Text = "Idle."; }
    }

    private async Task<bool> PreflightAsync()
    {
        if (_session.Toolchain is null)
        {
            _log.Step("Preflight: locating Visual Studio + Windows SDK");
            _session.Toolchain = await Task.Run(() => ToolchainLocator.Locate(new Progress<string>(s => _log.Info(s))));
        }
        if (_session.Toolchain is not { IsComplete: true })
        {
            _log.Error("MSVC / Windows SDK (UWP) not found. Install VS2022 \"Desktop C++\" + a Windows 10/11 SDK.");
            return false;
        }
        var t = _session.Toolchain;
        _log.Success($"MSVC {t.MsvcVersion} @ {t.MsvcRoot}");
        _log.Success($"SDK {t.SdkVersion} @ {t.SdkRoot}");
        return true;
    }

    private async Task BootstrapAsync()
    {
        if (!ReadyRepo()) return;
        _status.Text = "Bootstrapping…";
        var bootstrap = new BootstrapService(_session.Settings.WorkspaceDir);
        var ok = await bootstrap.RunAsync(Progress(), _cts.Token);
        _status.Text = ok ? "Bootstrap complete." : "Bootstrap FAILED (see log).";
    }

    private async Task TranslateAsync()
    {
        if (!ReadyRepo()) return;
        if (!await PreflightRuntimeAsync()) return;
        var layout = _session.Layout;
        var stage = new RetroRewindStage(_session.Settings, s => _log.Info(s));
        if (!stage.Prepare(out string rrMessage))
        {
            _log.Warn($"Retro Rewind not staged: {rrMessage} — building the base game only.");
        }
        var includeRetro = rrMessage == "staged";
        var translation = new TranslationService(_session.Runner, layout, new BootstrapService(_session.Settings.WorkspaceDir).TranslatorExe);
        _status.Text = "Translating…";
        var reuse = await translation.BaseCoversCodePulAsync(Progress(), _cts.Token);
        var ok = await translation.TranslateAsync(_session.Settings, includeRetro, reuse, Progress(), _cts.Token);
        _status.Text = ok ? "Translation complete." : "Translation FAILED (see log).";
    }

    private async Task CompileAsync(string target)
    {
        if (!ReadyRepo()) return;
        if (!await PreflightRuntimeAsync()) return;
        var (bootstrap, build, junctions) = BuildStack();
        _status.Text = $"Ensuring junctions…";
        var j = new MsvcJunctionService().Ensure(_session.Settings, _session.Toolchain!, s => _log.Info(s));
        if (!j.Ok) { _log.Error($"MSVC junctions failed: {j.Message}"); return; }
        _log.Info($"Junctions: {j.Inc} | {j.Lib}");
        if (!await build.ConfigureAsync(_session.Settings, _session.Toolchain!, j.Inc, j.Lib, force: false, Progress(), _cts.Token))
        { _status.Text = "Configure FAILED (see log)."; return; }
        _status.Text = $"Compiling {target}…";
        var ok = await build.BuildTargetAsync(target, _session.Settings, _session.Toolchain!, j.Inc, j.Lib, Progress(), _cts.Token);
        _status.Text = ok ? $"{target}.exe ready in native-build-uwp\\." : $"{target} compile FAILED (see log).";
        _ = bootstrap;
    }

    private async Task FullAsync()
    {
        await TranslateAsync();
        if (_status.Text.StartsWith("Translation complete", StringComparison.Ordinal) || _status.Text == "Idle.")
        {
            await CompileAsync("WiiCompiled");
            await CompileAsync("RetroRewind");
        }
    }

    private async Task<bool> PreflightRuntimeAsync() =>
        _session.Toolchain is not null || await PreflightAsync();

    private (BootstrapService, UwpBuildService, MsvcJunctionService) BuildStack()
    {
        var bootstrap = new BootstrapService(_session.Settings.WorkspaceDir);
        return (bootstrap, new UwpBuildService(_session.Layout, bootstrap), new MsvcJunctionService());
    }

    private bool ReadyRepo()
    {
        if (!_session.Layout.HasLauncher)
        {
            _log.Error("Workspace has no repo checkout. Run Setup → Clone first.");
            return false;
        }
        return true;
    }

    private IProgress<string> Progress() => new Progress<string>(s => _log.Info(s));
}
