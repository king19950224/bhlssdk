using FxSDK.Core;
using FxSDK.Models;
using FxSDK.Utility;
using NLog;

namespace FxSDK.Codec;

/// <summary>
/// 超声V3协议设备编解码器
/// </summary>
public class GeneralProtocolHandler : FxProtocolHandler
{
    private readonly Pt1000Decoder _pt1000Decoder = new();
    private static readonly ILogger Logger = LogManager.GetLogger(nameof(GeneralProtocolHandler));

    /// <summary>
    /// 写入属性数据帧Size
    /// </summary>
    private static readonly int WriteFrameSize = 5;

    /// <summary>
    /// 控制数据帧头
    /// </summary>
    private static readonly byte CtrFrameHead = 0xEF;

    /// <summary>
    /// 模拟数据帧头
    /// </summary>
    private static readonly byte AFrameHead = 0xEE;

    public const decimal OriginalInterval = 10M; //原始波形时钟
    
    public const short NoiseBase = 10; //噪声带
    public const short SignalMax = 110;//最大
    public const short SignalAuto = 90;//自动增益幅度
    public static readonly double[] GainRange = {0, 80};
    public static readonly decimal GainStep = 0.1M;//增益步长
    /// <summary>
    /// 工作模式转换时需要延迟
    /// </summary>
    public const int WorkModeDelay = 100;
    private const double DiscardMs = 500;//丢弃时间ms
    private const int MeasRange = 20000;//测量范围±20us(39,970)

    private DateTime _setWorkModeTime = DateTime.Now;//模式转换时间

    /// <summary>
    /// 纵波波形数据帧头
    /// </summary>
    private static readonly byte[] DecLWaveFrameHead = {0x70, 0x70, 0x70, 0x70};

    /// <summary>
    /// 横波波形数据帧头
    /// </summary>
    private static readonly byte[] DecTWaveFrameHead = {0x71, 0x71, 0x71, 0x71};

    /// <summary>
    /// 纵波全波数据帧头
    /// </summary>
    private static readonly byte[] DecFullWaveFrameHead = {0x72, 0x72, 0x72, 0x72};

    /// <summary>
    /// result frame head
    /// </summary>
    private static readonly byte[] DecResultFrameHead = {0xDE, 0xDE, 0xDE, 0xDE};

    /// <summary>
    /// frame tail
    /// </summary>
    private static readonly byte[] DecFrameTail = {0x00, 0x00, 0x00, 0x00};

    /// <summary>
    /// DA Range
    /// </summary>
    private const ushort DacRange = 10;

    /// <summary>
    /// 编码写控制参数命令
    /// </summary>
    /// <param name="commandCode">功能码</param>
    /// <param name="commandParams">功能参数</param>
    protected byte[] EncodeWriteParamCmd(byte commandCode, byte[] commandParams)
    {
        var writeCmdBytes = new byte[WriteFrameSize];
        writeCmdBytes[0] = CtrFrameHead; //帧头
        writeCmdBytes[1] = commandCode; //功能编码
        Array.Copy(commandParams, 0, writeCmdBytes, 2, commandParams.Length);
        return writeCmdBytes;
    }

    /// <summary>
    /// 编码写模拟参数命令
    /// </summary>
    /// <param name="commandCode">功能码</param>
    /// <param name="commandParams">功能参数</param>
    private byte[] EncodeWriteAnalogParamCmd(byte commandCode, byte[] commandParams)
    {
        var writeCmdBytes = new byte[WriteFrameSize];
        writeCmdBytes[0] = AFrameHead; //帧头
        writeCmdBytes[1] = commandCode; //功能编码
        Array.Copy(commandParams, 0, writeCmdBytes, 2, commandParams.Length);
        return writeCmdBytes;
    }

    /// <summary>
    /// 编码设备参数(如 测量频率)
    /// </summary>
    public override IReadOnlyList<object> EncodeDevice(IoSession session, DeviceConfig config, string? propertyName = null)
    {
        var encodeMessages = new List<object>();
        //编码测量频率
        var measFreqBytes = new List<byte>();
        if (config.MeasConfig != null)
        {
            var measFreq = Convert.ToUInt16(config.MeasConfig.Frequency);
            var sBytes = GetFreqBytes(measFreq);
            //编码测量频率
            var measFreqParams = new byte[3];
            measFreqParams[0] = sBytes[1];
            measFreqBytes.AddRange(EncodeWriteParamCmd(0x08, measFreqParams));
            //编码上传频率
            var upFreqParams = new byte[3];
            upFreqParams[0] = sBytes[0];
            measFreqBytes.AddRange(EncodeWriteParamCmd(0x10, upFreqParams));
        }
        //Expert编码控制模式(主动和被动模式)
        var controlModeBytes = new List<byte>();
        var controlModeParams = new byte[3];
        controlModeParams[0] = (byte) (config.AutoMode ? 2 : 1);
        controlModeBytes.AddRange(EncodeWriteParamCmd(0x12, controlModeParams));
        if (!string.IsNullOrEmpty(propertyName))
        {
            if (propertyName.Equals(nameof(DeviceConfig.MeasConfig))) //配置测量参数
            {
                encodeMessages.Add(measFreqBytes.ToArray());
            }
            else if (propertyName.Equals(nameof(DeviceConfig.AutoMode)))//控制模式
            {
                encodeMessages.Add(controlModeBytes.ToArray());
            }
        }
        else
        {
            encodeMessages.Add(measFreqBytes.ToArray());
        }
        return encodeMessages;
    }

    /// <summary>
    /// 获得上传及测频率码
    /// </summary>
    private byte[] GetFreqBytes(uint freq)
    {
        byte[] sBytes = {0x01, 0x00}; //上传及测量频率分别为1Hz和100Hz
        switch (freq)
        {
            case 1:
                sBytes[0] = 0x00;
                sBytes[1] = 0x00; //1,100Hz
                break;
            case 10:
                sBytes[0] = 0x01;
                sBytes[1] = 0x00; //10,100Hz
                break;
            case 100:
                sBytes[0] = 0x02;
                sBytes[1] = 0x00; //100,100Hz
                break;
            case 200:
                sBytes[0] = 0x03;
                sBytes[1] = 0x01; //1000,200Hz
                break;
            case 1000:
                sBytes[0] = 0x03;
                sBytes[1] = 0x04; //1000,1000Hz
                break;
            case 2000:
                sBytes[0] = 0x04;
                sBytes[1] = 0x05; //2000,2000Hz
                break;
            case 4000:
                sBytes[0] = 0x05;
                sBytes[1] = 0x06; //4000,4000Hz
                break;
            case 8000:
                sBytes[0] = 0x06;
                sBytes[1] = 0x07; //8000,8000Hz
                break;
        }

        return sBytes;
    }

    /// <summary>
    /// 获得脉冲电压索引
    /// </summary>
    private static byte GetPulseVolByte(uint pv)
    {
        if (pv >= 50)
        {
            return (byte) (pv / 50 - 1);
        }
        else //后续
        {
            return 0x08;
        }
    }

    /// <summary>
    /// 获取接收频率索引
    /// </summary>
    private byte GetProbeByte(decimal probeFreq)
    {
        byte rfByte = 0x00;
        if (probeFreq == 2M || probeFreq == 13M || probeFreq == 7.5M)
        {
            rfByte = 0x01;
        }
        else if (probeFreq == 5M || probeFreq == 23M)
        {
            rfByte = 0x02;
        }
        else if (probeFreq == 10M || probeFreq == 6M)
        {
            rfByte = 0x03;
        }

        return rfByte;
    }

    private decimal GetMeasWinDelay(ChannelConfig config, bool tWin = false)
    {
        var delay = config.ProbeFreq switch
        {
            2 => 200, //200
            5 => tWin ? 100 : 60, //60
            10 => 60, //60
            _ => 0, //default、13、23 延迟0
        };
        return delay;
    }

