using BoltPreload.Domain;

namespace BoltPreload.Fx.Simulator;

/// <summary>
/// 模拟器的"物理基线"：把每个通道当成一根真实螺栓，给定其零载声时 RefTof、声时-载荷灵敏度 K_L、
/// 参考温度、温度系数,以及当前真实预紧力（含缓慢松弛）。模拟器据此生成 EchoTime：
/// <code>echo = (RefTof + K_L × preload) × (1 + tempCoeff·(T−RefTemp)) + 噪声</code>
/// 采集链路用<b>匹配的真实标定</b>(K_L/RefTime/RefTemp)把 preload 原样算回来——
/// 因此 UI 上的预紧力是算法真算出来的,而不是人为注入的演示值。
/// <para>模拟器(BoltPreload.Fx)与标定供给器(SimulatedCalibrationProvisioner)共用本类,保证两端口径一致。</para>
/// </summary>
public sealed record BoltPhysics(
    decimal RefTofNs,
    decimal Kl,
    decimal RefTempC,
    decimal TempCoeff,
    decimal InitialPreloadN,
    decimal RelaxNPerHour);

/// <summary>模拟螺栓的默认物理参数集与 <see cref="BoltPhysics"/> 构造入口。</summary>
public static class SimulatedBoltPhysics
{
    /// <summary>SDK 默认温度系数（1/°C）。权威定义见 <see cref="BoltPhysicsDefaults.SdkTempCoefficientPerC"/>。</summary>
    public const decimal DefaultTempCoeff = BoltPhysicsDefaults.SdkTempCoefficientPerC;

    /// <summary>声时-载荷灵敏度 K_L = Δt/F（ns/N）。GB 单波法：F = Δt / K_L。</summary>
    public const decimal Kl = 0.003m;

    /// <summary>零载参考声时（ns）。</summary>
    public const decimal RefTofNs = 26000m;

    /// <summary>参考温度（°C）。</summary>
    public const decimal RefTempC = 22.0m;

    /// <summary>K_L 扩展不确定度（k=2），≈4%。</summary>
    public const decimal KlUncertainty = 0.00012m;

    // 每个通道对应一根螺栓的"真实预紧力剧本"（贴近各螺栓目标值,少数缓慢松弛以体现真实趋势/到限）。
    // 这是模拟器的物理真值,等价于真机现场螺栓的实际预紧力——不是测量值的人为伪造。
    // 通道号 → (初始预紧力 N, 松弛速率 N/小时)
    private static readonly Dictionary<int, (decimal Init, decimal Relax)> Script = new()
    {
        { 2, (96000m, 900m) },    // M16-002 目标100k：缓降,数日后触发预警/到限
        { 3, (92000m, 0m) },      // M16-003 稳定正常
        { 4, (118000m, 0m) },     // M20-004 稳定（≈目标98%）
        { 5, (122000m, 0m) },     // M20-005 略高于目标
        { 6, (110000m, 600m) },   // M20-006 缓慢松弛
        { 8, (116000m, 0m) },     // M20-001 稳定
        { 10, (99000m, 0m) },     // M16-010 稳定
        { 11, (78000m, 0m) },     // M16-011 低于下限→松动
        { 12, (101000m, 0m) },    // M16-012 稳定
    };

    public static IReadOnlyCollection<int> BoltedChannels => Script.Keys;

    public static BoltPhysics? For(int channelNo) =>
        Script.TryGetValue(channelNo, out var s)
            ? new BoltPhysics(RefTofNs, Kl, RefTempC, DefaultTempCoeff, s.Init, s.Relax)
            : null;

    /// <summary>给定通道与已运行时长,返回当前"真实预紧力"（含松弛,带下限保护）。</summary>
    public static decimal CurrentPreloadN(BoltPhysics phys, double elapsedHours)
    {
        var relaxed = phys.InitialPreloadN - phys.RelaxNPerHour * (decimal)elapsedHours;
        var floor = phys.InitialPreloadN * 0.5m;
        return Math.Max(floor, relaxed);
    }
}
