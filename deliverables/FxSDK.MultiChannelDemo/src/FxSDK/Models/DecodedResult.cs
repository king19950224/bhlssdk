namespace FxSDK.Models;

/// <summary>
/// 解码返回数据抽象基类
/// </summary>
public abstract class DecodedResult
{
    /// <summary>
    /// 地址码
    /// </summary>
    public ushort Address { get; set; } = 1;
    /// <summary>
    /// 接收时间
    /// </summary>
    public DateTime ReceiveTime { get; set; }
    /// <summary>
    /// 原始字节16进制字符串
    /// </summary>
    public string? HexString { get; set; }
}