    /// <summary>
    /// 编码通道信息
    /// </summary>
    public override IReadOnlyList<object> EncodeChannel(IoSession session, ChannelConfig config, string? propertyName = null)
    {
        var encodeMessages = new List<object>();
        //编码通道信息(指定配置的通道)-必要指令
        var channelParams = new[] {Convert.ToByte(config.Channel)};
        encodeMessages.Add(EncodeWriteParamCmd(0x01, channelParams));
        //编码自发自收模式
        var trModeParams = new byte[3];
        trModeParams[0] = 0x00;
        var trModeBytes = EncodeWriteAnalogParamCmd(0x01, trModeParams);
        //编码脉冲电压信息
        var pulseVolParams = new byte[3];
        pulseVolParams[0] = GetPulseVolByte(config.PulseConfig.PulseVoltage);
        var pulseVolBytes = EncodeWriteAnalogParamCmd(0x02, pulseVolParams);
        //编码脉冲宽度信息
        var pulseWidthParams = new byte[3];
        var pulseWidthDiv = Convert.ToUInt16(config.PulseConfig.PulseWidth / 10);
        var pulseWidthDivBytes = BitConverter.GetBytes(pulseWidthDiv);
        pulseWidthDivBytes.CopyTo(pulseWidthParams, 0);
        var pulseWidthBytes = EncodeWriteAnalogParamCmd(0x03, pulseWidthParams);
        //编码接收增益信息
        var gainParams = new byte[3];
        var gainDivBytes = BitConverter.GetBytes(Convert.ToUInt16(config.Gain * 10));
        Array.Copy(gainDivBytes, 0, gainParams, 0, 2);
        var gainBytes = EncodeWriteAnalogParamCmd(0x07, gainParams);
        //编码带宽信息
        var wbParams = new byte[3];
        wbParams[0] = 0x01;
        var wbBytes = EncodeWriteAnalogParamCmd(0x08, wbParams);
        //编码模拟参数配置完成
        var analogCompleteParams = new byte[3];
        analogCompleteParams[0] = 0x01;
        var analogCompleteBytes = EncodeWriteParamCmd(0x0E, wbParams);
        //编码回波时间窗信息
        var echoWinParams = new byte[3];
        var echoWinDiv = Convert.ToUInt32(config.EchoWin / OriginalInterval);
        var echoWinDivBytes = BitConverter.GetBytes(echoWinDiv);
        Array.Reverse(echoWinDivBytes);
        Array.Copy(echoWinDivBytes, 1, echoWinParams, 0, 3);
        var echoWinBytes = EncodeWriteParamCmd(0x04, echoWinParams);
        //编码纵波测量模式(同时编码阈值和位置信息)
        var measModeParams = new byte[3];
        if (config.MeasMode == 1) //最大2^20
        {
            var winDelay = GetMeasWinDelay(config);
            var measWin = 0M;
            var measMin = config.EchoWin - MeasRange;
            var measMax = config.EchoWin + MeasRange;
            if (config.MeasWin > 0 
                && config.MeasWin > measMin && config.MeasWin < measMax)
            {
                measWin = config.MeasWin + winDelay; //延迟用于时间对齐
            }
            var measWinDiv = Convert.ToUInt32(measWin / OriginalInterval);
            var measureWinBytes = BitConverter.GetBytes(measWinDiv);
            Array.Reverse(measureWinBytes);
            Array.Copy(measureWinBytes, 1, measModeParams, 0, 3);
        }
        else if (config.MeasMode == 2)
        {
            measModeParams[0] = 0x77;
            measModeParams[1] = config.Threshold > 0 ? (byte) config.Threshold : (byte) (config.Threshold + 256);
        }
        var measModeBytes = EncodeWriteParamCmd(0x05, measModeParams);
        #region 横波参数编码
        var echoWin2Params = new byte[3];
        if (config.TEchoConfig is {EchoWin: > 0})
        {
            var echoWin2Div = Convert.ToUInt32(config.TEchoConfig.EchoWin / OriginalInterval);
            var echoWin2DivBytes = BitConverter.GetBytes(echoWin2Div);
            Array.Reverse(echoWin2DivBytes);
            Array.Copy(echoWin2DivBytes, 1, echoWin2Params, 0, 3); 
        }
        var echoWin2Bytes = EncodeWriteParamCmd(0x0C, echoWin2Params);
        //编码横波位置
        var measWin2Params = new byte[3];
        if (config.TEchoConfig is {MeasWin: > 0})
        {
            var win2Delay = GetMeasWinDelay(config, true);
            var measWin2 = config.TEchoConfig.MeasWin + win2Delay; 
            var measWin2Div = Convert.ToUInt32(measWin2 / OriginalInterval);
            var measureWin2Bytes = BitConverter.GetBytes(measWin2Div);
            Array.Reverse(measureWin2Bytes);
            Array.Copy(measureWin2Bytes, 1, measWin2Params, 0, 3);
        }
        var measWin2Bytes = EncodeWriteParamCmd(0x07, measWin2Params);
        //编码横波缩放
        var tScaleParams = new byte[3];
        if (config.TEchoConfig is {GainScale: > 0})
        {
            var tc2N = (byte)Math.Ceiling(Math.Log(config.TEchoConfig.GainScale, 2));
            tScaleParams[0] = tc2N;
        }
        var tScaleBytes = EncodeWriteAnalogParamCmd(0x11, tScaleParams);
        #endregion
        //编码温度补偿
        var tcParams = new byte[3];
        tcParams[0] = (byte) (config.TcMode > 0 ? 0x01 : 0x00);
        if (config.TempProbe == 1)
        {
            tcParams[0] = (byte) (config.TcMode > 0 ? 0x03 : 0x02);
        }
        if (config.TempCoefficient.HasValue)
        {
            var scaledTc = Math.Round(config.TempCoefficient.Value * 10000000, 0);
            var scaledTcBytes = BitConverter.GetBytes(Convert.ToUInt16(scaledTc));
            tcParams[1] = scaledTcBytes[0];
            tcParams[2] = scaledTcBytes[1];
        }
        var tcBytes = EncodeWriteParamCmd(0x18, tcParams);
        //编码探头频率
        var probeFreqParams = new byte[3];
        probeFreqParams[0] = GetProbeByte(config.ProbeFreq);
        var probeFreqBytes = EncodeWriteParamCmd(0x06, probeFreqParams);
        
        //如果有参数名称,则更新指定参数名称的参数
        if (!string.IsNullOrEmpty(propertyName))
        {
            if (propertyName.Equals(nameof(config.PulseConfig)))
            {
                encodeMessages.Add(pulseVolBytes);
                encodeMessages.Add(pulseWidthBytes);
            }
            else if (propertyName.Equals(nameof(config.TEchoConfig)))
            {
                encodeMessages.Add(echoWin2Bytes);
                encodeMessages.Add(measWin2Bytes);
                encodeMessages.Add(tScaleBytes);
            }
            else if (propertyName.Equals(nameof(config.EchoWin)))
            {
                encodeMessages.Add(echoWinBytes);
            }
            else if (propertyName.Equals(nameof(config.ProbeFreq)))
            {
                encodeMessages.Add(probeFreqBytes);
            }
            else if (propertyName.Equals(nameof(config.Gain)))
            {
                encodeMessages.Add(gainBytes);
            }
            else if (propertyName.Equals(nameof(config.MeasMode))
                     || propertyName.Equals(nameof(config.MeasWin)) || propertyName.Equals(nameof(config.Threshold)))
            {
                encodeMessages.Add(measModeBytes);
            }
            else if (propertyName.Equals(nameof(config.TcMode))
                     || propertyName.Equals(nameof(config.TempProbe)) ||
                     propertyName.Equals(nameof(config.TempCoefficient)))
            {
                encodeMessages.Add(tcBytes);
            }
        }
        else
        {
            //全部参数更新
            encodeMessages.Add(trModeBytes);
            encodeMessages.Add(pulseVolBytes);
            encodeMessages.Add(pulseWidthBytes);
            encodeMessages.Add(wbBytes);
            encodeMessages.Add(analogCompleteBytes);
            encodeMessages.Add(FxUtility.Delay10Ms);//加延迟避免WiFi下导致连接中断
            encodeMessages.Add(echoWinBytes);
            encodeMessages.Add(probeFreqBytes);
            encodeMessages.Add(gainBytes);
            encodeMessages.Add(measModeBytes);
            encodeMessages.Add(tcBytes);
            encodeMessages.Add(FxUtility.Delay10Ms);
            if (config.TEchoConfig != null)
            {
                encodeMessages.Add(echoWin2Bytes);
                encodeMessages.Add(measWin2Bytes);
                encodeMessages.Add(tScaleBytes);
                encodeMessages.Add(FxUtility.Delay10Ms);
            }
        }

        //返回编码字节数组
        return encodeMessages;
    }

