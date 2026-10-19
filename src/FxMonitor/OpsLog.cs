namespace FxMonitor;

/// <summary>
/// 操作记录：exe 旁的「操作记录.log」，一行一条，带时间。记录改变设备状态或力值口径的操作，
/// 如置零、参数下发、加载标定文件。
/// </summary>
internal static class OpsLog
{
    private static readonly object Gate = new();

    public static void Append(string line)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "操作记录.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // 日志写不了不能影响操作本身
        }
    }
}
