namespace BoltPreload.Algorithms.Preload;

/// <summary>
/// GB/T 43232-2023 单波法（详见方案 §8.3）。
/// <para>
/// K_L 量纲约定（已裁定，见 docs/sdk-api-facts.md §12）：全系统统一
/// <c>K_L = Δt/F</c>（单位 ns/N，量级 ~1e-4），因此 <c>F = Δt / K_L</c>（见 <see cref="Calculate"/>，
/// 与 <c>torque-calibrate</c>/<c>quick-calibrate</c> 的 <c>kL = Δt/F</c> 自洽，整条标定→计算链闭合）。
/// 注意标定证书与 plan 早期章节（§8.3/§8.5/附录 C）用的是 <c>K_L = Δt/(F·t₀)</c>
/// （单位 1/N，量级 ~1e-11），与本约定相差因子 t₀(~3e4 ns)。
/// <b>quick-calibrate 入参必须按 ns/N 约定（量级 ~1e-4）填写，切勿直接照证书的 1e-11 填</b>
/// （CalibrationEndpoints 的量级护栏会拦截）。
/// </para>
/// <para>精度约 ±5%（K_L 标定可靠时）。</para>
/// </summary>
public sealed class GbSingleWaveCalculator : IPreloadCalculator
{
    public string ModelName => "GbSingleWave";
    public string Version => "1.0.0";
    public PreloadAccuracyLevel AccuracyLevel => PreloadAccuracyLevel.GbSingleWave;

    public PreloadResult Calculate(PreloadInput input)
    {
        if (input.Calibration is null)
        {
            return new PreloadResult(
                PreloadN: null,
                UncertaintyN: null,
                DeltaTofNs: 0m,
                WithinGbRange: false,
                ModelUsed: ModelName,
                CalibrationId: null);
        }

        // 1. 温度补偿（与 SimplifiedCalculator 一致，按 SDK 除法归一化模型）
        //    normalizedTof = echoTime / (1 + tempCoeff × (T - T_ref))
        //    deltaTof = normalizedTof - refTime
        var deltaTempC = input.TempC - input.RefTempC;
        var tempFactor = 1m + input.TempCoefficient * deltaTempC;
        if (tempFactor <= 0m)
        {
            return new PreloadResult(null, null, 0m, false, ModelName, input.Calibration.CalibrationId);
        }
        var normalizedTofNs = input.EchoTimeNs / tempFactor;
        var deltaTofNs = normalizedTofNs - input.RefTimeNs;

        // 2. 单波法预紧力：F = Δt / K_L
        //    全系统裁定约定 K_L = Δt/F（单位 ns/N，见 docs/sdk-api-facts.md §12），
        //    与标定链（torque/quick calibrate 存的 kL=Δt/F）及 channels.k_l /
        //    calibration_records.k_l 的存量数据自洽。勿翻转为 1/N 约定——那会让全部已存 k_l 失真。
        if (input.Calibration.KL <= 0m)
        {
            return new PreloadResult(null, null, deltaTofNs, false, ModelName, input.Calibration.CalibrationId);
        }
        var preloadN = deltaTofNs / input.Calibration.KL;

        // 物理上预紧力是单方向（拉伸 = 正值）。出现负值意味着：
        // 1) 螺栓比安装时还短（极少见，温度补偿过头或基线漂移）；
        // 2) 标定方向反 / K_L 符号错；
        // 3) SDK 读到的 EchoTime 异常。
        // 历史版本用 Math.Abs(preloadN) 抹平 → 把 bug 藏起来。改成显式返回 null + WithinGbRange=false，
        // 让上层(sink)能记录"算法异常"而不是把错误数据当真。
        if (preloadN < 0m)
        {
            return new PreloadResult(
                PreloadN: null,
                UncertaintyN: null,
                DeltaTofNs: deltaTofNs,
                WithinGbRange: false,
                ModelUsed: ModelName + ":negative_preload",
                CalibrationId: input.Calibration.CalibrationId);
        }

        // 3. 不确定度（合成）：u(F) / F = sqrt( (u(Δt)/Δt)² + (u(K_L)/K_L)² )
        //    简化处理：Δt 标准不确定度按 1 ns（典型噪声）；K_L 用 calibration_records 里的 U(k=2)
        decimal? uncertaintyN = null;
        if (Math.Abs(deltaTofNs) > 1m)
        {
            var relU_dt = 1m / Math.Abs(deltaTofNs);  // 1 ns 标准不确定度
            var relU_kl = input.Calibration.KLUncertainty > 0
                ? input.Calibration.KLUncertainty / input.Calibration.KL / 2m  // U(k=2) → u(k=1)
                : 0m;
            var combinedRel = (decimal)Math.Sqrt((double)(relU_dt * relU_dt + relU_kl * relU_kl));
            uncertaintyN = preloadN * combinedRel * 2m;  // 扩展不确定度 k=2，preloadN 已保证 >= 0
        }

        // 4. 国标适用性检查（30%~90% Rp0.2）
        var withinGb = false;
        if (input.Bolt.CrossSectionMm2 > 0m)
        {
            var stressMpa = preloadN / input.Bolt.CrossSectionMm2;
            withinGb = stressMpa >= input.Bolt.Rp02Mpa * 0.30m
                    && stressMpa <= input.Bolt.Rp02Mpa * 0.90m;
        }

        return new PreloadResult(
            PreloadN: preloadN,
            UncertaintyN: uncertaintyN,
            DeltaTofNs: deltaTofNs,
            WithinGbRange: withinGb,
            ModelUsed: ModelName,
            CalibrationId: input.Calibration.CalibrationId);
    }
}