    public override IReadOnlyList<object> EncodeWorkMode(IoSession session, WorkMode mode, ushort channel = 0, ushort address = 1)
    {
        var encodeMessages = new List<object>();
        const byte commandCode = 0x03;
        var commandParams = new byte[1];
        var channelParams = new[] {Convert.ToByte(channel)};
        switch (mode)
        {
            case WorkMode.LWaveRead: //纵波扫波模式
                //编码通道信息(指定配置的通道)-模式通道
                encodeMessages.Add(EncodeWriteParamCmd(0x01, channelParams));
                //编码模式信息
                commandParams[0] = 0x01; //纵波波形模式
                encodeMessages.Add(EncodeWriteParamCmd(commandCode, commandParams));
                break;
            case WorkMode.SingleMeas: //单通道测量模式
                //编码通道信息(指定配置的通道)-模式通道
                encodeMessages.Add(EncodeWriteParamCmd(0x01, channelParams));
                encodeMessages.Add(FxUtility.Delay10Ms);
                //编码模式信息
                commandParams[0] = 0x00; //单通道结果
                encodeMessages.Add(EncodeWriteParamCmd(commandCode, commandParams));
                break;
            case WorkMode.TWaveRead: //横波扫波模式
                //编码通道信息(指定配置的通道)-模式通道
                encodeMessages.Add(EncodeWriteParamCmd(0x01, channelParams));
                //编码模式信息
                commandParams[0] = 0x03; //横波波形模式
                encodeMessages.Add(EncodeWriteParamCmd(commandCode, commandParams));
                break;
            case WorkMode.DualMeas: //双波结果
                //编码通道信息(指定配置的通道)-模式通道
                encodeMessages.Add(EncodeWriteParamCmd(0x01, channelParams));
                //编码模式信息
                commandParams[0] = 0x04; //双波模式
                encodeMessages.Add(EncodeWriteParamCmd(commandCode, commandParams));
                break;
            case WorkMode.MultiMeas:
                commandParams[0] = 0x05;
                encodeMessages.Add(EncodeWriteParamCmd(commandCode, commandParams));
                break;
            case WorkMode.FullWaveRead:
                //编码通道信息(指定配置的通道)-模式通道
                var channelFullParams = new[] {Convert.ToByte(channel)};
                encodeMessages.Add(EncodeWriteParamCmd(0x01, channelFullParams));
                commandParams[0] = 0x06; //读全波
                encodeMessages.Add(EncodeWriteParamCmd(commandCode, commandParams));
                break;
            case WorkMode.Standby:
                commandParams[0] = 0x02; //待机
                encodeMessages.Add(EncodeWriteParamCmd(commandCode, commandParams));
                ClearContext();//待机时清理一下缓存中的信息
                break;
        }
        
        lock (AcqTimeLock)
        {
            CurrentAcqTime = null;
            _setWorkModeTime = DateTime.Now;
        }
        return encodeMessages;
    }

    public override IReadOnlyList<object> EncodeReadDeviceParameter(IoSession session, string propertyName, ushort address = 1)
    {
        //忽略
        return new[] {FxUtility.Delay10Ms};
    }

    public override IReadOnlyList<object> EncodeReadChannelParameter(IoSession session, string propertyName, ushort channel = 1, ushort address = 1)
    {
        //忽略
        return new[] {FxUtility.Delay10Ms};
    }

    public override IReadOnlyList<object> EncodeOutputConfig(IoSession session, AnalogOutputConfig outputConfig, ushort channel = 1)
    {
        var encodeMessages = new List<object>();
        //编码ao 配置开始指令
        var aoStartParams = new byte[] {0x01};
        encodeMessages.Add(EncodeWriteParamCmd(0x17, aoStartParams));
        var dtDiv = GetDTofDiv(outputConfig.DTofRange);
        if (outputConfig.OutMode == 1)
        {
            //校准输出
            var last14 = (uint) ((double) outputConfig.OutputVoltage * Math.Pow(2, 14) / DacRange);
            if (last14 == (uint) Math.Pow(2, 14))
                last14 -= 1;
            var data = (3 << 14) + last14; //15:14(11)
            var dataBytes = BitConverter.GetBytes(Convert.ToUInt16(data));
            Array.Reverse(dataBytes);
            encodeMessages.Add(EncodeWriteParamCmd(0x0F, dataBytes));
        }
        else
        {
            //取消校准输出-即输出轴力
            encodeMessages.Add(EncodeWriteParamCmd(0x0F, new byte[] {0x00}));
            //编码声时差范围
            var dtDivBytes = BitConverter.GetBytes(dtDiv);
            Array.Reverse(dtDivBytes); //转换为高-低 字节顺序
            encodeMessages.Add(EncodeWriteParamCmd(0x09, dtDivBytes));
            //编码归一化的参考回波的声时
            if (outputConfig.RefEchoTime.HasValue)
            {
                var normalTofDiv = Convert.ToDecimal(outputConfig.RefEchoTime / OriginalInterval);
                var intNormalTofDiv = Convert.ToUInt32(Math.Floor(normalTofDiv)); //获取单位为 10ns 的整数部分
                var decNormTofDiv = Convert.ToUInt16((normalTofDiv - intNormalTofDiv) * 65536); //获取单位为 10ns 的小数部分
                var intNormTofBytes = BitConverter.GetBytes(intNormalTofDiv);
                var decNormTofBytes = BitConverter.GetBytes(decNormTofDiv);
                Array.Reverse(intNormTofBytes);
                Array.Reverse(decNormTofBytes); //转为高-低 字节顺序
                var intNormTof3Bytes = new[] {intNormTofBytes[1], intNormTofBytes[2], intNormTofBytes[3]}; //只保留 3 位
                encodeMessages.Add(EncodeWriteParamCmd(0x0A, intNormTof3Bytes));
                encodeMessages.Add(EncodeWriteParamCmd(0x0B, decNormTofBytes));
            }
            var devConfig = SessionContext.GetDeviceConfig(session);
            //编码力标定表
            if (outputConfig.CalibrationTable is {Count: > 0})
            {
                decimal calValue = 0;
                var rowIndex = 0;
                foreach (var calRow in outputConfig.CalibrationTable)
                {
                    try
                    {
                        if (devConfig?.CommunicationType > 0  && rowIndex % 10 == 0)//非串口通讯加入每输出10条加10ms延迟
                        {
                            encodeMessages.Add(FxUtility.Delay10Ms);
                        }
                        var pre10 = (uint) (calRow[0] * (decimal) Math.Pow(2, 10) / (dtDiv * OriginalInterval));
                        if (calRow[1] >= 0)
                            calValue = calRow[1];
                        var last14 = (uint) (calValue * (decimal) Math.Pow(2, 14) / outputConfig.FullRange);
                        var data = (pre10 << 14) + last14; //left shift 14 bit
                        var calRowBytes = BitConverter.GetBytes(data); //
                        Array.Reverse(calRowBytes);
                        var calRow3Bytes = new[] {calRowBytes[1], calRowBytes[2], calRowBytes[3]};
                        encodeMessages.Add(EncodeWriteParamCmd(0x0D, calRow3Bytes));
                        rowIndex++;
                    }
                    catch (Exception ex)
                    {
                        //Console.WriteLine(ex);
                    }
                }
            }
        }

        //编码ao配置完成指令
        var aoEndParams = new byte[] {0x00};
        encodeMessages.Add(EncodeWriteParamCmd(0x17, aoEndParams));
        return encodeMessages;
    }

