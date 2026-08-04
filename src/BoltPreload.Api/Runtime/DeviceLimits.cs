namespace BoltPreload.Api.Runtime;

/// <summary>
/// 设备通道数的两个口径，全项目唯一一份。
/// <para>
/// 收口之前三个文件各写各的：点位表按 64 校验并自称"单板通道上限"，运行期一律钳到 48，
/// 诊断端点摘要写"1-40"。后果是点位表能导入 CH49-64、也真的建了螺栓档案，
/// 但运行期钳到 48 之后这些通道永远不会被采样，现场从界面上看不出差别。
/// </para>
/// </summary>
internal static class DeviceLimits
{
    /// <summary>
    /// 单板通道数硬件上限（方案 §BOARD_CHANNEL_MAX，见 资料/工程文档/plan.md 与
    /// development-guide.md 的 model=3 多通道监测协议：单板最多 48 通道）。
    /// 超出的通道号没有对应硬件，下发读取只会拿到超时，因此入口就要拦住，
    /// 别让台账里出现永远采不到数的通道。
    /// </summary>
    public const int MaxChannelsPerDevice = 48;

    /// <summary>
    /// 设备没登记 channel_count 时的缺省通道数，与 devices.channel_count 的建表缺省一致
    /// （见 deploy/sql/0012_multichannel_model.sql：现场默认 40）。
    /// <para>
    /// 这是"现场典型配置"，不是硬件上限，别和 <see cref="MaxChannelsPerDevice"/> 混用：
    /// 缺省值改大改小都只影响新设备的通道目录，而上限是物理事实。
    /// </para>
    /// </summary>
    public const int DefaultChannelCount = 40;
}
