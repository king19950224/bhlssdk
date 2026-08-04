using BoltPreload.Fx;

namespace BoltPreload.Application.Ingestion;

/// <summary>
/// 等 connector 进入 Connected 的公共轮询。
/// <para>
/// 采集启动（IngestionService）与看门狗重连（IngestionWatchdogService）都要等这一下。
/// 此前两处各写了一份逐字相同的循环，只有日志文案不同 —— 改等待策略得记得两边一起改。
/// </para>
/// <para>
/// 用轮询而不是 Task.Delay 硬等一秒：硬等对慢机器不够、对快机器纯浪费。
/// </para>
/// </summary>
internal static class FxConnectorWait
{
    private const int TimeoutMs = 5000;
    private const int PollIntervalMs = 50;

    /// <summary>
    /// 等到 Connected 返回 true，超时返回 false。
    /// 不在这里记日志：两个调用方的语境（首次连接 / 重连后）不同，文案由调用方给。
    /// </summary>
    public static async Task<bool> UntilConnectedAsync(IFxConnector connector, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < TimeoutMs)
        {
            if (connector.State == FxConnectorState.Connected) return true;
            await Task.Delay(PollIntervalMs, ct);
        }
        return false;
    }
}