    /// <summary>
    /// 依据声时差范围计算声时差分度范围
    /// 除以10   大于该值的最小2的N方值
    /// </summary>
    public static ushort GetDTofDiv(ushort dTofScale)
    {
        var dScale = dTofScale / (ushort) OriginalInterval;
        var c2N = (int) Math.Ceiling(Math.Log(dScale, 2));
        return Convert.ToUInt16(Math.Pow(2, c2N));
    }
    #region 按照协议解码数据
    protected override List<DecodedResult> DoDecode(IoSession session, CancellationToken token)
    {
        var decodedResults = new List<DecodedResult>();
        var deviceMode = SessionContext.GetDeviceMode(session);
        switch (deviceMode)
        {
            case WorkMode.LWaveRead:
                decodedResults.AddRange(DecodeLWaveResults(session,token));
                break;
            case WorkMode.SingleMeas:
                decodedResults.AddRange(DecodeChannelResults(session,token));
                break;
            case WorkMode.MultiMeas:
                decodedResults.AddRange(DecodeMultiChannelResults(session,token));
                break;
            case WorkMode.TWaveRead:
                decodedResults.AddRange(DecodeTWaveResults(session,token));
                break;
            case WorkMode.DualMeas:
                decodedResults.AddRange(DecodeChannelDualResults(session,token));
                break;
            case WorkMode.FullWaveRead:
                decodedResults.AddRange(DecodeFullWaveResults(session,token));
                break;
            case WorkMode.Standby:
                default:
                    ClearContext();//待机或者未知状态清理上下文数据
                break;
        }
        return decodedResults;
    }
    private int _headBeginIdx = -1; //动态解析头索引
    /// <summary>
    /// 解码单纵波波形结果
    /// WiFi实际1012个字节
    /// </summary>
    private List<ChannelResult> DecodeLWaveResults(IoSession session, CancellationToken token)
    {
        var decodedList = new List<ChannelResult>();
        var needValidTail = true;
        var echoFrameSize = 3997;
        var devConfig = SessionContext.GetDeviceConfig(session);
        if (devConfig?.CommunicationType > 0)//非串口通讯使用1000
        {
            echoFrameSize = 1000;
            needValidTail = false;
        }
        var frameSize = DecLWaveFrameHead.Length + echoFrameSize + DecFrameTail.Length; //定长帧长度
        while (!token.IsCancellationRequested && ContextData.Count >= frameSize)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (_headBeginIdx < 0)
                {
                    _headBeginIdx = ByteUtility.IndexOf(ContextData, DecLWaveFrameHead);
                    if (_headBeginIdx < 0)
                    {
                        var removeLength = ContextData.Count - DecLWaveFrameHead.Length;
                        ContextData.RemoveRange(0,removeLength); //保留帧头长度的字节
                        break;
                    }
                }

                //找到帧头,则清理帧头前面的无效字节
                if (_headBeginIdx > 0)
                {
                    ContextData.RemoveRange(0,_headBeginIdx);
                    _headBeginIdx = 0;
                }

                if (ContextData.Count < frameSize)
                {
                    break; //数据不全，等待补充完整
                }

                //数据长度完整
                var decodedFrame = ContextData.GetRange(0, frameSize);
                CurrentAcqTime = DateTime.Now; //波形采集时间采用当前时间
                //需要判断帧尾但帧尾不对  暂时先不判断帧尾
                // if (needValidTail && !IsFrameTailValid(decodedFrame, DecFrameTail))
                // {
                //     ContextData.RemoveAt(0); //删除首个元素，继续往后找
                //     _headBeginIdx = -1;
                // }
                // else
                {
                    var waveData = new List<short>();
                    var waveStartIndex = DecLWaveFrameHead.Length + 3 + 1;
                    var waveEndIndex = frameSize - DecFrameTail.Length;
                    for (var i = waveStartIndex; i < waveEndIndex; i++)
                    {
                        var dataByte = decodedFrame[i];
                        if (dataByte > 127)
                        {
                            waveData.Add((sbyte) (dataByte - 256));
                        }
                        else
                        {
                            waveData.Add(dataByte);
                        }
                    }
                    
                    //波形起始3字节 高-低
                    var echoStartBytes = decodedFrame.GetRange(DecLWaveFrameHead.Length, 3).ToArray();
                    echoStartBytes[0] = (byte) (echoStartBytes[0] & 0x0F); //清除高字节的前 4 位
                    var echoStartFourBytes = new byte[4];
                    Array.Copy(echoStartBytes, 0, echoStartFourBytes, 1, 3);
                    if (BitConverter.IsLittleEndian)
                        Array.Reverse(echoStartFourBytes);
                    
                    //赋值到对象
                    var channelResult = new ChannelResult
                    {
                        HexString = ByteUtility.BytesToHexStr(decodedFrame),
                        EchoWave = new EchoWave
                        {
                            EchoStart = BitConverter.ToUInt32(echoStartFourBytes, 0) * (int)OriginalInterval + 60, //延时60ns时间对齐
                            ClockInt = Convert.ToUInt16(OriginalInterval),
                            WaveData = waveData,
                        },
                        ReceiveTime = CurrentAcqTime.Value,
                    };
                    var channelConfig = SessionContext.GetLastChannelConfig(session);
                    
                    if (channelConfig != null)
                    {
                        channelResult.Channel = channelConfig.Channel;
                        decimal[] midResults;
                        var waveDataDouble = waveData.Select(o => (double) o).ToList();
                        var zeroIdx = 0d;
                        if (channelConfig.MeasMode == 0)//自动波谷法
                        {
                            zeroIdx = CalculateExtensions.CalcZeroIndexByAutoValley(waveDataDouble, out midResults, NoiseBase);
                        }
                        else if (channelConfig.MeasMode == 1)//手动位置法
                        {
                            var posIndex = (channelConfig.MeasWin - channelResult.EchoWave.EchoStart) / OriginalInterval;
                            if (posIndex < 0 || posIndex >= waveDataDouble.Count)
                            {
                                posIndex = 0;
                            }
                            zeroIdx = CalculateExtensions.CalcZeroIndexByPosition(waveDataDouble, (int)posIndex, out midResults, NoiseBase);
                        }
                        else//手动阈值法
                        {
                            zeroIdx = CalculateExtensions.CalcZeroIndexBySimpleThr(waveDataDouble, out midResults, NoiseBase, channelConfig.Threshold);
                        }
                        if (zeroIdx < 0)
                        {
                            channelResult.StatusCode = Convert.ToInt16(zeroIdx);
                        }
                        channelResult.MidResults = midResults;
                    }
                    decodedList.Add(channelResult);
                    ContextData.RemoveRange(0,decodedFrame.Count);
                    _headBeginIdx = -1; //
                }
            }
            catch (Exception)
            {
                ContextData.RemoveAt(0); //删除首个元素，继续往后找
                _headBeginIdx = -1;
                break;
            }
        }
        return decodedList;
    }
    /// <summary>
    /// 解码单纵波波形结果
    /// </summary>
    private List<ChannelResult> DecodeTWaveResults(IoSession session, CancellationToken token)
    {
        var decodedList = new List<ChannelResult>();
        var needValidTail = true;
        var echoFrameSize = 3997;
        var devConfig = SessionContext.GetDeviceConfig(session);
        if (devConfig?.CommunicationType > 0)//非串口通讯使用1000
        {
            echoFrameSize = 1000;
            needValidTail = false;//非串口波形没有帧尾
        }
        var frameSize = DecTWaveFrameHead.Length + echoFrameSize + DecFrameTail.Length; //定长帧长度
        while (!token.IsCancellationRequested && ContextData.Count >= frameSize)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (_headBeginIdx < 0)
                {
                    _headBeginIdx = ByteUtility.IndexOf(ContextData, DecTWaveFrameHead);
                    if (_headBeginIdx < 0)
                    {
                        var removeLength = ContextData.Count - DecTWaveFrameHead.Length;
                        ContextData.RemoveRange(0,removeLength); //保留帧头长度的字节
                        break;
                    }
                }

                //找到帧头,则清理帧头前面的无线字节
                if (_headBeginIdx > 0)
                {
                    ContextData.RemoveRange(0,_headBeginIdx);
                    _headBeginIdx = 0;
                }

                if (ContextData.Count < frameSize)
                {
                    break; //数据不全，等待补充完整
                }

                //数据长度完整
                var decodedFrame = ContextData.GetRange(0, frameSize);
                CurrentAcqTime = DateTime.Now; //波形的采集时间使用当前时间
                //需要判断帧尾但帧尾不对
                // if (needValidTail && !IsFrameTailValid(decodedFrame, DecFrameTail))
                // {
                //     ContextData.RemoveAt(0); //删除首个元素，继续往后找
                //     _headBeginIdx = -1;
                // }
                // else
                {
                    var waveData = new List<short>();
                    var waveStartIndex = DecTWaveFrameHead.Length + 3 + 1;
                    var waveEndIndex = frameSize - DecFrameTail.Length;
                    for (var i = waveStartIndex; i < waveEndIndex; i++)
                    {
                        var dataByte = decodedFrame[i];
                        if (dataByte > 127)
                        {
                            waveData.Add((sbyte) (dataByte - 256));
                        }
                        else
                        {
                            waveData.Add(dataByte);
                        }
                    }
                    
                    //波形起始3字节 高-低
                    var echoStartBytes = decodedFrame.GetRange(DecTWaveFrameHead.Length, 3).ToArray();
                    echoStartBytes[0] = (byte) (echoStartBytes[0] & 0x0F); //清除高字节的前 4 位
                    var echoStartFourBytes = new byte[4];
                    Array.Copy(echoStartBytes, 0, echoStartFourBytes, 1, 3);
                    if (BitConverter.IsLittleEndian)
                        Array.Reverse(echoStartFourBytes);

                    //赋值到对象
                    var channelResult = new ChannelResult
                    {
                        HexString = ByteUtility.BytesToHexStr(decodedFrame),
                        EchoWave = new EchoWave
                        {
                            EchoStart = BitConverter.ToUInt32(echoStartFourBytes, 0) * 10 + 60, //延时60ns时间对齐
                            ClockInt = Convert.ToUInt16(OriginalInterval),
                            WaveData = waveData,
                        },
                        ReceiveTime = CurrentAcqTime.Value,
                    };
                    var waveDataDouble = waveData.Select(o => (double) o).ToList();
                    var channelConfig = SessionContext.GetLastChannelConfig(session);
                    if (channelConfig is {TEchoConfig: not null})
                    {
                        var posIndex = (channelConfig.TEchoConfig.MeasWin - channelResult.EchoWave.EchoStart) / OriginalInterval;
                        if (posIndex < 0) posIndex = 0;
                        var zeroIdx = CalculateExtensions.CalcZeroIndexByPosition(waveDataDouble, (int)posIndex, out var midResults, NoiseBase);
                        if (zeroIdx < 0)
                        {
                            channelResult.StatusCode = Convert.ToInt16(zeroIdx);
                        }
                        channelResult.MidResults = midResults;
                    }
                    decodedList.Add(channelResult);
                    ContextData.RemoveRange(0,decodedFrame.Count);
                    _headBeginIdx = -1; //
                }
            }
            catch (Exception)
            {
                ContextData.RemoveAt(0); //删除首个元素，继续往后找
                _headBeginIdx = -1;
                break;
            }
        }
        return decodedList;
    }
    /// <summary>
    /// 解码单通道测量结果
    /// </summary>
    protected List<ChannelResult> DecodeChannelResults(IoSession session, CancellationToken token)
    {
        var decodedList = new List<ChannelResult>();
        var frameSize = DecResultFrameHead.Length + 27 + DecFrameTail.Length; //定长帧长度
        while (!token.IsCancellationRequested && ContextData.Count >= frameSize)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (_headBeginIdx < 0)
                {
                    _headBeginIdx = ByteUtility.IndexOf(ContextData, DecResultFrameHead);
                    if (_headBeginIdx < 0)
                    {
                        var removeLength = ContextData.Count - DecResultFrameHead.Length;
                        ContextData.RemoveRange(0,removeLength); //保留帧头长度的字节
                        break;
                    }
                }

                //找到帧头,则清理帧头前面的无效字节
                if (_headBeginIdx > 0)
                {
                    ContextData.RemoveRange(0,_headBeginIdx);
                    _headBeginIdx = 0;
                }

                if (ContextData.Count < frameSize)
                {
                    break; //数据不全，等待补充完整
                }

                //数据长度完整
                var decodedFrame = ContextData.GetRange(0,frameSize);
                var devConfig = SessionContext.GetDeviceConfig(session);
                if (IsFrameTailValid(decodedFrame, DecFrameTail))
                {
                    lock (AcqTimeLock)
                    {
                        CurrentAcqTime = CurrentAcqTime?.AddSeconds((double) devConfig!.SampleInterval) ?? DateTime.Now; //初始化采集时间
                        if (CurrentAcqTime - _setWorkModeTime < TimeSpan.FromMilliseconds(DiscardMs))
                        {
                            ContextData.RemoveRange(0,decodedFrame.Count);
                            _headBeginIdx = -1; //重新找帧头
                            continue;
                        }
                    }
                    var dataIndex = DecResultFrameHead.Length;
                    var decodedFrameArray = decodedFrame.ToArray();
                    //声时结果 2,3,4 整数  5,6小数
                    byte[] tofIntBytes = new byte[4], tofDecBytes = new byte[2];
                    Array.Copy(decodedFrameArray, dataIndex + 2, tofIntBytes, 1, 3);
                    Array.Reverse(tofIntBytes); //高-低
                    Array.Copy(decodedFrameArray, dataIndex + 5, tofDecBytes, 0, 2);
                    Array.Reverse(tofDecBytes); //高-低
                    var tofIntData = BitConverter.ToUInt32(tofIntBytes, 0) * OriginalInterval;
                    var tofDecData = BitConverter.ToUInt16(tofDecBytes, 0) * OriginalInterval / 65536M;
                    //独立温度探头解析 温度积分值和温度积分参考值 7-11
                    var tempBytes = new byte[4];
                    var tempRefBytes = new byte[4];
                    Array.Copy(decodedFrameArray, dataIndex + 7, tempBytes, 0, 4);
                    Array.Reverse(tempBytes); //高-低
                    Array.Copy(decodedFrameArray, dataIndex + 11, tempRefBytes, 0, 4);
                    Array.Reverse(tempRefBytes); //高-低
                    var refResIv = BitConverter.ToUInt32(tempRefBytes, 0); //参考电阻积分值
                    var pt1000Iv = BitConverter.ToUInt32(tempBytes, 0); //pt1000探头积分值
                    decimal? indTemp = null;
                    try
                    {
                        indTemp = _pt1000Decoder.GetResult(pt1000Iv, refResIv); //独立探头温度测量值
                    }
                    catch
                    {
                        // 忽略
                    }
                    //模拟量采集  15-22  高-低 Expert (1-2重复 3-4重复) standard (1-3重复 2-4重复) 解析1和4即可 
                    var lowAiBytes = new byte[2];
                    var highAiBytes = new byte[2];
                    Array.Copy(decodedFrameArray, dataIndex + 15, lowAiBytes, 0, 2);
                    Array.Reverse(lowAiBytes);
                    Array.Copy(decodedFrameArray, dataIndex + 21, highAiBytes, 0, 2);
                    Array.Reverse(highAiBytes);
                    //角度采集
                    var angleTempBytes = new byte[4];
                    Array.Copy(decodedFrameArray, dataIndex + 23, angleTempBytes, 0, 4);
                    Array.Reverse(angleTempBytes);
                    //赋值到对象
                    var channelResult = new ChannelResult
                    {
                        HexString = ByteUtility.BytesToHexStr(decodedFrame),
                        EchoTime = tofIntData + tofDecData, //ns
                        SpTemp = indTemp,
                        LowAi = BitConverter.ToUInt16(lowAiBytes, 0),
                        HighAi = BitConverter.ToUInt16(highAiBytes, 0),
                        Angle = BitConverter.ToInt16(angleTempBytes, 0),
                        ReceiveTime = CurrentAcqTime.Value,
                    };
                    var lastChannelConfig = SessionContext.GetLastChannelConfig(session, channelResult.Address);
                    channelResult.Channel = lastChannelConfig?.Channel ?? 1; //默认通道1
                    //保存温度通道到会话
                    if (channelResult.SpTemp.HasValue)
                    {
                        SessionContext.StoreChannelTemp(session, channelResult.Address, channelResult.Channel, channelResult.SpTemp!.Value);
                    }
                    var channelConfig = SessionContext.GetChannelConfig(session, channelResult.Address, channelResult.Channel);
                    if (channelConfig != null)
                    {
                        channelResult.SpTemp = SessionContext.GetChannelTemp(session, channelResult.Address, channelResult.Channel);
                    }

                    if (channelConfig is {RefTime: not null, RefTemp: not null})
                    {
                        var tempCoefficient = channelConfig.TempCoefficient ?? CalculateExtensions.DefaultTempCoeff;
                        channelResult.DeltaTof = CalculateExtensions.CalcDeltaTof(channelConfig.RefTime.Value, channelConfig.RefTemp.Value,
                            (decimal) channelResult.EchoTime, channelResult.SpTemp!.Value, channelConfig.TcMode,tempCoefficient);
                    }
                    decodedList.Add(channelResult);
                    ContextData.RemoveRange(0,decodedFrame.Count);
                    _headBeginIdx = -1; //
                }
                else
                {
                    ContextData.RemoveAt(0); //删除首个元素，继续往后找
                    _headBeginIdx = -1;
                    break;
                }
            }
            catch (Exception)
            {
                ContextData.RemoveAt(0); //删除首个元素，继续往后找
                _headBeginIdx = -1;
                break;
            }
        }
        return decodedList;
    }
    /// <summary>
    /// 解码单通道双波测量结果
    /// </summary>
    private List<ChannelResult> DecodeChannelDualResults(IoSession session, CancellationToken token)
    {
        var decodedList = new List<ChannelResult>();
        var frameSize = DecResultFrameHead.Length + 24 + DecFrameTail.Length; //定长帧长度
        while (!token.IsCancellationRequested && ContextData.Count >= frameSize)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (_headBeginIdx < 0)
                {
                    _headBeginIdx = ByteUtility.IndexOf(ContextData, DecResultFrameHead);
                    if (_headBeginIdx < 0)
                    {
                        var removeLength = ContextData.Count - DecResultFrameHead.Length;
                        ContextData.RemoveRange(0,removeLength); //保留帧头长度的字节
                        break;
                    }
                }
                //找到帧头,则清理帧头前面的无线字节
                if (_headBeginIdx > 0)
                {
                    ContextData.RemoveRange(0,_headBeginIdx);
                    _headBeginIdx = 0;
                }

                if (ContextData.Count < frameSize)
                {
                    break; //数据不全，等待补充完整
                }

                //数据长度完整
                var decodedFrame = ContextData.GetRange(0,frameSize);
                var devConfig = SessionContext.GetDeviceConfig(session);
                if (IsFrameTailValid(decodedFrame, DecFrameTail))
                {
                    lock (AcqTimeLock)
                    {
                        CurrentAcqTime = CurrentAcqTime?.AddSeconds((double) devConfig!.SampleInterval) ?? DateTime.Now;
                        if (CurrentAcqTime - _setWorkModeTime < TimeSpan.FromMilliseconds(DiscardMs))
                        {
                            ContextData.RemoveRange(0, decodedFrame.Count);
                            _headBeginIdx = -1; //重新找帧头
                            continue;
                        }
                    }
                    var dataIndex = DecResultFrameHead.Length;
                    var decodedFrameArray = decodedFrame.ToArray();
                    //声时结果 2,3,4 整数  5,6小数
                    byte[] tofIntBytes = new byte[4], tofDecBytes = new byte[2];
                    Array.Copy(decodedFrameArray, dataIndex + 2, tofIntBytes, 1, 3);
                    Array.Reverse(tofIntBytes); //高-低
                    Array.Copy(decodedFrameArray, dataIndex + 5, tofDecBytes, 0, 2);
                    Array.Reverse(tofDecBytes); //高-低
                    var tofIntData = BitConverter.ToUInt32(tofIntBytes, 0) * OriginalInterval;
                    var tofDecData = BitConverter.ToUInt16(tofDecBytes, 0) * OriginalInterval / 65536M;
                    //独立温度探头解析 温度积分值和温度积分参考值 7-11
                    var tempBytes = new byte[4];
                    var tempRefBytes = new byte[4];
                    Array.Copy(decodedFrameArray, dataIndex + 7, tempBytes, 0, 4);
                    Array.Reverse(tempBytes); //高-低
                    Array.Copy(decodedFrameArray, dataIndex + 11, tempRefBytes, 0, 4);
                    Array.Reverse(tempRefBytes); //高-低
                    var refResIv = BitConverter.ToUInt32(tempRefBytes, 0); //参考电阻积分值
                    var pt1000Iv = BitConverter.ToUInt32(tempBytes, 0); //pt1000探头积分值
                    var indTemp = _pt1000Decoder.GetResult(pt1000Iv, refResIv); //独立探头温度测量值
                    //声时2结果 2,3,4 整数  5,6小数
                    byte[] tof2IntBytes = new byte[4], tof2DecBytes = new byte[2];
                    Array.Copy(decodedFrameArray, dataIndex + 15, tof2IntBytes, 1, 3);
                    Array.Reverse(tof2IntBytes); //高-低
                    Array.Copy(decodedFrameArray, dataIndex + 17, tof2DecBytes, 0, 2);
                    Array.Reverse(tof2DecBytes); //高-低
                    var tof2IntData = BitConverter.ToUInt32(tof2IntBytes, 0) * OriginalInterval;
                    var tof2DecData = BitConverter.ToUInt16(tof2DecBytes, 0) * OriginalInterval / 65536M;
                    //赋值到对象
                    var channelResult = new ChannelResult
                    {
                        HexString = ByteUtility.BytesToHexStr(decodedFrame),
                        EchoTime = tofIntData + tofDecData, //ns
                        EchoTime2 = tof2IntData + tof2DecData,
                        SpTemp = indTemp,
                        ReceiveTime = CurrentAcqTime.Value,
                    };
                    //保存温度通道到会话
                    if (channelResult.SpTemp.HasValue)
                    {
                        SessionContext.StoreChannelTemp(session, channelResult.Address, channelResult.Channel, channelResult.SpTemp.Value);
                    }
                    var channelConfig = SessionContext.GetChannelConfig(session, channelResult.Address, channelResult.Channel);
                    if (channelConfig != null)
                    {
                        channelResult.SpTemp = SessionContext.GetChannelTemp(session, channelResult.Address, channelResult.Channel);
                    }

                    if (channelConfig is {RefTime: not null, RefTemp: not null})
                    {
                        var tempCoefficient = channelConfig.TempCoefficient ?? CalculateExtensions.DefaultTempCoeff;
                        channelResult.DeltaTof = CalculateExtensions.CalcDeltaTof(
                            channelConfig.RefTime.Value, channelConfig.RefTemp.Value,
                            (decimal) channelResult.EchoTime, channelResult.SpTemp!.Value, channelConfig.TcMode,
                            tempCoefficient);
                    }
                    decodedList.Add(channelResult);
                    ContextData.RemoveRange(0,decodedFrame.Count);
                    _headBeginIdx = -1; //
                }
                else
                {
                    ContextData.RemoveAt(0); //删除首个元素，继续往后找
                    _headBeginIdx = -1;
                    break;
                }
            }
            catch (Exception)
            {
                ContextData.RemoveAt(0); //删除首个元素，继续往后找
                _headBeginIdx = -1;
                break;
            }
        }
        return decodedList;
    }
    const int MaxFramesPerBatch = 200;//单次最多循环解码的帧数
    /// <summary>
    /// 解码多通道结果
    /// </summary>
    private List<MultiChannelResult> DecodeMultiChannelResults(IoSession session, CancellationToken token)
    {
        var decodedList = new List<MultiChannelResult>();
        try
        {
            var device = SessionContext.GetDeviceConfig(session);
            if (device != null)
            {
                var mcrConfig = device.MCRConfig;
                //通道起始编号(1) + 状态信息(1) + 通道声时(4)*8 + 温度积分(4) + 温度参考(4) + ADH4(2) + ADH3(2) + ADH2(2) +ADH1(2)
                // + ADL4(2) + ADL3(2) + ADL2(2) + ADL1(2) + 角度4(2) + 角度3(2) + 角度2(2) +角度1(2)
                var frameSize = DecResultFrameHead.Length + 1 + 1 + 4 * 8 + 4 + 4 + 2 * 4 + 2 * 4 + 2 * 4;
                var frameCount = 0;
                while (!token.IsCancellationRequested && ContextData.Count >= frameSize)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (_headBeginIdx < 0)
                        {
                            _headBeginIdx = ByteUtility.IndexOf(ContextData, DecResultFrameHead);
                            if (_headBeginIdx < 0)
                            {
                                var removeRange = ContextData.Count - DecResultFrameHead.Length;
                                ContextData.RemoveRange(0,removeRange); //除帧头长度之前的全清除
                                break;
                            }
                        }

                        //找到帧头，清理帧头以前的数据
                        if (_headBeginIdx > 0)
                        {
                            ContextData.RemoveRange( 0,_headBeginIdx);
                            _headBeginIdx = 0;
                        }

                        if (ContextData.Count < frameSize)
                        {
                            break; //数据不全，等待补充完整
                        }

                        var devConfig = SessionContext.GetDeviceConfig(session);
                        var decodedFrame = ContextData.GetRange(0, frameSize);
                        
                        //乱帧以及帧尾判断
                        if (ByteUtility.IndexOf(decodedFrame, DecResultFrameHead, DecResultFrameHead.Length) < 0)
                            //&& IsFrameTailValid(decodedFrame,DecFrameTail))//帧尾判断
                        {
                            lock (AcqTimeLock)
                            {
                                CurrentAcqTime = CurrentAcqTime?.AddSeconds((double) devConfig!.SampleInterval) ?? DateTime.Now;
                                if (CurrentAcqTime - _setWorkModeTime < TimeSpan.FromMilliseconds(DiscardMs))
                                {
                                    ContextData.RemoveRange(0,frameSize);
                                    _headBeginIdx = -1; //重新找帧头
                                    continue;
                                }
                            }
                            var multiChResult = new MultiChannelResult
                            {
                                HexString = ByteUtility.BytesToHexStr(decodedFrame),
                                ReceiveTime = CurrentAcqTime.Value,
                                Results = new Dictionary<ushort, ChannelResult>(),
                            };
                            var intTempProbe = false; //是否使用一体式集成探头
                            for (var i = 0; i < device.UsChannelCount; i++)
                            {
                                #region 解码声时部分

                                var usStart = i * 4 + 6;
                                var tofBytes = decodedFrame.Skip(usStart).Take(4).ToArray();
                                if (BitConverter.IsLittleEndian) Array.Reverse(tofBytes); //为小端模式转换
                                var tofAll = BitConverter.ToUInt32(tofBytes, 0);
                                var tofIntData = (tofAll >> 12) * OriginalInterval; //右移12位得整数部分
                                var tofDecData = (tofAll & 0xFFF) * OriginalInterval / 4096M; //And运算得12位小数部分

                                #endregion

                                #region 解码 ADH 部分

                                ushort? adhValue = null;
                                if (i < device.AnalogChannelCount)
                                {
                                    var adhStart = 4 + 2 + 32 + 8 + 2 * 3 - i * 2;
                                    var adhBytes = decodedFrame.Skip(adhStart).Take(2).ToArray();
                                    if (BitConverter.IsLittleEndian) Array.Reverse(adhBytes); //小端模式转换
                                    adhValue = BitConverter.ToUInt16(adhBytes, 0);
                                }

                                #endregion

                                #region 解码 ADL 部分

                                ushort? adlValue = null;
                                if (i < device.AnalogChannelCount)
                                {
                                    var adlStart = 4 + 2 + 32 + 8 + 2 * 4 + 2 * 3 - i * 2;
                                    var adlBytes = decodedFrame.Skip(adlStart).Take(2).ToArray();
                                    if (BitConverter.IsLittleEndian) Array.Reverse(adlBytes); //小端模式转换
                                    adlValue = BitConverter.ToUInt16(adlBytes, 0);
                                }

                                #endregion

                                #region 解码 Angle 部分

                                int? angleValue = null;
                                if (i < device.AngleChannelCount)
                                {
                                    var angleStart = 4 + 2 + 32 + 8 + 2 * 4 + 2 * 4 + 2 * 3 - i * 2;
                                    var angleBytes = decodedFrame.Skip(angleStart).Take(2).ToArray();
                                    if (BitConverter.IsLittleEndian) Array.Reverse(angleBytes);
                                    angleValue = BitConverter.ToInt16(angleBytes, 0);
                                }

                                #endregion
                                
                                var channelResult = new ChannelResult
                                {
                                    Channel = Convert.ToUInt16(i + 1),
                                    EchoTime = tofIntData + tofDecData, //
                                    HighAi = adhValue, LowAi = adlValue,
                                    Angle = angleValue,
                                };
                                var chConfig = SessionContext.GetChannelConfig(session, device.Address, channelResult.Channel);
                                if (chConfig is {TempProbe: 1})
                                {
                                    intTempProbe = true; //启用集成探头,则全部都是集成探头
                                }
                                if (mcrConfig != null && mcrConfig.EnabledChannels!.Contains(channelResult.Channel))
                                {
                                    multiChResult.Results.Add(channelResult.Channel, channelResult);
                                }
                            }
                            
                            //获取温度及计算声时差
                            var tempBytes = decodedFrame.Skip(38).Take(4).ToArray();
                            var tempRefBytes = decodedFrame.Skip(42).Take(4).ToArray();
                            if (intTempProbe) //集成温度
                            {
                                var temp1Ch = tempBytes[0];
                                var temp1Value = _pt1000Decoder.GetAdTemp(BitConverter.ToInt16(new[] {tempBytes[2], tempBytes[1]}));
                                var temp2Ch = tempBytes[3];
                                var temp2Value = _pt1000Decoder.GetAdTemp(BitConverter.ToInt16(new[] {tempRefBytes[1], tempRefBytes[0]}));
                                SessionContext.StoreChannelTemp(session, device.Address, temp1Ch, temp1Value);
                                SessionContext.StoreChannelTemp(session, device.Address, temp2Ch, temp2Value);
                            }
                            else
                            {
                                Array.Reverse(tempBytes);
                                Array.Reverse(tempRefBytes);
                                var ptRes = BitConverter.ToUInt32(tempBytes, 0);
                                var refRes = BitConverter.ToUInt32(tempRefBytes, 0);
                                var aloneTemp = _pt1000Decoder.GetResult(ptRes, refRes);
                                if (aloneTemp.HasValue)//独立温度 则8个通道都缓存独立温度数值
                                {
                                    for (var i = 0; i < device.UsChannelCount; i++)
                                    {
                                        var tempChannel = Convert.ToUInt16(i + 1);
                                        SessionContext.StoreChannelTemp(session, device.Address, tempChannel, aloneTemp.Value);
                                    }
                                }
                            }
                            
                            foreach (var kvResult in multiChResult.Results)
                            {
                                var chConfig = SessionContext.GetChannelConfig(session, device.Address, kvResult.Key);
                                var chResult = kvResult.Value;
                                chResult.ReceiveTime = multiChResult.ReceiveTime;
                                chResult.SpTemp = SessionContext.GetChannelTemp(session, device.Address, kvResult.Key);
                                //小窗口在线中值滤波
                                chResult.EchoTime = (decimal) GetMedianValue(session, chResult.Address, chResult.Channel, 
                                    nameof(ChannelResult.EchoTime), (double) chResult.EchoTime!);
                                chResult.SpTemp = (decimal) GetMedianValue(session, chResult.Address, chResult.Channel,
                                    nameof(ChannelResult.SpTemp), (double) chResult.SpTemp!, 10);
                                if (chResult.HighAi.HasValue)
                                {
                                    chResult.HighAi = (ushort) GetMedianValue(session, chResult.Address, chResult.Channel, 
                                        nameof(ChannelResult.HighAi), (double) chResult.HighAi);
                                }
                                if (chResult.LowAi.HasValue)
                                {
                                    chResult.LowAi = (ushort) GetMedianValue(session, chResult.Address, chResult.Channel, 
                                        nameof(ChannelResult.LowAi), (double) chResult.LowAi);
                                }
                                chResult.Angle = Convert.ToInt16(GetMedianValue(session, chResult.Address, chResult.Channel, 
                                    nameof(ChannelResult.Angle), chResult.Angle!.Value));
                                if (chConfig is {RefTime: not null, RefTemp: not null})//包含参考值则计算声时差
                                {
                                    var tempCoefficient = chConfig.TempCoefficient ?? CalculateExtensions.DefaultTempCoeff;
                                    chResult.DeltaTof = CalculateExtensions.CalcDeltaTof(chConfig.RefTime.Value, chConfig.RefTemp.Value, (decimal) chResult.EchoTime!,
                                        chResult.SpTemp!.Value, chConfig.TcMode, tempCoefficient);
                                }
                            }
                            decodedList.Add(multiChResult);
                            ContextData.RemoveRange(0,decodedFrame.Count);
                            _headBeginIdx = -1;
                            
                            frameCount++;
                            if (frameCount > MaxFramesPerBatch)//单次处理超限制时暂时退出解码任务
                            {
                                break;
                            }
                        }
                        else
                        {
                            ContextData.RemoveAt(0); //删除首个元素，继续往后找
                            _headBeginIdx = -1;
                            break;
                        }
                    }
                    catch (Exception)
                    {
                        ContextData.RemoveAt(0); //删除首个元素，继续往后找
                        _headBeginIdx = -1;
                        break;
                    }
                }
            }
        }
        catch (Exception e)
        {
            Logger.Error($"{nameof(DecodeMultiChannelResults)} 异常. 细节:{e.Message}");
            ContextData.RemoveAt(0); //删除首个元素，继续往后找
            _headBeginIdx = -1;
        }
        return decodedList;
    }
    /// <summary>
    /// 解码读全波结果
    /// </summary>
    private List<ChannelResult> DecodeFullWaveResults(IoSession session, CancellationToken token)
    {
        var decodedList = new List<ChannelResult>();
        var frameSize = DecFullWaveFrameHead.Length + 3 + 3; //变长帧最小长度
        while (!token.IsCancellationRequested && ContextData.Count >= frameSize)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (_headBeginIdx < 0) //如果之前还未找到帧头
                {
                    _headBeginIdx = ByteUtility.IndexOf(ContextData, DecFullWaveFrameHead);
                    if (_headBeginIdx < 0)
                    {
                        var removeLength = ContextData.Count - DecFullWaveFrameHead.Length;
                        ContextData.RemoveRange(0,removeLength); //保留 4 个字节确保帧头不被清理
                        break; //找不到帧头，清理前面的数据 等待新的数据再开始解析
                    }
                }

                //找到帧头，清理帧头以前的数据
                if (_headBeginIdx > 0)
                {
                    ContextData.RemoveRange(0,_headBeginIdx);
                    _headBeginIdx = 0;
                }

                //如果找到了帧头，则继续长度字节的寻找
                if (ContextData.Count > (DecFullWaveFrameHead.Length + 6)) //帧头(4)+长度(2)
                {
                    var lenStartIdx = DecFullWaveFrameHead.Length + 3;
                    var lenBytes = ContextData.Skip(lenStartIdx).Take(3).ToArray();
                    var dataLen = (int) ByteUtility.GetDataLen(lenBytes);
                    var endIdx = lenStartIdx + 3 + dataLen; //数据帧总长度
                    if (ContextData.Count < endIdx)
                    {
                        break;
                    }
                    CurrentAcqTime = DateTime.Now;//全波采集时间采用当前时间
                    
                    var decodedFrame = ContextData.GetRange(0,endIdx - _headBeginIdx).ToList();
                    var waveStartIdx = DecFullWaveFrameHead.Length + 3 + 3;
                    var waveData = new List<short>();
                    for (var i = 0; i < dataLen; i++)
                    {
                        var dataByte = decodedFrame[waveStartIdx + i];
                        if (dataByte > 127)
                        {
                            waveData.Add((sbyte) (dataByte - 256));
                        }
                        else
                        {
                            waveData.Add(dataByte);
                        }
                    }
                    var channelResult = new ChannelResult
                    {
                        ReceiveTime = CurrentAcqTime.Value, 
                        HexString = ByteUtility.BytesToHexStr(decodedFrame),
                        EchoWave = new EchoWave
                        {
                            ClockInt = Convert.ToUInt16(OriginalInterval),
                            EchoStart = 120,
                            WaveData = waveData,
                        },
                    };
                    decodedList.Add(channelResult);
                    ContextData.RemoveRange(0,decodedFrame.Count);
                    _headBeginIdx = -1;
                }
                else
                {
                    break;
                }
            }
            catch (Exception)
            {
                ContextData.RemoveAt(0);//删除首个元素 继续找
                _headBeginIdx = -1;
                break;
            }
        }
        return decodedList;
    }
    #endregion
    private bool IsFrameTailValid(List<byte> frameData, byte[] frameTail)
    {
        // 检查 frameData 的长度是否足够包含帧尾
        if (frameData.Count < frameTail.Length)
        {
            return false; // 数据不足，无法比较
        }

        // 获取 frameData 的最后 DecLWaveFrameTail.Length 个字节
        var getFrameTail = frameData.Skip(frameData.Count - frameTail.Length).ToArray();
        // 比较 frameTail 和 DecLWaveFrameTail
        return frameTail.SequenceEqual(getFrameTail);
    }
}