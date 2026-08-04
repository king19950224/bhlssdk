namespace BoltPreload.Domain;

/// <summary>螺栓物理量的兜底缺省值，全项目唯一一份。</summary>
public static class BoltPhysicsDefaults
{
    /// <summary>
    /// SDK 默认温度系数（1/°C，方案 §8.4.5）。螺栓没登记 temp_coefficient_per_c 时用它做温补。
    /// <para>
    /// 必须全项目同一个值：入库热路径（PostgresMeasurementSink）用它把声时归一到参考温度，
    /// 标定端点用它反算基准声时。两边一旦不一致温补就不可逆 —— 同一颗螺栓标定前后
    /// 算出的预紧力对不上，而且不会报错，只是数悄悄偏了。
    /// </para>
    /// <para>
    /// 收口之前这个常量在 4 处各存一份、3 个名字，还有 1.0269e-4m 与 0.00010269m 两种写法，
    /// 靠"改的时候记得四处都改"来维持不变量。
    /// </para>
    /// </summary>
    public const decimal SdkTempCoefficientPerC = 1.0269e-4m;
}
