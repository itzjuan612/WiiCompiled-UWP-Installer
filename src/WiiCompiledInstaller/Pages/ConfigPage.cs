using System.Globalization;
using System.Text.RegularExpressions;
using WiiCompiledInstaller.Models;
using WiiCompiledInstaller.Services;

namespace WiiCompiledInstaller.Pages;

/// <summary>
/// Edits the [video] defaults that get baked into the packaged
/// Config.default.toml (first-run seed) and the appx version.
/// </summary>
public sealed class ConfigPage : UserControl
{
    private readonly InstallerSession _session;
    private readonly LogPane _log;
    private readonly CheckBox _widescreen;
    private readonly CheckBox _skipUnready;
    private readonly NumericUpDown _resMult;
    private readonly NumericUpDown _fps;
    private readonly NumericUpDown _workers;
    private readonly NumericUpDown _prewarm;
    private readonly TextBox _hotkey;
    private readonly TextBox _version;

    public ConfigPage(InstallerSession session, LogPane log)
    {
        _session = session; _log = log;
        var d = session.Settings.Defaults;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        int r = 0;
        void Row(string label, Control c)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) }, 0, r);
            c.Anchor = AnchorStyles.Left;
            grid.Controls.Add(c, 1, r++);
        }

        _widescreen = new CheckBox { Checked = d.Widescreen, AutoSize = true, Text = "16:9" };
        Row("Widescreen", _widescreen);
        _resMult = new NumericUpDown { Minimum = 1, Maximum = 8, Value = d.ResolutionMultiplier, Width = 120 };
        Row("Resolution multiplier", _resMult);
        _fps = new NumericUpDown { Minimum = 0, Maximum = 180, Value = d.FrameInterpolationFps, Width = 120 };
        Row("Frame interpolation fps (0 = native 60)", _fps);
        _skipUnready = new CheckBox { Checked = d.SkipUnreadyPipelines, AutoSize = true, Text = "skip_unready_pipelines" };
        Row("Skip draws while shaders compile", _skipUnready);
        _workers = new NumericUpDown { Minimum = 0, Maximum = 64, Value = d.PipelineCompileWorkers, Width = 120 };
        Row("Pipeline compile workers (0 = platform default)", _workers);
        _prewarm = new NumericUpDown { Minimum = 0, Maximum = 4096, Value = d.PrewarmMinFreeMb, Width = 120 };
        Row("Prewarm min free MB (0 = 900 on Xbox)", _prewarm);
        _hotkey = new TextBox { Text = d.OverlayHotkey, Width = 320 };
        Row("Overlay hotkey ('+'-joined buttons)", _hotkey);
        _version = new TextBox { Text = session.Settings.PackageVersion, Width = 160 };
        Row("Package version (major.MINOR.PATCH.build)", _version);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(MakeButton("Save + write packaged Config.default.toml", ApplyAsync));
        buttons.Controls.Add(MakeButton("Reload from file", () => { LoadFromFile(); return Task.CompletedTask; }));
        grid.Controls.Add(buttons, 0, r);
        grid.SetColumnSpan(buttons, 2);

        var note = new Label
        {
            AutoSize = true, ForeColor = SystemColors.GrayText,
            Text = "This edits the seed that first-run installs copy into their Config.toml; existing installs keep theirs.",
            Margin = new Padding(3, 16, 3, 3),
        };
        grid.Controls.Add(note, 0, r + 1);
        grid.SetColumnSpan(note, 2);

        Controls.Add(grid);
        LoadFromFile();
    }

    private static Button MakeButton(string text, Func<Task> onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(4) };
        b.Click += async (_, _) => await onClick();
        return b;
    }

    private string ConfigPath => new WorkspaceLayout(_session.Settings.WorkspaceDir).ConfigDefault;

    private void LoadFromFile()
    {
        if (!File.Exists(ConfigPath)) return;
        var text = File.ReadAllText(ConfigPath);
        var defaults = _session.Settings.Defaults;
        defaults.Widescreen = ReadBool(text, "widescreen", defaults.Widescreen);
        defaults.ResolutionMultiplier = ReadInt(text, "resolution_multiplier", defaults.ResolutionMultiplier);
        defaults.FrameInterpolationFps = ReadInt(text, "frame_interpolation_fps", defaults.FrameInterpolationFps);
        defaults.SkipUnreadyPipelines = ReadBool(text, "skip_unready_pipelines", defaults.SkipUnreadyPipelines);
        defaults.PipelineCompileWorkers = ReadInt(text, "pipeline_compile_workers", defaults.PipelineCompileWorkers);
        defaults.PrewarmMinFreeMb = ReadInt(text, "prewarm_min_free_mb", defaults.PrewarmMinFreeMb);
        defaults.OverlayHotkey = ReadString(text, "overlay_hotkey", defaults.OverlayHotkey);
        _widescreen.Checked = defaults.Widescreen;
        _resMult.Value = (decimal)defaults.ResolutionMultiplier;
        _fps.Value = defaults.FrameInterpolationFps;
        _skipUnready.Checked = defaults.SkipUnreadyPipelines;
        _workers.Value = defaults.PipelineCompileWorkers;
        _prewarm.Value = defaults.PrewarmMinFreeMb;
        _hotkey.Text = defaults.OverlayHotkey;
        _version.Text = _session.Settings.PackageVersion;
        _log.Info($"Loaded packaged defaults from {ConfigPath}");
    }

    private Task ApplyAsync()
    {
        var d = _session.Settings.Defaults;
        d.Widescreen = _widescreen.Checked;
        d.ResolutionMultiplier = (int)_resMult.Value;
        d.FrameInterpolationFps = (int)_fps.Value;
        d.SkipUnreadyPipelines = _skipUnready.Checked;
        d.PipelineCompileWorkers = (int)_workers.Value;
        d.PrewarmMinFreeMb = (int)_prewarm.Value;
        d.OverlayHotkey = _hotkey.Text.Trim().Length == 0 ? d.OverlayHotkey : _hotkey.Text.Trim();
        _session.Settings.PackageVersion = Normalize(_version.Text.Trim());
        _session.Store.Save();

        if (!File.Exists(ConfigPath))
        {
            _log.Error($"Packaged config not found: {ConfigPath} (clone the repo first)");
            return Task.CompletedTask;
        }
        var lines = File.ReadAllLines(ConfigPath).ToList();
        SetKey(lines, "widescreen", d.Widescreen ? "true" : "false");
        SetKey(lines, "resolution_multiplier", d.ResolutionMultiplier.ToString(CultureInfo.InvariantCulture));
        SetKey(lines, "frame_interpolation_fps", d.FrameInterpolationFps.ToString());
        SetKey(lines, "skip_unready_pipelines", d.SkipUnreadyPipelines ? "true" : "false");
        if (d.PipelineCompileWorkers > 0) SetKey(lines, "pipeline_compile_workers", d.PipelineCompileWorkers.ToString());
        if (d.PrewarmMinFreeMb > 0) SetKey(lines, "prewarm_min_free_mb", d.PrewarmMinFreeMb.ToString());
        SetKey(lines, "overlay_hotkey", $"\"{d.OverlayHotkey}\"");
        File.WriteAllLines(ConfigPath, lines);
        _log.Success($"Packaged Config.default.toml updated ({ConfigPath}).");

        if (!string.IsNullOrWhiteSpace(_session.Settings.PackageVersion))
        {
            var manifest = new WorkspaceLayout(_session.Settings.WorkspaceDir).Manifest;
            if (File.Exists(manifest))
            {
                new PackagingService(new WorkspaceLayout(_session.Settings.WorkspaceDir)).SetVersion(_session.Settings.PackageVersion);
                _log.Success($"AppxManifest.xml Version -> {Normalize(_session.Settings.PackageVersion)}");
            }
        }
        return Task.CompletedTask;
    }

    private static string Normalize(string v)
    {
        var parts = v.Split('.');
        if (parts.Length == 3) v += ".0";
        return v;
    }

    /// <summary>Replace an active or commented key=value line inside [video]; insert after the header if absent.</summary>
    private static void SetKey(List<string> lines, string key, string value)
    {
        var active = new Regex($@"^(\s*){Regex.Escape(key)}\s*=.*$");
        var commented = new Regex($@"^(\s*)#\s*{Regex.Escape(key)}\s*=.*$");
        bool inVideo = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('[')) { inVideo = t.Equals("[video]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inVideo) continue;
            if (active.IsMatch(lines[i])) { lines[i] = $"{key} = {value}"; return; }
            if (commented.IsMatch(lines[i])) { lines[i] = $"{key} = {value}"; return; }
        }
        if (inVideo) return;
        var header = lines.FindIndex(l => l.Trim().Equals("[video]", StringComparison.OrdinalIgnoreCase));
        if (header < 0) { lines.Insert(0, "[video]"); header = 0; }
        lines.Insert(header + 1, $"{key} = {value}");
    }

    private static string? Raw(string text, string key)
    {
        var m = Regex.Match(text, $@"^\s*{Regex.Escape(key)}\s*=\s*(.+?)\s*(?:#.*)?$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool ReadBool(string t, string k, bool fallback) => Raw(t, k) switch
    {
        "true" => true, "false" => false, _ => fallback,
    };
    private static int ReadInt(string t, string k, int fallback) =>
        int.TryParse(Raw(t, k), out var v) ? v : fallback;
    private static string ReadString(string t, string k, string fallback)
    {
        var raw = Raw(t, k);
        if (raw == null) return fallback;
        var trimmed = raw.Trim('"');
        return trimmed.Length == 0 ? fallback : trimmed;
    }
}
