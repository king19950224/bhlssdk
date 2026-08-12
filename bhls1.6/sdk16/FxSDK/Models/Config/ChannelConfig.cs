namespace FxSDK.Models;

/// <summary>
/// 超声参数
/// </summary>
public class ChannelConfig
{
    public const ushort DefaultAddress = 1;
    /// <summary>
    /// 地址码(该通道所在的设备地址) 默认 1
    /// </summary>
    public ushort Address { get; set; } = DefaultAddress;
    /// <summary>
    /// 终端通道号
    /// 0代表无效通道
    /// </summary>
    public ushort Channel { get; set; } = 1;
    /// <summary>
    /// 探头频率MHz
    /// </summary>
    public decimal ProbeFreq { get; set; } = 2.0M;
    /// <summary>
    /// 窗口时间ns
    /// echoWin = 试件长度(mm) * 2 * 1000000 / 声速(m/s);//ns
    /// </summary>
    public decimal EchoWin { get; set; }
    /// <summary>
    /// 回波窗口宽度us(默认15)
    /// </summary>
    public ushort EchoWidth { get; set; } = 15;
    /// <summary>
    /// 脉冲设置
    /// </summary>
    public PulseConfig PulseConfig { get; set; } = new();
    /// <summary>
    /// 增益(1~47) 默认 10
    /// </summary>
    public decimal Gain { get; set; } = 10;
    /// <summary>
    /// 横波配置参数(横纵波测试时启用)
    /// </summary>
    public TEchoConfig? TEchoConfig { get; set; }
    #region 计算参数
    /// <summary>
    /// 测量模式
    /// 0 自动
    /// 1 位置(棋子平台-动态特征法)
    /// 2 阈值
    /// </summary>
    public ushort MeasMode { get; set; } = 2;//默认阈值法
    /// <summary>
    /// 阈值 0代表未设置,系统将使用上阈值即 峰值*0.6 进行运算 
    /// </summary>
    public short Threshold { get; set; } = 0;
    /// <summary>
    /// 位置窗口
    /// </summary>
    public decimal MeasWin { get; set; } = 0;
    /// <summary>
    /// 0 不使用温度补偿
    /// 1 使用材料系数
    /// 2 使用自定义系数
    /// </summary>
    public ushort TcMode { get; set; } = 1;
    /// <summary>
    /// 所选温度探头
    /// 0 独立探头
    /// 1 一体式探头
    /// </summary>
    public ushort TempProbe { get; set; } = 0;
    /// <summary>
    /// 温度补偿系数
    /// </summary>
    public decimal? TempCoefficient { get; set; }

    /// <summary>
    /// 参考声时(设置了参考声时SDK就会自动计算声时差结果)
    /// </summary>
    public decimal? RefTime { get; set; }
    /// <summary>
    /// 自动法置零时的包络粗定位绝对时间(ns)。
    /// 与 RefTime 配对，用于根据包络位移预测当前上升沿过零点。
    /// </summary>
    public decimal? RefEnvTime { get; set; }
    /// <summary>
    /// 自动法置零时保存的稳定载波周期(ns)。
    /// 测量和监测优先使用该周期限定同相上升沿搜索范围。
    /// </summary>
    public decimal? RefPeriodNs { get; set; }
    /// <summary>
    /// 参考温度
    /// </summary>
    public decimal? RefTemp { get; set; }
    /// <summary>
    /// 是否声时差模式(false代表返回回波声时)
    /// IOT24设备特有参数
    /// </summary>
    public bool IsDeltaToFMode { get; set; } = true;
    /// <summary>
    /// 使用温度通道
    /// </summary>
    public ushort UseTempChannel { get; set; } = 0;
    /// <summary>
    /// 温度 Tau 系数，单位秒；为空时使用全局温度 Tau 策略。
    /// </summary>
    public decimal? TempTauSeconds { get; set; }
    #endregion
    /// <summary>
    /// 纵波参考回波
    /// </summary>
    public EchoWave? LEchoWave { get; set; }
}
/// <summary>
/// 脉冲设置
/// </summary>
public class PulseConfig
{
    /// <summary>
    /// St 和 Expert 脉冲电压 其他电压默认
    /// 50v
    /// 100v
    /// 150v
    /// 200v
    /// 250v
    /// 300v
    /// 350v
    /// 400v
    /// </summary>
    public ushort PulseVoltage { get; set; } = 50;
    /// <summary>
    /// 脉冲宽度
    /// </summary>
    public ushort PulseWidth { get; set; } = 40;
    /// <summary>
    /// 占空比 0~100 (默认50)
    /// </summary>
    public byte Duty { get; set; } = 50;
    /// <summary>
    /// 正向脉冲(0-15) (默认2)
    /// </summary>
    public byte Forward { get; set; } = 2;
    /// <summary>
    /// 反向脉冲(0-15) (默认0)
    /// </summary>
    public byte Reverse { get; set; } = 0;
}
/// <summary>
/// 横波设置
/// </summary>
public class TEchoConfig
{
    /// <summary>
    /// 横波窗口时间
    /// </summary>
    public decimal EchoWin { get; set; }
    /// <summary>
    /// 横波位置时间
    /// </summary>
    public decimal MeasWin { get; set; } = 0;
    /// <summary>
    /// 横波增益倍数
    /// </summary>
    public ushort  GainScale { get; set; }
}
