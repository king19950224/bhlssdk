namespace FxSDK.Models;

/// <summary>
/// 设备参数
/// 比如 终端地址、 电池电量、设备配置、通道参数等
/// </summary>
public class DeviceParameter : DecodedResult
{
    public DeviceParameter(string paramName, object paramValue)
    {
        ParamName = paramName;
        ParamValue = paramValue;
    }
    /// <summary>
    /// 参数所在通道
    /// </summary>
    public ushort? Channel { get; set; }
    /// <summary>
    /// 参数名称
    /// </summary>
    public string  ParamName { get; set; }
    /// <summary>
    /// 参数读取值
    /// </summary>
    public object ParamValue { get; set; }
}