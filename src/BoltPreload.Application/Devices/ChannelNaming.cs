namespace BoltPreload.Application.Devices;

/// <summary>
/// 物理口号 → 现场中文命名（用户拍板的口径）。
/// <para>
/// 实验台线束每 10 个物理口为一“通道”束（1..10 → 通道1，11..20 → 通道2，…），
/// 束内从 1 数到 10。第 13 口 → “通道2-3号螺栓”；第 21 口 → “通道3-1号螺栓”。
/// 自动建档与手工建档的缺省螺栓编号都用这一套，告警/报告/识别页显示随之统一。
/// </para>
/// </summary>
public static class ChannelNaming
{
    private const int BankSize = 10;

    /// <summary>“通道”束号：1..10 → 1，11..20 → 2，…</summary>
    public static int BankOf(int channelNo) => (channelNo - 1) / BankSize + 1;

    /// <summary>束内序号：每束从 1 数到 10。</summary>
    public static int SlotOf(int channelNo) => (channelNo - 1) % BankSize + 1;

    /// <summary>缺省螺栓编号，如第 13 口 → “通道2-3号螺栓”。</summary>
    public static string DefaultBoltCode(int channelNo) => $"通道{BankOf(channelNo)}-{SlotOf(channelNo)}号螺栓";
}
