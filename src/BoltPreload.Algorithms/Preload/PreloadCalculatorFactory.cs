namespace BoltPreload.Algorithms.Preload;

/// <summary>
/// 按螺栓的标定状态、规格自动选择最高精度的可用计算器。
/// </summary>
/// <remarks>
/// 路由策略（按方案 §8.3 模型选择）：
/// <list type="number">
/// <item>螺栓 ≥ M5 + 标定齐全 + 标定未过期 → 单波法（GB 单波，当前生产路径）</item>
/// <item>否则（无标定 / 标定过期 / &lt; M5）→ 简化模型 A（仅看趋势，不作正式监测值）</item>
/// </list>
/// <para>
/// 注：双波法（≥M12、精度更高）属 Phase 3+ 规划，<b>当前未启用</b>——见 <see cref="Pick"/> 内被注释的 M12 分支，
/// 现阶段 ≥M12 螺栓仍走单波法（<see cref="GbBiWaveCalculator"/> 尚未实现，调用会显式抛异常）。
/// </para>
/// </remarks>
public interface IPreloadCalculatorFactory
{
    IPreloadCalculator Pick(BoltSpec bolt, CalibrationData? calibration, DateTimeOffset now);
}

public sealed class PreloadCalculatorFactory : IPreloadCalculatorFactory
{
    private readonly SimplifiedCalculator _simplified;
    private readonly GbSingleWaveCalculator _singleWave;
    private readonly GbBiWaveCalculator _biWave;

    public PreloadCalculatorFactory(
        SimplifiedCalculator simplified,
        GbSingleWaveCalculator singleWave,
        GbBiWaveCalculator biWave)
    {
        _simplified = simplified;
        _singleWave = singleWave;
        _biWave = biWave;
    }

    public IPreloadCalculator Pick(BoltSpec bolt, CalibrationData? calibration, DateTimeOffset now)
    {
        // 无标定 → 简化模型
        if (calibration is null) return _simplified;

        // 标定过期 → 降级到简化模型，UI 上提示重新标定
        if (calibration.IsExpired(now)) return _simplified;

        // M12 以上 → 双波法（Phase 3+ 上线后启用）
        // Phase 1/2 这里 fallthrough 到单波法
        // if (bolt.NominalDiamMm >= 12m) return _biWave;

        // M5 以上 + 标定齐全 → 单波法
        if (bolt.NominalDiamMm >= 5m) return _singleWave;

        // 兜底
        return _simplified;
    }
}
