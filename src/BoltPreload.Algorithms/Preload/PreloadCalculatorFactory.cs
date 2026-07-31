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
/// 注：双波法（≥M12、精度更高）尚未实现。<see cref="Pick"/> 已就位 ≥M12 路由，但加了
/// <see cref="GbBiWaveCalculator.IsAvailable"/> 守卫：bi-wave 实现可用前自动回退单波法，
/// 确保 ≥M12 螺栓不会因双波法未实现而丢失预紧力计算（也绝不会路由到会抛异常的未实现分支）。
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

        // M12 以上 → 双波法（精度更高，未实现）。bi-wave 未实现可用（IsAvailable=false）时
        // 自动 fallthrough 到单波法，确保 ≥M12 螺栓不丢失预紧力计算，也不会调到未实现的双波法。
        if (bolt.NominalDiamMm >= 12m && _biWave.IsAvailable) return _biWave;

        // M5 以上 + 标定齐全 → 单波法
        if (bolt.NominalDiamMm >= 5m) return _singleWave;

        // 兜底
        return _simplified;
    }
}
