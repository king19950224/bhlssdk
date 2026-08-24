namespace BoltPreload.Calibration;

/// <summary>
/// 标定试验的力值平台期检测（force-sync-calibration-design §3）。
/// <para>
/// 试验模式下外部力值（人工进样/串口测力仪/设备 AI）连续到达，本类回答一个问题：
/// "此刻是不是一个可以出标定点的稳态平台"。从最新样本向历史扩展，只要扩展集的
/// 极差不超过允差带就继续并——平台越长，均值越稳、窗口内可配对的声时样本越多。
/// </para>
/// <para>
/// 纯函数、无状态：调用方（试验端点）每次力值进样后重算即可。去重（同一平台不出两个点、
/// 相邻档位必须拉开步距）由调用方按已生成点判断——检测器只管"稳不稳"，不管"该不该出点"，
/// 与 EchoStabilityGuard（只管声时稳不稳）分工一致。
/// </para>
/// </summary>
public static class ForcePlateauDetector
{
    /// <summary>一条力值样本。</summary>
    public sealed record Sample(DateTimeOffset Ts, decimal ForceN);

    /// <summary>检测到的平台期。</summary>
    /// <param name="Start">平台起点（最早一条仍在允差带内的样本时刻）</param>
    /// <param name="End">平台终点（最新样本时刻）</param>
    /// <param name="MeanForceN">平台内力值均值（N）——出点时作为 estimated_preload_n</param>
    /// <param name="SampleCount">平台内力值样本数</param>
    public sealed record Plateau(DateTimeOffset Start, DateTimeOffset End, decimal MeanForceN, int SampleCount)
    {
        public double DurationSeconds => (End - Start).TotalSeconds;
    }

    // ===== 默认判据（试验端点引用；将来有现场经验再调） =====

    /// <summary>平台最短持续时间（秒）。短于它的"停顿"多半是换手/读表，不算档位。</summary>
    public const int MinDurationSeconds = 10;

    /// <summary>平台内力值样本数下限（人工进样 2s 一条 → 10s 有 5 条）。</summary>
    public const int MinSamples = 5;

    /// <summary>允差带绝对下限（N）。低载荷段相对带太窄，人工读表的抖动会把真平台拆碎。</summary>
    public const decimal AbsBandN = 500m;

    /// <summary>允差带相对值（占平台均值）。1% 对应扭矩扳手保持精度的量级。</summary>
    public const decimal RelBand = 0.01m;

    /// <summary>相邻档位的最小力值步距（N）。小于它视为同一档，不重复出点。</summary>
    public const decimal MinStepN = 2000m;

    /// <summary>
    /// 从样本流（按时间升序）检测"以最新样本收尾"的平台期。返回 null = 当前不在平台上。
    /// </summary>
    /// <param name="samples">按 Ts 升序的样本（调用方只需给最近几分钟）</param>
    /// <param name="minDurationSeconds">平台最短持续（秒）</param>
    /// <param name="minSamples">平台内样本数下限</param>
    /// <param name="absBandN">允差带绝对下限（N）</param>
    /// <param name="relBand">允差带相对值（占均值）</param>
    public static Plateau? Detect(
        IReadOnlyList<Sample> samples,
        int minDurationSeconds = MinDurationSeconds,
        int minSamples = MinSamples,
        decimal absBandN = AbsBandN,
        decimal relBand = RelBand)
    {
        if (samples.Count == 0) return null;

        // 从最新样本向前扩展：维护扩展集的 min/max/sum，极差一超允差带就停。
        // 允差带随扩展集均值变化（相对带），因此每步用当前扩展集的均值重算——
        // 这让高载荷段允差更宽（1% of 100kN = 1kN），低载荷段由绝对带 0.5kN 兜底。
        int end = samples.Count - 1;
        decimal min = samples[end].ForceN, max = samples[end].ForceN, sum = 0;
        int count = 0;
        int startIdx = end;

        for (int i = end; i >= 0; i--)
        {
            var f = samples[i].ForceN;
            var newMin = Math.Min(min, f);
            var newMax = Math.Max(max, f);
            var newSum = sum + f;
            var newCount = count + 1;
            var mean = newSum / newCount;
            var band = Math.Max(absBandN, Math.Abs(mean) * relBand);
            if (newMax - newMin > band) break;   // 这条样本把极差撑破了允差带 → 平台到此为止

            min = newMin; max = newMax; sum = newSum; count = newCount;
            startIdx = i;
        }

        if (count < minSamples) return null;
        var start = samples[startIdx].Ts;
        var endTs = samples[end].Ts;
        if ((endTs - start).TotalSeconds < minDurationSeconds) return null;

        return new Plateau(start, endTs, Math.Round(sum / count, 2), count);
    }
}
