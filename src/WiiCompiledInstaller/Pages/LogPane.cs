namespace WiiCompiledInstaller.Pages;

/// <summary>Thread-safe, capped, color-tinted console for process output and step markers.</summary>
public sealed class LogPane : RichTextBox
{
    private const int MaxChars = 400_000;

    public LogPane()
    {
        ReadOnly = true;
        BackColor = System.Drawing.Color.FromArgb(24, 24, 24);
        ForeColor = System.Drawing.Color.Gainsboro;
        Font = new Font("Consolas", 9f);
        BorderStyle = BorderStyle.None;
        DetectUrls = false;
        Dock = DockStyle.Fill;
    }

    public void Info(string message) => Append(message, System.Drawing.Color.Gainsboro);
    public void Step(string message) => Append("== " + message + " ==", System.Drawing.Color.DeepSkyBlue);
    public void Success(string message) => Append(message, System.Drawing.Color.LightGreen);
    public void Warn(string message) => Append(message, System.Drawing.Color.Khaki);
    public void Error(string message) => Append(message, System.Drawing.Color.IndianRed);

    private void Append(string text, System.Drawing.Color color)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => Append(text, color))); return; }
        if (TextLength > MaxChars) Clear();
        SelectionStart = TextLength;
        SelectionLength = 0;
        SelectionColor = color;
        AppendText(text + Environment.NewLine);
        SelectionColor = ForeColor;
        ScrollToCaret();
    }
}
