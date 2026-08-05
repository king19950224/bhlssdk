namespace BoltPreload.Algorithms.Preload;

/// <summary>
/// 模型 A：经典弹性力学简化公式（详见方案 §8.3）。
/// <para>
/// 公式：<c>F = E·S·v·Δt / (2·L₀)</c>
/// </para>
/// <para>
/// 物理前提要先说清：本模型把声速 v 当常数（只算路径变长的效应），忽略声弹性效应
/// （应力本身也会改变 v）。直接用碳钢标称声速 v=5890 m/s 时，跟真机标定结果可能差几倍
/// （比如 80mm M16 在 Δt=195ns 下，本公式算出 ~303 kN，而真机 K_L 标定可能是 ~90 kN）。
/// </para>
/// <para>
/// 这个差异不是 bug，是模型本质：模型 A 反映的是"假定声速不变"的理论上限。
/// 工程实际用本模型时，需要把 <c>SoundVelocityMs</c> 字段当作"等效声速 v_effective"，
/// 由现场一颗螺栓的少量标定数据反推得到。直接拿标称声速使用，结果只能作"看趋势"用，
/// 数值无意义。
/// </para>
/// <para>
/// 用途（按方案 §8.3）：
/// </para>
/// <list type="bullet">
/// <item>厂家给不出 K_L 标定时的兜底（精度 ±15%~25%，前提是 v_effective 已校准）</item>
/// <item>现场临时巡检的快速估算</item>
/// <item>UI 上的"科普展示"页面对照公式</item>
/// </list>
/// <para>
/// 精度档次定义为 <see cref="PreloadAccuracyLevel.Simplified"/>，意指"标定前可用、看趋势"。
/// 生产数据必须走 GbSingleWave 或 GbBiWave。
/// </para>
/// <para>
/// 符号约定（§8.3 全方案统一）：<c>Δt</c> 小写 = TOF 时间差；<c>ΔT</c> 大写 = 温度差。
/// 代码里对应 <c>DeltaTofNs</c> 和 <c>DeltaTempC</c>。
/// </para>
/// </summary>
public sealed class SimplifiedCalculator : IPreloadCalculator
{
    public string ModelName => "Simplified";
    public string Version => "1.0.0";
    public PreloadAccuracyLevel AccuracyLevel => PreloadAccuracyLevel.Simplified;

    public PreloadResult Calculate(PreloadInput input)
    {
        // 1. 温度补偿（按 SDK 除法归一化模型，详见方案 §8.4.5）
        //    normalizedTof = echoTime / (1 + tempCoeff × (temp - refTemp))
        //    deltaTof = normalizedTof - refTime
        var deltaTempC = input.TempC - input.RefTempC;
        var tempFactor = 1m + input.TempCoefficient * deltaTempC;
        if (tempFactor <= 0m)
        {
            // 温度修正因子异常（系数和温差导致分母 ≤ 0），无法计算
            return new PreloadResult(
                PreloadN: null,
                UncertaintyN: null,
                DeltaTofNs: 0m,
                WithinGbRange: false,
                ModelUsed: ModelName,
                CalibrationId: null);
        }

        var normalizedTofNs = input.EchoTimeNs / tempFactor;
        var deltaTofNs = normalizedTofNs - input.RefTimeNs;

        // 2. 简化公式 F = E·S·v·Δt / (2·L₀)
        //    单位统一为 SI：
        //    E (GPa → Pa) × 1e9
        //    S (mm² → m²) × 1e-6
        //    v (m/s)
        //    Δt (ns → s) × 1e-9
        //    L₀ (mm → m) × 1e-3
        //    合并系数 = 1e9 × 1e-6 × 1e-9 / 1e-3 = 1e-3
        var bolt = input.Bolt;

        // 输入有效性防护：E / S / v / L_e 必须为正，否则简化公式无物理意义。
        // 尤其 EffectiveLengthMm == 0 会让下面的 decimal 除法抛 DivideByZeroException，
        // 在 50Hz × N 通道的入库热路径里会直接中断整条链路（与 GbSingleWaveCalculator 对 KL<=0 的防护一致）。
        if (bolt.ElasticModulusGpa <= 0m
            || bolt.CrossSectionMm2 <= 0m
            || bolt.SoundVelocityMs <= 0m
            || bolt.EffectiveLengthMm <= 0m)
        {
            return new PreloadResult(
                PreloadN: null,
                UncertaintyN: null,
                DeltaTofNs: deltaTofNs,
                WithinGbRange: false,
                ModelUsed: ModelName + ":invalid_bolt_spec",
                CalibrationId: null);
        }

        var preloadN = bolt.ElasticModulusGpa
                     * bolt.CrossSectionMm2
                     * bolt.SoundVelocityMs
                     * deltaTofNs
                     / (2m * bolt.EffectiveLengthMm)
                     * 0.001m;  // 综合单位系数（见上推导）

        // 3. 不确定度：简化模型 ±20% 中间值
        //    （详见方案 §8.3 模型 A：±15%~25%，依赖于 v/E/S/L₀ 准度）
        //    注：近零预紧时 deltaTof 可能因温补舍入呈微负，preloadN 也微负；用 Abs 取不确定度量级。
        //    大幅负值（基线漂移/方向错）由下面的 GB 适用范围检查（应力∉[30%,90%]Rp0.2）兜底标记为超范围。
        var uncertaintyN = Math.Abs(preloadN) * 0.20m;

        // 4. 国标适用性检查（详见方案 §4 / §8.3）
        //    应力 σ = F / A_e（A_e 是有效截面积）
        //    适用范围: 30%~90% × Rp0.2
        var withinGb = false;
        if (bolt.CrossSectionMm2 > 0m)
        {
            var stressMpa = preloadN / bolt.CrossSectionMm2;  // N / mm² = MPa
            var lower = bolt.Rp02Mpa * 0.30m;
            var upper = bolt.Rp02Mpa * 0.90m;
            withinGb = stressMpa >= lower && stressMpa <= upper;
        }

        return new PreloadResult(
            PreloadN: preloadN,
            UncertaintyN: uncertaintyN,
            DeltaTofNs: deltaTofNs,
            WithinGbRange: withinGb,
            ModelUsed: ModelName,
            CalibrationId: null);  // 简化模型不依赖标定档案
    }
}
