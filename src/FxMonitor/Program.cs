namespace FxMonitor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception, "界面");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Report(ex, "进程");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report(e.Exception, "后台任务", dialog: false);
            e.SetObserved();
        };

        using var single = new Mutex(true, @"Local\Baiheng.FxMonitor", out var first);
        if (!first)
        {
            MessageBox.Show(
                "螺栓预紧力监视器已经打开。\n\n同一台电脑不要开两个：两个程序会同时连一台设备，命令互相打断。",
                "螺栓预紧力监视器",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var endpoint = args.FirstOrDefault(a => !a.StartsWith('-'));
        var auto = args.Contains("--auto");
        var demo = args.Contains("--demo");
        Application.Run(new MainForm(endpoint, auto, demo));
    }

    /// <summary>没接住的异常写进操作记录。界面线程上的弹一句；后台任务只记日志，免得隔很久突然弹出一个窗口。</summary>
    private static void Report(Exception ex, string where, bool dialog = true)
    {
        try
        {
            OpsLog.Append($"未处理异常（{where}）：{ex}");
        }
        catch (Exception)
        {
        }

        if (!dialog) return;
        try
        {
            MessageBox.Show(
                $"程序遇到一个没有接住的错误，已经写进操作记录.log。\n\n{ex.Message}",
                "螺栓预紧力监视器",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception)
        {
        }
    }
}
