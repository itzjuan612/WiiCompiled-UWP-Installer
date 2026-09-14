namespace WiiCompiledInstaller;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--headless", StringComparison.OrdinalIgnoreCase))
            return Headless.Run(args[1..]);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
