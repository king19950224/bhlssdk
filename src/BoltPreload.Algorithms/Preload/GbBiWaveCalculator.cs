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
/// <para>
/// <b>横波数据源在本机型上不存在（2026-08 查证，先看完再动手）：</b>
/// </para>
/// <list type="number">
/// <item><c>FxCommunication.CreateProtocolHandler</c> 按 model 选处理器，model=3 → <c>MultiChIotProtocolHandler</c>，
/// 它继承 <c>FxProtocolHandler</c> 而非 <c>GeneralProtocolHandler</c>；与串口/TCP、直连/LoRa 都无关。</item>
/// <item>横波参数下发（<c>ChannelConfig.TEchoConfig</c> 的横波窗口与位置）和声时2解码
/// （<c>EchoTime2 = tof2IntData + tof2DecData</c>）在厂家 SDK 里只存在于 <c>GeneralProtocolHandler</c>，
/// 即 model=0 标准版 / model=1 专家版这类单通道仪器。<c>MultiChIotProtocolHandler</c> 全文没有这两样。</item>
/// <item>model=3 的 <c>EchoTime</c> 由主机从波形算出（该文件里 <c>EchoStart + zeroIdx * InterpolationInterval</c>），
/// 设备只传波形。全波模式（<c>WorkMode.FullWaveRead</c>）能把纵波 A 扫拼长，但设备不激励也不选通横波窗口，得不到 t_T。</item>
/// <item>实测 98,113 个真机波形，采集窗口一律 600~640 点 × 25 ns，最远到 45,000 ns；纵波基线约 32,142 ns，
/// 横波按碳钢声速比应落在约 58,600 ns，不在任何一个窗口内。</item>
/// </list>
/// <para>
/// 结论：启用本计算器的前置条件不是写算法，而是厂家在 model=3 上补横波支持（plan.md §9.16 协议接管）
/// 外加现场换纵横波复合探头。在那之前 <see cref="IsAvailable"/> 必须保持 false，
/// 对外精度口径按单波法 ±5%（见 <c>DisplayLabels.PrecisionSpec</c>）。
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
