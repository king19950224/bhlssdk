namespace BoltPreload.Algorithms.Preload;

/// <summary>
/// 螺栓预紧力换算器接口（详见方案附录 A.2、§8.3）。
/// </summary>
/// <remarks>
/// 三档实现，按螺栓标定状态自动降级：
/// <list type="number">
/// <item>双波法（最准，±3%）— Phase 2+ 实现</item>
/// <item>GB/T 43232-2023 单波法（±5%）— Phase 2 实现</item>
/// <item>简化模型 A（±15%~25%，仅需 v/E/S/L₀，不需标定）— Phase 1 实现兜底</item>
/// </list>
/// 调用方按 <see cref="IPreloadCalculatorFactory"/> 拿到合适的实例。
/// </remarks>
public interface IPreloadCalculator
{
    /// <summary>算法名（持久化到 measurements.algo_version）</summary>
    string ModelName { get; }

    /// <summary>算法版本（升级时改这个）</summary>
    string Version { get; }

    /// <summary>精度档次</summary>
    PreloadAccuracyLevel AccuracyLevel { get; }

    /// <summary>计算预紧力</summary>
    PreloadResult Calculate(PreloadInput input);
}

public enum PreloadAccuracyLevel
{
    /// <summary>±15%~25%（无标定，模型 A）</summary>
    Simplified,

    /// <summary>±5%（GB/T 43232 单波法）</summary>
    GbSingleWave,

    /// <summary>±3%（GB/T 43232 双波法）</summary>
    GbBiWave
}
