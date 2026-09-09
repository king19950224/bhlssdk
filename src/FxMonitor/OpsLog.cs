namespace FxMonitor;

/// <summary>
/// 操作记录：exe 旁的「操作记录.log」，一行一条带时间。会改设备状态或改变力值口径的动作都记——
/// 置零、参数下发、加载标定文件。出了问题要能回答"谁在什么时候把什么改成了什么"。
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
