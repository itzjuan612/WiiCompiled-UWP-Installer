using System.Security.Cryptography;
using WiiCompiledInstaller.Services;

namespace WiiCompiledInstaller.Pages;

public sealed class RetroRewindPage : UserControl
{
    private readonly InstallerSession _session;
    private readonly LogPane _log;
    private readonly TextBox _source;
    private readonly Label _info;

    public RetroRewindPage(InstallerSession session, LogPane log)
    {
        _session = session; _log = log;

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        _source = new TextBox { Dock = DockStyle.Fill, Text = session.Settings.RetroRewindDirectory ?? "", Margin = new Padding(3) };
        panel.Controls.Add(new Label { Text = "Retro Rewind source (RetroRewind6 folder or an update .zip):", AutoSize = true });
        panel.Controls.Add(_source);

        var browseRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        browseRow.Controls.Add(MakeButton("Browse folder...", BrowseFolder));
        browseRow.Controls.Add(MakeButton("Browse update .zip...", BrowseZip));
        panel.Controls.Add(browseRow);

        var actionRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        actionRow.Controls.Add(MakeButton("Inspect", InspectAsync));
        actionRow.Controls.Add(MakeButton("Stage into workspace", StageAsync));
        panel.Controls.Add(actionRow);

        _info = new Label { AutoSize = true, Text = "Point me at your RetroRewind6 folder (or a 6.x update .zip) and press Inspect.", Margin = new Padding(3, 14, 3, 3) };
        panel.Controls.Add(_info);

        var note = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "Staging copies the pack (or just Binaries\\Code.pul out of the zip) into the workspace where the" +
                   Environment.NewLine +
                   "translator expects it. A new Retro Rewind version then only needs a rebuild of RetroRewind -" +
                   Environment.NewLine +
                   "the source itself is not pinned to any RR version.",
        };
        panel.Controls.Add(note);

        Controls.Add(panel);
    }

    private static Button MakeButton(string text, Func<Task> onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(4) };
        b.Click += async (_, _) => await onClick();
        return b;
    }

    private static Button MakeButton(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(4) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog();
        if (dlg.ShowDialog() == DialogResult.OK) _source.Text = dlg.SelectedPath;
    }

    private void BrowseZip()
    {
        using var dlg = new OpenFileDialog { Filter = "Retro Rewind update|*.zip" };
        if (dlg.ShowDialog() == DialogResult.OK) _source.Text = dlg.FileName;
    }

    private Task InspectAsync()
    {
        var src = _source.Text.Trim();
        if (src.Length == 0) { _log.Warn("Enter a pack folder or update .zip first."); return Task.CompletedTask; }
        if (Directory.Exists(src))
        {
            var version = Path.Combine(src, "version.txt");
            var code = FindCodePul(src);
            _info.Text = "version.txt: " + (File.Exists(version) ? File.ReadAllText(version).Trim() : "absent") +
                "   |   Code.pul: " + (code == null ? "NOT FOUND" : $"{new FileInfo(code).Length:N0} B, SHA256 {ShortSha(code)}");
            _log.Info(_info.Text);
        }
        else if (File.Exists(src))
        {
            _info.Text = $"Zip update package: {new FileInfo(src).Length:N0} B. Staging extracts Binaries\\Code.pul from it.";
            _log.Info(_info.Text);
        }
        else _log.Error($"Path not found: {src}");
        return Task.CompletedTask;
    }

    private async Task StageAsync()
    {
        _session.Settings.RetroRewindDirectory = _source.Text.Trim();
        _session.Store.Save();
        await Task.Run(() =>
        {
            var stage = new RetroRewindStage(_session.Settings, s => _log.Info(s));
            if (stage.Prepare(out var msg) && msg == "staged")
                _log.Success("Retro Rewind staged. Rebuild (Build tab) to translate the new Code.pul into RetroRewind.exe.");
            else
                _log.Warn($"Staging result: {msg}");
        });
    }

    private static string? FindCodePul(string root)
    {
        var direct = Path.Combine(root, "Binaries", "Code.pul");
        if (File.Exists(direct)) return direct;
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "Code.pul", SearchOption.AllDirectories))
                if (f.Contains("Binaries", StringComparison.OrdinalIgnoreCase)) return f;
        }
        catch { }
        return null;
    }

    private static string ShortSha(string file)
    {
        using var sha = SHA256.Create();
        using var s = File.OpenRead(file);
        return Convert.ToHexString(sha.ComputeHash(s))[..12];
    }
}
