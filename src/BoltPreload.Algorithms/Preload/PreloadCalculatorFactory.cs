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
/// 双波法（≥M12、精度更高）尚未实现。<see cref="Pick"/> 里 ≥M12 的路由已就位，
/// 由 <see cref="GbBiWaveCalculator.IsAvailable"/> 守着：实现可用前 ≥M12 自动落到单波法，
/// 不会路由到没实现的分支上去。
/// </para>
/// </remarks>
public interface IPreloadCalculatorFactory
{
    IPreloadCalculator Pick(BoltSpec bolt, CalibrationData? calibration, DateTimeOffset now);
}

/// <summary>默认工厂实现，路由规则见 <see cref="IPreloadCalculatorFactory"/> 注释。</summary>
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

        // M12 以上走双波法，但 IsAvailable=false（未实现）时落到下面的单波分支
        if (bolt.NominalDiamMm >= 12m && _biWave.IsAvailable) return _biWave;

        // M5 以上 + 标定齐全 → 单波法
        if (bolt.NominalDiamMm >= 5m) return _singleWave;

        // 兜底
        return _simplified;
    }
}
