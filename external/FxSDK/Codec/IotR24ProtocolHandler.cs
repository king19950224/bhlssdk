using System.Collections;
using FxSDK.Core;
using FxSDK.Models;
using FxSDK.Utility;

namespace FxSDK.Codec;

public class IotR24ProtocolHandler : FxProtocolHandler
{
    public IotR24ProtocolHandler()
    {
        DelayMilliseconds = 10;
    }
    /// <summary>
    /// 编码帧头
    /// </summary>
    private const byte EncFrameHead = 0xEF;
    /// <summary>
    /// 编码模拟量参数帧头
    /// </summary>
    private const byte EncAnalogFrameHead = 0xEE;
    /// <summary>
    /// 工作模式命令码
    /// </summary>
    private const byte ModeCommandCode = 0x03;
    /// <summary>
    /// 解码结果帧头
    /// </summary>
    private static readonly byte[] DecResultFrameHead = {0xDE, 0xDE, 0xDE, 0xDE};

    /// <summary>
    /// 解码波形帧头
    /// </summary>
    private static readonly byte[] DecEchoFrameHead = {0x70, 0x70, 0x70, 0x70};

    /// <summary>
    /// 解码全波波形帧头
    /// </summary>
    private static readonly byte[] DecFullEchoFrameHead = {0x72, 0x72, 0x72, 0x72};

    /// <summary>
    /// 解码帧尾
    /// </summary>
    private static readonly byte[] DecFrameTail = {0x00, 0x00, 0x00, 0x00};

    /// <summary>
    /// 写入帧Size
    /// </summary>
    private const byte EncFrameSize = 5;

    public const decimal OriginalInterval = 10M; //原始回波时钟

    public const short NoiseBase = 10; //噪声带
    public const short SignalAuto = 90;//自动增益幅度
    public const short SignalMax = 110;//最大值
    public static readonly double[] GainRange = {0, 80};
    public static readonly decimal GainStep = 0.1M;//增益步长
    private const int UltraCount = 24; //24通道
    private const decimal EchoTimeUnit = 0.625M; //回波声时量化值
    private const decimal TempUnit = 0.00625M; //温度量化单位
    public override IReadOnlyList<object> EncodeDevice(IoSession session, DeviceConfig config, string? propertyName = null)
    {
        var encodeMessages = new List<object>();
        //编码测量频率
        var measBytes = new List<byte>();
        if (config.MeasConfig != null)
        {
            var freqBytes = EncodeMeasFreq(config.MeasConfig.Frequency);
            measBytes.AddRange(freqBytes);
        }

        if (!string.IsNullOrEmpty(propertyName))
        {
            if (propertyName.Equals(nameof(config.MeasConfig))) //配置测量参数
            {
                encodeMessages.Add(measBytes.ToArray());
            }
            else if (propertyName.Equals(nameof(config.WriteFlash))) //写flash
            {
                var writeFlashParams = new byte[] { 0x00 };
                encodeMessages.Add(EncodeWriteParamCmd(0x17, writeFlashParams));
            }
            else if (propertyName.Equals(nameof(config.ReadFlash))) //读flash
            {
                var readFlashParams = new byte[] { 0x00 };
                encodeMessages.Add(EncodeWriteParamCmd(0x18, readFlashParams));
            }
        }
        else
        {
            encodeMessages.Add(measBytes.ToArray());
        }
        return encodeMessages;
    }

    /// <summary>
    /// 编码测量频率
    /// </summary>
    private byte[] EncodeMeasFreq(decimal freq)
    {
        var commandParams = new byte[3];
        commandParams[0] = freq switch
        {
            100M => 0x00,
            1M => 0x06,
            _ => 0x07
        };
        return EncodeWriteParamCmd(0x10, commandParams);
    }

