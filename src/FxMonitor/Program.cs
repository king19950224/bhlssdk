namespace FxMonitor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var endpoint = args.FirstOrDefault(a => !a.StartsWith('-'));
        var auto = args.Contains("--auto");
        var demo = args.Contains("--demo");
        Application.Run(new MainForm(endpoint, auto, demo));
    }
}
