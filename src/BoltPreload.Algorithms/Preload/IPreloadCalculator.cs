namespace BoltPreload.Algorithms.Preload;

/// <summary>
/// 螺栓预紧力换算器接口（详见方案附录 A.2、§8.3）。
/// </summary>
/// <remarks>
/// 三档实现，按螺栓标定状态自动降级：
/// <list type="number">
/// <item>双波法（最准，±3%）— 未实现，且本机型采不到横波声时，交付口径不含这一档（见 <see cref="GbBiWaveCalculator"/>）</item>
/// <item>GB/T 43232-2023 单波法（±5%）— 已实现，当前生产路径，也是对外承诺的精度</item>
/// <item>简化模型 A（±15%~25%，仅需 v/E/S/L₀，不需标定）— 已实现，无标定时兜底</item>
/// </list>
/// 调用方按 <see cref="IPreloadCalculatorFactory"/> 拿到合适的实例。
/// 对外展示的精度文案统一取 <c>DisplayLabels.PrecisionSpec</c>，不要在页面或报告里另写一份。
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

/// <summary>预紧力计算精度档次，由所用模型决定（工厂按标定状态与螺栓规格路由）。</summary>
public enum PreloadAccuracyLevel
{
    /// <summary>±15%~25%（无标定，模型 A）</summary>
    Simplified,

    /// <summary>±5%（GB/T 43232 单波法）</summary>
    GbSingleWave,

    /// <summary>±3%（GB/T 43232 双波法）。枚举值保留，但本机型采不到横波，工厂不会路由到这一档。</summary>
    GbBiWave
}