    /// <summary>
    /// 编码写参数命令
    /// </summary>
    /// <param name="commandCode">功能码</param>
    /// <param name="commandParams">功能参数</param>
    private byte[] EncodeWriteParamCmd(byte commandCode, byte[] commandParams)
    {
        var writeCmdBytes = new byte[EncFrameSize];
        writeCmdBytes[0] = EncFrameHead; //帧头
        writeCmdBytes[1] = commandCode; //功能编码
        Array.Copy(commandParams, 0, writeCmdBytes, 2, commandParams.Length);
        return writeCmdBytes;
    }
    private byte[] EncodeWriteAnalogParamCmd(byte commandCode, byte[] commandParams)
    {
        var writeCmdBytes = new byte[EncFrameSize];
        writeCmdBytes[0] = EncAnalogFrameHead; //帧头
        writeCmdBytes[1] = commandCode; //功能编码
        Array.Copy(commandParams, 0, writeCmdBytes, 2, commandParams.Length);
        return writeCmdBytes;
    }
    public override IReadOnlyList<object> EncodeChannel(IoSession session, ChannelConfig config, string? propertyName = null)
    {
        var encodeMessages = new List<object>();
        //编码通道信息(指定配置的通道)-必要指令
        var channelParams = new[] {Convert.ToByte(config.Channel)};
        encodeMessages.Add(EncodeWriteParamCmd(0x01, channelParams));
        encodeMessages.Add(FxUtility.Delay10Ms);
        //编码参数更新使能-必要指令
        var updateEnableParams = new byte[] {0x01};
        encodeMessages.Add(EncodeWriteParamCmd(0x12, updateEnableParams));
        encodeMessages.Add(FxUtility.Delay10Ms);
        //编码回波窗口
        var echoWinParams = new byte[3];
        var echoWinDiv = Convert.ToUInt32(config.EchoWin / OriginalInterval);
        var echoWinDivBytes = BitConverter.GetBytes(echoWinDiv);
        Array.Reverse(echoWinDivBytes);
        Array.Copy(echoWinDivBytes, 1, echoWinParams, 0, 3);
        var echoWinBytes = EncodeWriteParamCmd(0x04, echoWinParams);
        //编码探头频率
        var probeFreqBytes = EncodeProbeFreq(config.ProbeFreq);
        //编码增益
        var gainParams = BitConverter.GetBytes(Convert.ToUInt16(config.Gain * 10));
        var gainBytes = EncodeWriteAnalogParamCmd(0x07, gainParams);
        //编码测量阈值
        var thresholdParams = new byte[1];
        thresholdParams[0] = config.Threshold switch
        {
            > 0 => (byte) config.Threshold,
            < 0 => (byte) (config.Threshold + 256),
            _ => -64 + 256
        };
        var thresholdBytes = EncodeWriteParamCmd(0x15, thresholdParams);
        //编码温度补偿
        var tcModeParams = new byte[1];
        tcModeParams[0] = (byte) (config.TcMode == 1 ? 0x00 : 0x11);//1使用主机温度补偿,其他不开启
        var tcModeBytes = EncodeWriteParamCmd(0x16, tcModeParams);
        //编码 参考声时配置参数
        var refTimeParams = new byte[3];
        var wrTime = 0M;
        if (config.IsDeltaToFMode && config is {RefTime: > 0, RefTemp: not null})
        {
            wrTime = config.RefTime.Value;
            if (config.TcMode == 1)//1使用主机温度补偿则基础声时归一到标准温度
            {
                wrTime = CalculateExtensions.CalcNormalTof(config.RefTime.Value, config.RefTemp.Value, CalculateExtensions.StandardTemperature);
            }
        }
        var refTimeDiv = Convert.ToUInt32(wrTime / EchoTimeUnit);
        var refTimeDivBytes = BitConverter.GetBytes(refTimeDiv);
        Array.Reverse(refTimeDivBytes); //
        Array.Copy(refTimeDivBytes, 1, refTimeParams, 0, 3);
        var refTimeBytes = EncodeWriteParamCmd(0x0A, refTimeParams);
        //指定参数更新
        if (!string.IsNullOrEmpty(propertyName))
        {
            if (propertyName.Equals(nameof(config.EchoWin)))
            {
                encodeMessages.Add(FxUtility.Delay10Ms);
                encodeMessages.Add(echoWinBytes);
            }
            else if (propertyName.Equals(nameof(config.ProbeFreq)))
            {
                encodeMessages.Add(FxUtility.Delay10Ms);
                encodeMessages.Add(probeFreqBytes);
            }
            else if (propertyName.Equals(nameof(config.Gain)))
            {
                encodeMessages.Add(FxUtility.Delay10Ms);
                encodeMessages.Add(gainBytes);
            }
            else if (propertyName.Equals(nameof(config.Threshold)))
            {
                encodeMessages.Add(FxUtility.Delay10Ms);
                encodeMessages.Add(thresholdBytes);
            }
            else if (propertyName.Equals(nameof(config.TcMode)))
            {
                encodeMessages.Add(FxUtility.Delay10Ms);
                encodeMessages.Add(tcModeBytes);
            }
            else if (propertyName.Equals(nameof(config.RefTime)))
            {
                encodeMessages.Add(FxUtility.Delay10Ms);
                encodeMessages.Add(refTimeBytes);
            }
        }
        else
        {
            //全部参数更新(默认不写入参考声时)
            encodeMessages.Add(FxUtility.Delay10Ms);
            encodeMessages.Add(echoWinBytes);
            encodeMessages.Add(FxUtility.Delay10Ms);
            encodeMessages.Add(probeFreqBytes);
            encodeMessages.Add(FxUtility.Delay10Ms);
            encodeMessages.Add(gainBytes);
            encodeMessages.Add(FxUtility.Delay10Ms);
            encodeMessages.Add(thresholdBytes);
            encodeMessages.Add(FxUtility.Delay10Ms);
            encodeMessages.Add(tcModeBytes);
            encodeMessages.Add(FxUtility.Delay10Ms);
        }
        //需要加入扫波指令才能更新
        var lWaveParams = new byte[] {0x01};
        encodeMessages.Add(EncodeWriteParamCmd(ModeCommandCode, lWaveParams));
        //编码参数更新结束-必要指令
        var updateEndParams = new byte[] {0x02};
        encodeMessages.Add(EncodeWriteParamCmd(0x12, updateEndParams));
        return encodeMessages;
    }

