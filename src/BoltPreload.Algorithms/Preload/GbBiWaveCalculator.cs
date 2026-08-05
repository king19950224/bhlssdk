namespace BoltPreload.Algorithms.Preload;

/// <summary>
/// GB/T 43232-2023 双波法（详见方案 §8.3）。
/// <para>公式：<c>σ_b = v_T0 · (t_Lσ · x₀ - t_Tσ) / (2·L_e · (A_L - A_T))</c></para>
/// <para>精度约 ±3%。需要同时测纵波和横波 TOF。</para>
/// <para><b>仅适用于公称直径 ≥ M12</b> 的螺栓（GB/T 43232 适用边界）。</para>
/// <para>
/// 当前未实现（<see cref="IsAvailable"/>=false）：完整实现需先给 <see cref="PreloadInput"/> 扩展横波 TOF
/// （<c>ChannelResult.EchoTime2</c>）。在此之前，<see cref="Calculate"/> 采取【安全降级】而非抛异常——
/// 绝不在采集热路径上抛 <see cref="System.NotImplementedException"/> 拖垮入库；
/// 且 <see cref="PreloadCalculatorFactory"/> 在 <see cref="IsAvailable"/>=false 时会把 ≥M12 螺栓回退到单波法。
/// </para>
/// </summary>
public sealed class GbBiWaveCalculator : IPreloadCalculator
{
    public string ModelName => "GbBiWave";
    public string Version => "1.0.0";
    public PreloadAccuracyLevel AccuracyLevel => PreloadAccuracyLevel.GbBiWave;

    /// <summary>双波法是否已实现可用。实现横波 TOF 数据源后置为 true，工厂据此启用 ≥M12 路由。</summary>
    public bool IsAvailable => false;

    public PreloadResult Calculate(PreloadInput input)
    {
        // 双波法（≥M12，±3%）尚未实现：需要横波 TOF（ChannelResult.EchoTime2），当前 PreloadInput 尚无该字段。
        // 安全降级：返回"无法计算"（PreloadN=null）并在 ModelUsed 标注原因，由上层按未计算处理（不写错误预紧力）。
        // 正常不会走到这里——工厂在 IsAvailable=false 时已把 ≥M12 改走单波法；此分支仅防御直接误用。
        return new PreloadResult(
            PreloadN: null,
            UncertaintyN: null,
            DeltaTofNs: 0m,
            WithinGbRange: false,
            ModelUsed: ModelName + ":unavailable",
            CalibrationId: input.Calibration?.CalibrationId);
    }
}
