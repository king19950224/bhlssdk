using FxSDK.Core;

namespace FxSDK.Models;

public class DeviceConfig
{
    #region 读取或者上报参数key定义
    /// <summary>
    /// 电池电量值参数
    /// </summary>
    public static readonly string BatVoltage = nameof(BatVoltage);
    /// <summary>
    /// 挂起指令参数
    /// </summary>
    public static readonly string PendingCmdCount = nameof(PendingCmdCount);
    #endregion
    /// <summary>
    /// 设备型号
    /// 0 标准版
    /// 1 专家版
    /// 2 实时24通道版
    /// 3 或默认 多通道监测版(LoraMesh)
    /// </summary>
    public ushort Model { get; set; } = 3;//默认 多通道监测版
    /// <summary>
    /// 通讯方式(默认TCPClient)
    /// 0 串口
    /// 1 TCPClient
    /// 2 TCPServer(2.0 起请使用 FxSDK.Server.FxServer)
    /// </summary>
    public byte CommunicationType { get; set; } = 1;
    /// <summary>
    /// SDK 通讯参数
    /// 串口格式 COM3:115200,0,8,1
    /// 网络格式(本机作为TcpClient) 远端Ip:Port (示例 192.168.0.7:23)
    /// TCP Server 模式请使用 FxSDK.Server.FxServerListenerOptions 配置监听端口。
    /// </summary>
    public string CommunicationParameters { get; set; } = string.Empty;
    /// <summary>
    /// 连接超时时间(s)
    /// 默认 20s
    /// </summary>
    public double ConnTimeOut { get; set; } = 20d;
    /// <summary>
    /// TCP 会话运行时选项。为空时使用 SDK 默认心跳和接收超时行为。
    /// </summary>
    public TcpSessionOptions? TcpSessionOptions { get; set; }
    /// <summary>
    /// 波特率参数
    /// </summary>
    public uint BaudRate { get; set; } = 115200;
    /// <summary>
    /// 传输模式
    /// 0 Lora Mesh
    /// 1 直连
    /// </summary>
    public ushort TransferMode { get; set; } = 0;
    /// <summary>
    /// 超声信号通道个数(单终端)
    /// </summary>
    public ushort UsChannelCount { get; set; } = 1;
    /// <summary>
    /// 模拟量采集通道个数
    /// </summary>
    public ushort AnalogChannelCount { get; set; } = 1;
    /// <summary>
    /// 角度采集通道个数
    /// </summary>
    public ushort AngleChannelCount { get; set; } = 1;
    /// <summary>
    /// 依据通讯方式获取有意义的会话Id
    /// </summary>
    public string GetSessionId()
    {
        switch (CommunicationType)
        {
            case 0: // 串口
            {
                return SessionContext.GetSerialSessionId(CommunicationParameters);
            }
            case 1: // TCP Client
            case 2: // TCP Server
            {
                return SessionContext.GetTcpSessionId(CommunicationParameters);
            }
            default:
                throw new ArgumentException("Unsupported communication type.", nameof(CommunicationType));
        }
    }
    #region 公开配置属性
    /// <summary>
    /// 终端地址码
    /// </summary>
    public ushort Address { get; set; } = 1;//默认 1
    /// <summary>
    /// 终端新地址码
    /// </summary>
    public ushort NAddress { get; set; } = 1;
    /// <summary>
    /// 多通道配置
    /// </summary>
    public MCRConfig? MCRConfig { get; set; }
    /// <summary>
    /// 测量配置
    /// </summary>
    public MeasConfig? MeasConfig { get; set; }
    /// <summary>
    /// Lora配置
    /// </summary>
    public LoraConfig? LoraConfig { get; set; }
    /// <summary>
    /// 网络参数检查配置
    /// </summary>
    public NetCheckConfig? NetCheckConfig { get; set; }
    #endregion
    /// <summary>
    /// 是否自主运行模式
    /// </summary>
    public bool AutoMode { get; set; } = false;
    /// <summary>
    /// 采集间隔(s) 默认10Hz
    /// </summary>
    public decimal SampleInterval
    {
        get
        {
            var interval = 0.01M;//默认 0.01s
            if (MeasConfig is {Frequency: > 0})
            {
                interval = 1M / MeasConfig.Frequency;
            }
            return interval;
        }
    } 
    /// <summary>
    /// 写入flash
    /// </summary>
    public bool WriteFlash { get; set; }
    /// <summary>
    /// 从flash中读出
    /// </summary>
    public bool ReadFlash { get; set; }
    /// <summary>
    /// 最大终端
    /// </summary>
    public ushort MaxTerminal { get; set; } = 1;//默认 1
    /// <summary>
    /// 显式终端地址集合。为空时保持按 1..MaxTerminal 的兼容行为。
    /// </summary>
    public ushort[] TerminalAddresses { get; set; } = Array.Empty<ushort>();
    /// <summary>
    /// 通过地址和通道号获取逻辑通通号
    /// </summary>
    public ushort GetLogicChannel(ushort address, ushort channel)
    {
        if (address < 1)
        {
            address = 1;
        }
        return Convert.ToUInt16((address - 1) * UsChannelCount + channel);
    }
    /// <summary>
    /// 通过逻辑通通号获取终端地址和通道号
    /// </summary>
    public ushort[] GetTerminalChannel(uint logicChannel)
    {
        if (logicChannel == 0)
        {
            logicChannel = 1;
        }
        var sciChannel = Convert.ToUInt16(logicChannel - 1);//使用默认规则
        var phyChannel = Convert.ToUInt16(sciChannel % UsChannelCount + 1);//通道号(从1开始的)
        var phyAddress = Convert.ToUInt16(sciChannel / UsChannelCount + 1);
        if (UsChannelCount == 1) phyAddress = 1;
        return new[] { phyAddress, phyChannel };
    }
    /// <summary>
    /// 获取测量频率
    /// </summary>
    public decimal GetMeasFreq()
    {
        var measFreq = 10M;
        if (MeasConfig != null)
        {
            measFreq = MeasConfig.Frequency;
        }
        return measFreq;
    }
}
/// <summary>
/// 多通道轮询/实时配置
/// </summary>
public class MCRConfig
{
    /// <summary>
    /// 轮询启用的通道
    /// </summary>
    public List<ushort>? EnabledChannels { get; set; }
    /// <summary>
    /// 轮询开始一天的秒数(连续轮询时该参数无效)
    /// 比如 8点开始 则这个设置为 8*3600
    /// </summary>
    public uint StartSeconds { get; set; }
    /// <summary>
    /// 每天轮询次数(0代表连续轮询,最大为 1440次也就是 1 分钟一轮)
    /// </summary>
    public ushort DailyCount { get; set; } = 0;
    /// <summary>
    /// 单轮单通道采集次数(快速轮询模式配置成1)
    /// </summary>
    public ushort SingleCount { get; set; } = 1;
}
/// <summary>
/// 测量配置
/// </summary>
public class MeasConfig
{
    /// <summary>
    /// 测量频率 默认10Hz
    /// </summary>
    public decimal Frequency { get; set; } = 10;
    /// <summary>
    /// 测量对时（自2000年以来的天数）
    /// </summary>
    public ushort DaysSince2000 { get; set; }
    /// <summary>
    /// 测量对时（当天的总秒数）
    /// </summary>
    public uint SecondsToday { get; set; }
    /// <summary>
    /// 测量采样深度
    /// </summary>
    public ushort SampleDepth { get; set; } = 4;
}
/// <summary>
/// Lora配置
/// </summary>
public class LoraConfig
{
    /// <summary>
    /// lora集总地址
    /// </summary>
    public ushort LoraToAddress { get; set; } = 0x8000;
    /// <summary>
    /// lora集总信道
    /// </summary>
    public ushort LoraToChannel { get; set; } = 0x50;
}
/// <summary>
/// 网络检查配置
/// </summary>
public class NetCheckConfig
{
    /// <summary>
    /// 启用网络检查
    /// </summary>
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// 检查周期(s)
    /// </summary>
    public uint CheckPeriod { get; set; } = 600;
}
/// <summary>
/// 工作模式
/// </summary>
public enum WorkMode
{
    Standby,//待机
    SingleMeas,//单通道测量模式
    MultiMeas,//多通道测量模式
    DualMeas,//双波测量模式
    ReadParam,//读取参数模式
    FullWaveRead, //读全波模式
    Reboot,//重启
    LWaveRead,//纵波扫波模式
    TWaveRead //横波扫波模式
}