    /// <summary>
    /// 编码测量频率
    /// </summary>
    private byte[] EncodeProbeFreq(decimal freq)
    {
        var commandParams = new byte[1];
        commandParams[0] = freq switch
        {
            0M => 0x00,
            2M => 0x01,
            _ => 0x01
        };
        return EncodeWriteParamCmd(0x06, commandParams);
    }

    public override IReadOnlyList<object> EncodeWorkMode(IoSession session, WorkMode mode, ushort channel = 0, ushort address = 1)
    {
        var encodeMessages = new List<object>();
        var commandParams = new byte[1];

        switch (mode)
        {
            case WorkMode.SingleMeas:
            case WorkMode.LWaveRead:
                //编码通道信息(指定配置的通道)-模式通道
                var channelParams = new[] {Convert.ToByte(channel)};
                encodeMessages.Add(EncodeWriteParamCmd(0x01, channelParams));
                encodeMessages.Add(FxUtility.Delay10Ms);
                //编码模式信息
                commandParams[0] = 0x01;
                encodeMessages.Add(EncodeWriteParamCmd(ModeCommandCode, commandParams));
                break;
            case WorkMode.MultiMeas:
                commandParams[0] = 0x05;
                encodeMessages.Add(EncodeWriteParamCmd(ModeCommandCode, commandParams));
                break;
            case WorkMode.FullWaveRead:
                //编码通道信息(指定配置的通道)-模式通道
                var channelFullParams = new[] {Convert.ToByte(channel)};
                encodeMessages.Add(EncodeWriteParamCmd(0x01, channelFullParams));
                encodeMessages.Add(FxUtility.Delay10Ms);
                commandParams[0] = 0x06;
                encodeMessages.Add(EncodeWriteParamCmd(ModeCommandCode, commandParams));
                break;
            case WorkMode.Standby:
                commandParams[0] = 0x02;
                encodeMessages.Add(EncodeWriteParamCmd(ModeCommandCode, commandParams));
                break;
        }
        lock (AcqTimeLock)
        {
            CurrentAcqTime = null;
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
        //忽略
        return new[] {FxUtility.Delay10Ms};
    }

    #region 按照协议解码数据
    protected override List<DecodedResult> DoDecode(IoSession session, CancellationToken token)
    {
        var decodedResults = new List<DecodedResult>();
        var deviceMode = SessionContext.GetDeviceMode(session);
        switch (deviceMode)
        {
            case WorkMode.SingleMeas:
            case WorkMode.LWaveRead:
                decodedResults.AddRange(DecodeChannelResults(session,token));
                break;
            case WorkMode.MultiMeas:
                decodedResults.AddRange(DecodeMultiChannelResults(session,token));
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
    /// 解码单通道结果
    /// </summary>
    private List<ChannelResult> DecodeChannelResults(IoSession session, CancellationToken token)
    {
        var decodedList = new List<ChannelResult>();
        var frameSize = DecEchoFrameHead.Length + 3 + 3 + 1 + 6 + 2 + 1 + 3991; //定长帧长度
        while (!token.IsCancellationRequested && ContextData.Count >= frameSize)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (_headBeginIdx < 0)
                {
                    _headBeginIdx = ByteUtility.IndexOf(ContextData, DecEchoFrameHead);
                    if (_headBeginIdx < 0)
                    {
                        var removeLength = ContextData.Count - DecEchoFrameHead.Length;
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
                CurrentAcqTime = DateTime.Now; //波形采集时间采用当前时间
                var decodedFrame = ContextData.GetRange(0, frameSize);
                var channel = decodedFrame[DecEchoFrameHead.Length + 15];
                var waveData = new List<short>();
                var waveIdx = DecEchoFrameHead.Length + 16;
                //3991个字节波形数据
                for (var i = waveIdx; i < frameSize; i++)
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

                var channelConfig = SessionContext.GetChannelConfig(session, ChannelConfig.DefaultAddress, channel);
                var waveDataDouble = waveData.Select(o => (double) o).ToList();
                short statusCode = -5;
                var threshold = waveDataDouble.Min() * 0.6d;
                if (channelConfig != null)
                {
                    threshold = channelConfig.Threshold;
                }
                if (decodedFrame[DecEchoFrameHead.Length + 6] >= 0x80)
                {
                    statusCode = 0;
                }
                var zeroIdx = CalculateExtensions.CalcZeroIndexByThrSignal(waveDataDouble, OriginalInterval, out var midResults, NoiseBase, Convert.ToInt16(threshold));
                if (zeroIdx < 0)
                {
                    statusCode = Convert.ToInt16(zeroIdx);
                }

                //波形起始3字节 高-低
                var echoStartBytes = decodedFrame.GetRange(DecEchoFrameHead.Length, 3).ToArray();
                var echoStartFourBytes = new byte[4];
                Array.Copy(echoStartBytes, 0, echoStartFourBytes, 1, 3);
                if (BitConverter.IsLittleEndian)
                    Array.Reverse(echoStartFourBytes);
                //声时3字节(可能是声时差或者回波声时) 高-低
                var tofBytes = decodedFrame.GetRange(DecEchoFrameHead.Length + 3, 3).ToArray();
                var tof = DecodeTof(tofBytes) * EchoTimeUnit;
                var isDeltaToFMode = true;
                if (channelConfig != null)
                {
                    isDeltaToFMode = channelConfig.IsDeltaToFMode;
                }
                //2字节温度结果 高-低
                var tempBytes = decodedFrame.GetRange(DecEchoFrameHead.Length + 13, 2).ToArray();
                if (BitConverter.IsLittleEndian)
                    Array.Reverse(tempBytes);
                var spTemp = BitConverter.ToInt16(tempBytes) * TempUnit;
                //赋值到对象
                var channelResult = new ChannelResult
                {
                    HexString = ByteUtility.BytesToHexStr(decodedFrame),
                    StatusCode = statusCode,
                    MidResults = midResults,
                    Channel = channel,
                    SpTemp = spTemp,
                    EchoWave = new EchoWave
                    {
                        EchoStart = BitConverter.ToUInt32(echoStartFourBytes, 0) * 10 + 30, //延时30ns时间对齐
                        ClockInt = Convert.ToUInt16(OriginalInterval),
                        WaveData = waveData,
                    },
                    ReceiveTime = CurrentAcqTime.Value,
                };
                //根据判断的值开始计算
                if (isDeltaToFMode)
                {
                    channelResult.DeltaTof = tof;
                    if (channelConfig is {RefTime: > 0, RefTemp: not null})
                    {
                        var echoTime = channelConfig.RefTime.Value + channelResult.DeltaTof;
                        if (channelConfig.TcMode == 1)//默认主机温度补偿
                        {
                            var refNormal = CalculateExtensions.CalcNormalTof(channelConfig.RefTime.Value, channelConfig.RefTemp!.Value, CalculateExtensions.StandardTemperature);
                            var normalTof = refNormal + channelResult.DeltaTof;//标准声速下的Tof
                            echoTime = CalculateExtensions.CalcNormalTof(normalTof!.Value, CalculateExtensions.StandardTemperature, spTemp);
                        }
                        else if (channelConfig.TcMode == 2)//使用自定义补偿系数,则DeltaTof先设置为null后面重算
                        {
                            channelResult.DeltaTof = null;
                        }
                        channelResult.EchoTime = echoTime;
                    }
                }
                else
                {
                    channelResult.EchoTime = tof;
                    if (channelConfig!.TcMode == 1)
                    {
                        channelResult.EchoTime = CalculateExtensions.CalcNormalTof(tof, CalculateExtensions.StandardTemperature, spTemp);
                    }
                }
                //如果通道有参考声时没有声时差,则尝试计算声时差
                if (channelConfig?.RefTime is > 0 && !channelResult.DeltaTof.HasValue)
                {
                    channelResult.DeltaTof = CalculateExtensions.CalcDeltaTof(channelConfig.RefTime.Value,channelConfig.RefTemp!.Value,
                        channelResult.EchoTime!.Value, spTemp, channelConfig.TcMode,channelConfig.TempCoefficient!.Value);
                }
                //状态数据解析
                var stateByte = decodedFrame[DecEchoFrameHead.Length + 6] & 0x7F;
                channelResult.StatusCode = stateByte switch
                {
                    0x01 => -1,
                    0x02 => -2,
                    0x03 => -3,
                    _ => channelResult.StatusCode
                };
                //下位机参数
                var devGain = (BitConverter.ToUInt16(new[]{
                                   decodedFrame[DecEchoFrameHead.Length + 8],
                                   decodedFrame[DecEchoFrameHead.Length + 7]}) & 0xFFF) * 0.1M / 2.5M; //增益
                var devEchoWin = BitConverter.ToUInt32(new[]{
                    decodedFrame[DecEchoFrameHead.Length + 11], decodedFrame[DecEchoFrameHead.Length + 10],
                    decodedFrame[DecEchoFrameHead.Length + 9], (byte) 0x00}) * 10M; //窗口参数
                var devThreshold = Convert.ToDecimal(decodedFrame[DecEchoFrameHead.Length + 12]);
                if (devThreshold > 127)
                {
                    devThreshold = devThreshold - 256;
                }
                if (channelResult.Channel > 24)
                {
                    channelResult.Channel = 1;
                }
                channelResult.ParamsArray = new[] {devEchoWin, devGain, devThreshold};
                decodedList.Add(channelResult);
                ContextData.RemoveRange(0,decodedFrame.Count);
                _headBeginIdx = -1; //
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
    /// 解码3字节声时(补码形式)
    /// </summary>
    private static int DecodeTof(byte[] tofBytes)
    {
        var byteArray = new byte[4];
        Array.Copy(tofBytes, 0, byteArray, 1, 3);
        // 根据当前系统的字节序来调整字节顺序
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(byteArray);
        }

        // 将四个字节转换为 uint32
        var data = BitConverter.ToUInt32(byteArray, 0);
        // 计算有符号3字节的最大正数值
        var maxPositiveValue = (1u << 23) - 1;
        // 如果数据大于最大正数值，则表示是负数，需要进行补码转换
        if (data > maxPositiveValue)
        {
            return (int) (data - (1u << 24));
        }
        else
        {
            return (int) data;
        }
    }
    /// <summary>
    /// 解码多通道结果
    /// </summary>
    private List<MultiChannelResult> DecodeMultiChannelResults(IoSession session, CancellationToken token)
    {
        var decodedList = new List<MultiChannelResult>();
        //
        try
        {
            var devConfig = SessionContext.GetDeviceConfig(session);
            if (devConfig != null)
            {
                var mcrConfig = devConfig.MCRConfig;
                //保留信息(2) + 状态高(3) + 状态中(3) + 温度信息(2) + 1~24声时*3 + 状态低(3) + 结束(2)
                var frameHeadSize = DecEchoFrameHead.Length;
                var frameSize = frameHeadSize + 2 + 3 + 3 + 3 + 2 + 24 * 3 + 2;
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
                                ContextData.RemoveRange( 0,removeRange); //保留帧头长度的字节
                                break;
                            }
                        }

                        //找到帧头，清理帧头以前的数据
                        if (_headBeginIdx > 0)
                        {
                            ContextData.RemoveRange(0,_headBeginIdx);
                            _headBeginIdx = 0;
                        }

                        if (ContextData.Count < frameSize)
                        {
                            break; //数据不全，等待补充完整
                        }

                        lock (AcqTimeLock)
                        {
                            CurrentAcqTime = CurrentAcqTime?.AddSeconds((double) devConfig!.SampleInterval) ?? DateTime.Now;
                        }
                        var decodedFrame = ContextData.GetRange(0,frameSize);
                        //解码温度信息
                        var tempBytes = decodedFrame.GetRange(frameHeadSize + 8, 2).ToArray();
                        if (BitConverter.IsLittleEndian)
                            Array.Reverse(tempBytes);
                        var spTemp = BitConverter.ToInt16(tempBytes) * TempUnit;
                        var multiChResult = new MultiChannelResult
                        {
                            HexString = ByteUtility.BytesToHexStr(decodedFrame),
                            ReceiveTime = CurrentAcqTime.Value,
                            Results = new Dictionary<ushort, ChannelResult>(),
                        };
                        //解码状态
                        var statusBytes = new List<byte>();
                        statusBytes.AddRange(decodedFrame.GetRange(frameHeadSize + 2, 3)); //高 3 字节
                        statusBytes.AddRange(decodedFrame.GetRange(frameHeadSize + 5, 3)); //中 3 字节
                        statusBytes.AddRange(decodedFrame.GetRange(frameHeadSize + 82, 3)); //低 3 字节
                        var decodeStatusArray = DecodeStatus(statusBytes.ToArray());
                        //解码24个通道结果
                        for (var i = 0; i < UltraCount; i++)
                        {
                            var channel = Convert.ToUInt16(i + 1);
                            var echoBytes = decodedFrame.GetRange(frameHeadSize + i * 3 + 10, 3).ToArray();
                            var tof = DecodeTof(echoBytes) * EchoTimeUnit;
                            var channelEnabled = mcrConfig?.EnabledChannels?.Contains(channel) ?? true;
                            if (channelEnabled)
                            {
                                var channelResult = new ChannelResult
                                {
                                    Channel = channel, DeltaTof = tof, SpTemp = spTemp,
                                    ReceiveTime = multiChResult.ReceiveTime,
                                };
                                var channelConfig = SessionContext.GetChannelConfig(session, ChannelConfig.DefaultAddress, channel);
                                if (channelConfig is {RefTime: > 0, RefTemp: not null})
                                {
                                    var echoTime = channelConfig.RefTime.Value + channelResult.DeltaTof;
                                    if (channelConfig.TcMode == 1)//默认主机温度补偿
                                    {
                                        var refNormal = CalculateExtensions.CalcNormalTof(channelConfig.RefTime.Value, channelConfig.RefTemp!.Value, CalculateExtensions.StandardTemperature);
                                        var normalTof = refNormal + channelResult.DeltaTof;//标准声速下的Tof
                                        echoTime = CalculateExtensions.CalcNormalTof(normalTof!.Value, channelConfig.RefTemp!.Value, spTemp);
                                    }
                                    channelResult.EchoTime = echoTime;
                                }
                                channelResult.StatusCode = decodeStatusArray[i] switch
                                {
                                    0x01 => -1,
                                    0x02 => -2,
                                    0x03 => -3,
                                    _ => channelResult.StatusCode
                                };
                                //小窗口在线中值滤波
                                channelResult.DeltaTof = (decimal) GetMedianValue(session, channelResult.Address, channelResult.Channel,
                                    nameof(ChannelResult.DeltaTof), (double) channelResult.DeltaTof!);
                                channelResult.SpTemp = (decimal) GetMedianValue(session, channelResult.Address, channelResult.Channel,
                                    nameof(ChannelResult.SpTemp), (double) channelResult.SpTemp,10);
                                multiChResult.Results.Add(channel, channelResult);
                            }
                        }
                        decodedList.Add(multiChResult);
                        ContextData.RemoveRange(0,decodedFrame.Count);
                        _headBeginIdx = -1;
                    }
                    catch (Exception)
                    {
                        ContextData.RemoveAt(0); //删除一个元素，继续往后找
                        _headBeginIdx = -1;
                        break;
                    }
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"{nameof(DecodeMultiChannelResults)} 异常. 细节:{e.Message}");
        }

        return decodedList;
    }
    /// <summary>
    /// 解码状态数组(高-低)
    /// </summary>
    private byte[] DecodeStatus(byte[] statusBytes)
    {
        var decodeArray = new byte[24];
        if(BitConverter.IsLittleEndian)
            Array.Reverse(statusBytes);
        var bitArray = new BitArray(statusBytes);
        bitArray.Reverse();

        for (var i = 0; i < 24; i++)
        {
            var bitIndex = i * 3; // 每个通道占3bit
            //高-低
            var b2 = bitArray.Get(bitIndex + 0) ? 1 : 0;
            var b1 = bitArray.Get(bitIndex + 1) ? 1 : 0;
            var b0 = bitArray.Get(bitIndex + 2) ? 1 : 0;

            var state = (b2 << 2) | (b1 << 1) | b0;
            decodeArray[i] = (byte)state;
        }

        return decodeArray;
    }
    /// <summary>
    /// 解码读全波结果
    /// </summary>
    private List<ChannelResult> DecodeFullWaveResults(IoSession session, CancellationToken token)
    {
        var decodedList = new List<ChannelResult>();
        var frameSize = DecFullEchoFrameHead.Length + 3 + 3; //变长帧最小长度
        while (!token.IsCancellationRequested && ContextData.Count >= frameSize)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (_headBeginIdx < 0) //如果之前还未找到帧头
                {
                    _headBeginIdx = ByteUtility.IndexOf(ContextData, DecFullEchoFrameHead);
                    if (_headBeginIdx < 0)
                    {
                        var removeLength = ContextData.Count - DecFullEchoFrameHead.Length;
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
                if (ContextData.Count > (DecFullEchoFrameHead.Length + 6)) //帧头(4)+长度(2)
                {
                    var lenStartIdx = DecFullEchoFrameHead.Length + 3;
                    var lenBytes = ContextData.Skip(lenStartIdx).Take(3).ToArray();
                    var dataLen = (int) ByteUtility.GetDataLen(lenBytes);
                    var endIdx = lenStartIdx + 3 + dataLen; //数据帧总长度
                    if (ContextData.Count < endIdx)
                    {
                        break;
                    }
                    CurrentAcqTime = DateTime.Now;//全波采集时间采用当前时间

                    var decodedFrame = ContextData.GetRange(0,endIdx - _headBeginIdx).ToList();
                    var waveStartIdx = DecFullEchoFrameHead.Length + 3 + 3;
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
}
