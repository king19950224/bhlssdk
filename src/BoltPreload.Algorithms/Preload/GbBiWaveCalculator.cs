namespace BoltPreload.Algorithms.Preload;

/// <summary>
/// GB/T 43232-2023 双波法（详见方案 §8.3）。
/// <para>公式：<c>σ_b = v_T0 · (t_Lσ · x₀ - t_Tσ) / (2·L_e · (A_L - A_T))</c></para>
/// <para>精度约 ±3%。需要同时测纵波和横波 TOF。Phase 3+ 实现。</para>
/// <para>**仅适用于公称直径 ≥ M12** 的螺栓（GB/T 43232 适用边界）。</para>
/// </summary>
public sealed class GbBiWaveCalculator : IPreloadCalculator
{
    public string ModelName => "GbBiWave";
    public string Version => "1.0.0";
    public PreloadAccuracyLevel AccuracyLevel => PreloadAccuracyLevel.GbBiWave;

    public PreloadResult Calculate(PreloadInput input)
    {
        // Phase 3+ 完整实现：需要 ChannelResult.EchoTime2（横波 TOF）
        // 当前 PreloadInput 没有横波 TOF 字段，要先扩展。
        throw new NotImplementedException("GbBiWaveCalculator: Phase 3+ 实现，需先扩展输入字段");
    }
}
