using System.Collections;
using FxSDK.Core;
using FxSDK.Models;
using FxSDK.Utility;
using NLog;

namespace FxSDK.Codec;

public class MultiChIotProtocolHandler : FxProtocolHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger(nameof(MultiChIotProtocolHandler));
    private const int LogPreviewByteCount = 32;
    private static readonly TimeSpan RepeatedFrameLogInterval = TimeSpan.FromSeconds(5);
    private static readonly byte[] FrameHead = {0xDE, 0xDE, 0xDE, 0xDE};
    private static readonly byte[] FrameTail = {0x00, 0x00, 0x00, 0x00};
    private static readonly byte[] WakeupHead = {0xF0, 0xF0, 0xF0, 0xF0, 0xF0};
    private const ushort LenMask = 0x9000; //长度掩码
    private const ushort BoardChannelMax = 48; //总通道数
    private const ushort BcAddress = 0xFFFF; //广播地址
    private const double SampleRate = 8000000d; //波形采样频率8MHz
    private const ushort QUnit = 1000; //窗口量化单位ns
    private const decimal OriginalInterval = 125M; //原始点间隔
    private const decimal InterpolationInterval = 25M; //插值时间间隔
    public const short NoiseBase = 50;
    public const short SignalMax = 600;
    public const short SignalAuto = 400;
    public static readonly double[] GainRange = {1, 47};
    public static readonly decimal GainStep = 1M;//增益步长
    public const ushort LoraWaitMs = 400;//lora发送等待
    private const byte LoraChMax = 79; //Lora的话 最大信道设置
    public const ushort QuantizationUnit = 1000;//量化单位
    private const ushort StorageMaxPts = 120;//最大存储波形点数
    private const byte ChannelRegisterCount = 5; //通道硬件寄存器个数(0x00 探头频率 ~ 0x04 增益)
    /// <summary>
    /// ReadChannelParamAsync 的合并读名：一帧读回 0x00~0x04 全部通道寄存器，
    /// 解码后仍按 ProbeFreq/EchoWin/EchoWidth/PulseConfig/Gain 逐个抛出 DeviceParameter。
    /// </summary>
    public const string ChannelRegistersParam = "ChannelRegisters";
    /// <summary>
    /// 写参数(0xA1)/写存储(0xC1)应答帧对应的 DeviceParameter 名，ParamValue 为应答命令码。
    /// 上层据此得知设备已处理写命令，不必靠定长等待。
    /// </summary>
    public const string WriteAckParam = "WriteAck";
    #region 对象编码成字节数组
    /// <summary>
    /// 设备配置,不使用参数的话只包含通用测量参数配置
    /// </summary>
    public override IReadOnlyList<object> EncodeDevice(IoSession session, DeviceConfig config, string? propertyName = null)
    {
        var encodeMessages = new List<object>();
        #region 测量参数属性编码部分
        var measParamBytes = new List<byte>();
        var measBytes = new List<byte>();
        var measConfig = config.MeasConfig;
        if (config.MeasConfig != null)
        {
            //配置测量频率
            var frequencyBytes = BitConverter.GetBytes(Convert.ToUInt16(measConfig!.Frequency * 100));
            measParamBytes.AddRange(frequencyBytes);
            //配置对时 天
            var daysSince2000Bytes = BitConverter.GetBytes(measConfig.DaysSince2000);
            measParamBytes.AddRange(daysSince2000Bytes);
            //配置对时 秒
            var secondsTodayBytes = BitConverter.GetBytes(measConfig.SecondsToday);
            measParamBytes.AddRange(secondsTodayBytes);
            //配置采样深度
            var sampleDepthBytes = BitConverter.GetBytes(measConfig.SampleDepth);
            measParamBytes.AddRange(sampleDepthBytes);
        }
        //构建写参数命令,测量参数使用广播
        ushort measStartAddress = 0x0E; //通道测量参数开始地址(包含采样率)
        var measParamConfig = new ParameterConfig(measStartAddress, 4, measParamBytes.ToArray()); //采样率~采样深度
        measBytes.AddRange(WrapCommand(config, config.Address,EncodeWriteParamCmd(BcAddress, measParamConfig)));
        #endregion
        if (!string.IsNullOrEmpty(propertyName))
        {
            if (config.TransferMode == 0)
            {
                encodeMessages.Add(FxUtility.Delay500Ms);//loraMesh 设备加入等待
            }
            if (propertyName.Equals(nameof(config.Address))) //配置设备地址
            {
                ushort startAddress = 0x00; //地址码参数地址
                var addressBytes = BitConverter.GetBytes(config.NAddress);
                //构建写参数命令
                var addressParamConfig = new ParameterConfig(startAddress, 1, addressBytes);
                encodeMessages.Add(WrapCommand(config, config.Address,EncodeWriteParamCmd(BcAddress, addressParamConfig))); //配置地址时用广播地址
            }
            else if (propertyName.Equals(nameof(config.MCRConfig))) //配置轮询参数
            {
                //构建写参数命令,支持LoraMesh多终端
                foreach (var i in ResolveTerminalAddresses(config))
                {
                    if (config.TransferMode == 0)
                    {
                        encodeMessages.Add(FxUtility.Delay500Ms);//loraMesh 设备加入等待
                    }
                    ushort mcrStartAddress = 0x04; //通道轮询参数开始地址
                    var mcrParamBytes = EncodeTerminalMcrConfig(config, i);
                    var mcrParamConfig = new ParameterConfig(mcrStartAddress, 9, mcrParamBytes.ToArray()); //6组选择+1+1+1(9个参数)
                    var mcrCommand = WrapCommand(config, i, EncodeWriteParamCmd(i, mcrParamConfig));
                    encodeMessages.Add(mcrCommand.ToArray());
                    //指令挂起
                    SessionContext.StoreToSession(session,mcrCommand,i);
                    var pendingCount = SessionContext.GetPendingCommands(session,i).Count;
                    var devParameter = new DeviceParameter(nameof(DeviceConfig.PendingCmdCount), pendingCount){Address = i};
                    session.EnqueueDecodedResults(new DecodedResult[] {devParameter});
                }
            }
            else if (propertyName.Equals(nameof(config.MeasConfig))) //配置测量参数
            {
                encodeMessages.Add(measBytes.ToArray());
            }
            else if (propertyName.Equals(nameof(config.BaudRate))) //配置波特率
            {
                ushort startAddress = 0x12; //波特率参数地址
                var baudRateBytes = BitConverter.GetBytes(config.BaudRate);
                //构建写参数命令
                var baudRateParamConfig = new ParameterConfig(startAddress, 1, baudRateBytes);
                encodeMessages.Add(WrapCommand(config, config.Address,
                    EncodeWriteParamCmd(config.Address, baudRateParamConfig)));
            }
            else if (propertyName.Equals(nameof(config.LoraConfig)))
            {
                ushort startAddress = 0x15;//Lora目标参数地址
                var loraConfigBytes = new List<byte>();
                loraConfigBytes.AddRange(BitConverter.GetBytes(config.LoraConfig!.LoraToAddress));
                loraConfigBytes.AddRange(BitConverter.GetBytes(config.LoraConfig.LoraToChannel));
                //构建写参数命令
                var loraParamConfig = new ParameterConfig(startAddress, 2, loraConfigBytes.ToArray());
                encodeMessages.Add(WrapCommand(config, config.Address, EncodeWriteParamCmd(config.Address, loraParamConfig)));
            }
            else if (propertyName.Equals(nameof(config.NetCheckConfig)))//网络参数检查配置
            {
                ushort startAddress = 0x17;
                ushort netCheckEnabled = 0;
                if (config.NetCheckConfig?.Enabled == true)
                {
                    netCheckEnabled = 1;
                }
                var netCheckPeriod = config.NetCheckConfig?.CheckPeriod;
                if (netCheckPeriod >= 24 * 3600)
                {
                    netCheckPeriod = 24 * 3600 - 1;
                }
                var netCheckConfigBytes = new List<byte>();
                netCheckConfigBytes.AddRange(BitConverter.GetBytes(netCheckEnabled));
                netCheckConfigBytes.AddRange(BitConverter.GetBytes((uint) netCheckPeriod!));
                //构建写参数命令
                var netCheckParamConfig = new ParameterConfig(startAddress, 2, netCheckConfigBytes.ToArray());//2个寄存器
                encodeMessages.Add(WrapCommand(config, config.Address, EncodeWriteParamCmd(config.Address, netCheckParamConfig)));
            }
            else
            {
                throw new ArgumentException(
                    "Invalid property name provided. Supported property names for configuration include: 'Address', 'MCRConfig', 'MeasConfig', and 'BaudRate'."
                );
            }
        }
        else
        {
            if (config.TransferMode == 0)
            {
                encodeMessages.Add(FxUtility.Delay500Ms);//loraMesh 设备加入等待
            }
            //测量参数
            if (measBytes.Count > 0)
            {
                encodeMessages.Add(measBytes.ToArray());
            }
        }
        return encodeMessages;
    }
    /// <summary>
    /// 编码单终端轮询配置
    /// </summary>
    private List<byte> EncodeTerminalMcrConfig(DeviceConfig config, ushort terminalAddress = 1)
    {
        var mcrParamBytes = new List<byte>();
        var mcrConfig = config.MCRConfig;
        //配置启用通道选择
        var baEnabledChannels = new BitArray(BoardChannelMax, false);
        if (mcrConfig!.EnabledChannels != null)
        {
            for (ushort i = 1; i <= BoardChannelMax; i++)
            {
                var logicChannel = config.GetLogicChannel(terminalAddress, i);
                if (mcrConfig.EnabledChannels.Contains(logicChannel))
                {
                    baEnabledChannels.Set(i - 1, true);
                }
            }
        }
        var enabledChannelsBytes = new byte[6]; //通道启用字节
        baEnabledChannels.CopyTo(enabledChannelsBytes, 0);
        mcrParamBytes.AddRange(enabledChannelsBytes);
        //配置轮询开始秒数
        var singleSeconds =  mcrConfig.SingleCount / config.GetMeasFreq();//单通道采集时间
        var startSeconds = mcrConfig.StartSeconds + (terminalAddress - 1) * (config.UsChannelCount * singleSeconds + 10);
        var startSecondsBytes = BitConverter.GetBytes(Convert.ToUInt32(startSeconds));
        mcrParamBytes.AddRange(startSecondsBytes);
        //配置每天轮询次数
        var dailyCountBytes = BitConverter.GetBytes(mcrConfig.DailyCount);
        mcrParamBytes.AddRange(dailyCountBytes);
        //配置单轮单通道采集次数
        var singleCountBytes = BitConverter.GetBytes(mcrConfig.SingleCount);
        mcrParamBytes.AddRange(singleCountBytes);
        return mcrParamBytes;
    }
    public override IReadOnlyList<object> EncodeChannel(IoSession session, ChannelConfig config, string? propertyName = null)
    {
        var encodeMessages = new List<object>();
        var device = SessionContext.GetDeviceConfig(session);
        if (device != null)
        {
            #region 属性编码部分
            //探头频率编码
            var probeFreqKHz = Convert.ToUInt16(config.ProbeFreq * 1000);
            var probeFreqBytes = BitConverter.GetBytes(probeFreqKHz);
            //窗口时间编码
            var echoWinUs = Convert.ToUInt16(config.EchoWin / QUnit);
            config.EchoWin = echoWinUs * QUnit;
            var echoWinBytes = BitConverter.GetBytes(echoWinUs);
            //窗口宽度编码
            var echoWidthBytes = BitConverter.GetBytes(config.EchoWidth);
            //激励配置编码
            var pulseConfig = config.PulseConfig;
            var pulseBytes = new byte[2];
            pulseBytes[0] = pulseConfig.Duty; //低 字节 占空比
            pulseBytes[1] = Convert.ToByte((pulseConfig.Forward << 4) | pulseConfig.Reverse); //高字节(高 4 位正向 低 4 位反向)
            //增益编码
            var iGain = Convert.ToUInt16(config.Gain);
            var gainParamBytes = BitConverter.GetBytes(iGain);
            #endregion
            ushort echoWinStartAddress = 0x01; //窗口时间地址
            //构建写参数命令
            //复用窗口参数构建
            var echoWinParamConfig = new ParameterConfig(echoWinStartAddress, 1, echoWinBytes);
            var echoWinParamBytes = WrapCommand(device, config.Address, EncodeWriteParamCmd(config.Address,
                echoWinParamConfig,Convert.ToByte(config.Channel))); //
            if (!string.IsNullOrEmpty(propertyName))
            {
                if (device.TransferMode == 0)
                {
                    encodeMessages.Add(FxUtility.Delay500Ms);//loraMesh 设备加入等待
                }
                if (propertyName.Equals(nameof(config.ProbeFreq)))
                {
                    ushort startAddress = 0x00; //探头频率地址
                    //构建写参数命令
                    var probeFreqParamConfig = new ParameterConfig(startAddress, 1, probeFreqBytes);
                    encodeMessages.Add(WrapCommand(device, device.Address, EncodeWriteParamCmd(device.Address,
                        probeFreqParamConfig,Convert.ToByte(config.Channel)))); //
                }
                else if (propertyName.Equals(nameof(config.EchoWin)))
                {
                    encodeMessages.Add(echoWinParamBytes); //
                }
                else if (propertyName.Equals(nameof(config.EchoWidth)))
                {
                    ushort startAddress = 0x02; //回波窗口宽度地址
                    //构建写参数命令
                    var echoWidthParamConfig = new ParameterConfig(startAddress, 1, echoWidthBytes);
                    encodeMessages.Add(WrapCommand(device, config.Address, EncodeWriteParamCmd(config.Address,
                        echoWidthParamConfig,Convert.ToByte(config.Channel)))); //
                }
                else if (propertyName.Equals(nameof(config.PulseConfig)))
                {
                    ushort startAddress = 0x03; //激励脉冲地址
                    //构建写参数命令
                    var pulseParamConfig = new ParameterConfig(startAddress, 1, pulseBytes);
                    encodeMessages.Add(WrapCommand(device, config.Address, EncodeWriteParamCmd(config.Address,
                        pulseParamConfig,Convert.ToByte(config.Channel)))); //
                }
                else if (propertyName.Equals(nameof(config.Gain)))
                {
                    encodeMessages.Add(echoWinParamBytes); //调整增益时EchoWin 同步写窗口时间
                    ushort startAddress = 0x04; //增益地址
                    //构建写参数命令
                    var gainParamConfig = new ParameterConfig(startAddress, 1, gainParamBytes);
                    encodeMessages.Add(WrapCommand(device, config.Address, EncodeWriteParamCmd(config.Address,
                        gainParamConfig,Convert.ToByte(config.Channel)))); //
                }
                else if (propertyName.Equals(nameof(config.RefTime))) //配置初始化信息
                {
                    if (config.RefTime == null || config.LEchoWave == null || config.LEchoWave.WaveData == null)
                        return encodeMessages;
                    var refTemp = config.RefTemp ?? CalculateExtensions.StandardTemperature;
                    var storageBytes = new List<byte>();
                    //测量方法(1)-阈值/动态特征(2)-参考TOF(4-ns)+参考温度0.01°C(2)+波形起始时间(2)+参考波形(120*2)
                    var measByte = Convert.ToByte(config.MeasMode);
                    var measValBytes = BitConverter.GetBytes(config.Threshold);
                    var refTofBytes = BitConverter.GetBytes(Convert.ToUInt32(config.RefTime.Value));
                    var refTempBytes = BitConverter.GetBytes(Convert.ToInt16(refTemp * 100));
                    var echoStartBytes = BitConverter.GetBytes(Convert.ToUInt16(config.LEchoWave.EchoStart / 1000)); //us
                    storageBytes.Add(measByte);
                    storageBytes.AddRange(measValBytes);
                    storageBytes.AddRange(refTofBytes);
                    storageBytes.AddRange(refTempBytes);
                    storageBytes.AddRange(echoStartBytes);
                    var refEchoWave = config.LEchoWave;
                    var savePts = refEchoWave.WaveData.Select(o => (double)o).ToList();
                    if (refEchoWave.WaveData.Count > StorageMaxPts)
                    {
                        var waveDataDouble = refEchoWave.WaveData.Select(o => (double) o).ToList();
                        savePts = SignalProcessor.InterpolateAndResample(waveDataDouble, refEchoWave.ClockInt, OriginalInterval);
                    }
                    foreach (var ptData in savePts)
                    {
                        storageBytes.AddRange(BitConverter.GetBytes(Convert.ToInt16(ptData)));
                    }
                    encodeMessages.Add(WrapCommand(device, device.Address,EncodeWriteData(config.Address, Convert.ToByte(config.Channel), storageBytes.ToArray())));
                }
                else
                {
                    throw new ArgumentException(
                        "Invalid property name provided. Supported property names for configuration include: 'ProbeFreq', 'EchoWin', 'EchoWidth','PulseConfig', and 'Gain', and 'RefTime'."
                    );
                }
            }
            else //全部配置
            {
                if (device.TransferMode == 0)
                {
                    encodeMessages.Add(FxUtility.Delay500Ms);//loraMesh 设备加入等待
                }
                ushort startAddress = 0x00; //全部参数起始地址
                var channelBytes = new List<byte>();
                channelBytes.AddRange(probeFreqBytes);
                channelBytes.AddRange(echoWinBytes);
                channelBytes.AddRange(echoWidthBytes);
                channelBytes.AddRange(pulseBytes);
                channelBytes.AddRange(gainParamBytes);
                //构建写参数命令
                var channelParamConfig = new ParameterConfig(startAddress, 5, channelBytes.ToArray()); //5个参数同时写
                encodeMessages.Add(WrapCommand(device, device.Address,
                    EncodeWriteParamCmd(device.Address, channelParamConfig, Convert.ToByte(config.Channel)))); //
            }
        }
        return encodeMessages;
    }
    public override IReadOnlyList<object> EncodeWorkMode(IoSession session, WorkMode mode, ushort channel = 0, ushort address = 1)
    {
        var encodeMessages = new List<object>();
        var device = SessionContext.GetDeviceConfig(session);
        if (device != null)
        {
            if (mode != WorkMode.FullWaveRead)
            {
                lock (_fullWaveList)
                {
                    _fullWaveReadActive = false;
                    _fullWaveList.Clear();
                }
            }
            if (device.TransferMode == 0)
            {
                encodeMessages.Add(FxUtility.Delay500Ms);//loraMesh 设备加入等待
            }
            switch (mode)
            {
                case WorkMode.LWaveRead:
                case WorkMode.SingleMeas:
                    SessionContext.ClearPendingCommands(session, address);
                    var m1Parameter = new DeviceParameter(nameof(DeviceConfig.PendingCmdCount),0) {Address = address};
                    session.EnqueueDecodedResults(new DecodedResult[] {m1Parameter});
                    encodeMessages.Add(WrapCommand(device, address, BuildModeCommand(address, 0x02, new[] {(byte) channel})));
                    break;
                case WorkMode.ReadParam:
                    SessionContext.ClearPendingCommands(session, address);
                    var r1Parameter = new DeviceParameter(nameof(DeviceConfig.PendingCmdCount),0) {Address = address};
                    session.EnqueueDecodedResults(new DecodedResult[] {r1Parameter});
                    break;
                case WorkMode.MultiMeas:
                    //监测时需要多终端启动
                    foreach (var i in ResolveTerminalAddresses(device))
                    {
                        // 新模式开始前清理旧挂起命令，避免重启/扫波残留命令把设备拉回单通道流。
                        SessionContext.ClearPendingCommands(session, i);
                        if (device.TransferMode == 0)
                        {
                            encodeMessages.Add(FxUtility.Delay500Ms);//loraMesh 设备加入等待
                        }
                        var multiMeasCommand = WrapCommand(device, i, BuildModeCommand(i, 0x12));
                        encodeMessages.Add(multiMeasCommand);
                        //指令挂起
                        SessionContext.StoreToSession(session,multiMeasCommand,i);
                        var pendingCount = SessionContext.GetPendingCommands(session,i).Count;
                        var s2Parameter = new DeviceParameter(nameof(DeviceConfig.PendingCmdCount), pendingCount){Address = i};
                        session.EnqueueDecodedResults(new DecodedResult[] {s2Parameter});
                    }
                    break;
                case WorkMode.FullWaveRead:
                    lock (_fullWaveList)
                    {
                        _fullWaveList.Clear();
                        _fullWaveAddress = address;
                        _fullWaveChannel = channel;
                        _fullWaveReadActive = true;
                    }
                    SessionContext.ClearPendingCommands(session, address);
                    var s3Parameter = new DeviceParameter(nameof(DeviceConfig.PendingCmdCount),0) {Address = address};
                    session.EnqueueDecodedResults(new DecodedResult[] {s3Parameter});
                    encodeMessages.Add(WrapCommand(device, address,
                        BuildModeCommand(address, 0x04, new[] {(byte) channel})));
                    break;
                case WorkMode.Reboot:
                    SessionContext.ClearPendingCommands(session, address);
                    var s4Parameter = new DeviceParameter(nameof(DeviceConfig.PendingCmdCount),0) {Address = address};
                    session.EnqueueDecodedResults(new DecodedResult[] {s4Parameter});
                    encodeMessages.Add(WrapCommand(device, address, BuildModeCommand(address, 0x5A)));
                    break;
                case WorkMode.Standby:
                default:
                    if (channel > 0)//通道停止 只涉及当前终端
                    {
                        SessionContext.ClearPendingCommands(session, address);
                        var stop1CmdBytes = WrapCommand(device, address, BuildModeCommand(address, 0x11));
                        var stop2CmdBytes = WrapCommand(device, address, BuildModeCommand(address, 0x01));
                        if (device.TransferMode == 0)
                        {
                            encodeMessages.Add(FxUtility.Delay500Ms);//loraMesh 设备加入等待
                        }
                        encodeMessages.Add(stop1CmdBytes);
                        if (device.TransferMode == 0)
                        {
                            encodeMessages.Add(FxUtility.Delay500Ms);//loraMesh 设备加入等待
                        }
                        encodeMessages.Add(stop2CmdBytes);
                        //指令挂起
                        SessionContext.StoreToSession(session,stop1CmdBytes,address);//停止指令挂起，以便询问时下发
                        SessionContext.StoreToSession(session,stop2CmdBytes,address);
                        var pendingCount = SessionContext.GetPendingCommands(session,address).Count;
                        var s5Parameter = new DeviceParameter(nameof(DeviceConfig.PendingCmdCount),pendingCount) {Address = address};
                        session.EnqueueDecodedResults(new DecodedResult[] {s5Parameter});
                    }
                    else //监测停止 处理所有终端
                    {
                        foreach (var i in ResolveTerminalAddresses(device))
                        {
                            SessionContext.ClearPendingCommands(session, i);
                            if (device.TransferMode == 0)
                            {
                                encodeMessages.Add(FxUtility.Delay500Ms);//loraMesh 设备加入等待
                            }
                            var stop1CmdBytes = WrapCommand(device, i, BuildModeCommand(i, 0x11));
                            var stop2CmdBytes = WrapCommand(device, i, BuildModeCommand(i, 0x01));
                            encodeMessages.Add(stop1CmdBytes);
                            if (device.TransferMode == 0)
                            {
                                encodeMessages.Add(FxUtility.Delay500Ms);//loraMesh 设备加入等待
                            }
                            encodeMessages.Add(stop2CmdBytes);
                            SessionContext.StoreToSession(session,stop1CmdBytes,i);//停止指令挂起，以便询问时下发
                            SessionContext.StoreToSession(session,stop2CmdBytes,i);
                            var pendingCount = SessionContext.GetPendingCommands(session,i).Count;
                            var s6Parameter = new DeviceParameter(nameof(DeviceConfig.PendingCmdCount),pendingCount) {Address = i};
                            session.EnqueueDecodedResults(new DecodedResult[] {s6Parameter});
                        }
                    }
                    break;
            }
        }
        return encodeMessages;
    }

    private static IReadOnlyList<ushort> ResolveTerminalAddresses(DeviceConfig device)
    {
        var configuredAddresses = device.TerminalAddresses
            .Where(address => address > 0)
            .Distinct()
            .OrderBy(address => address)
            .ToArray();
        if (configuredAddresses.Length > 0)
        {
            return configuredAddresses;
        }

        return Enumerable.Range(1, Math.Max(1, (int)device.MaxTerminal))
            .Select(value => (ushort)value)
            .ToArray();
    }

    public override IReadOnlyList<object> EncodeReadDeviceParameter(IoSession session, string propertyName, ushort address = 1)
    {
        var encodeMessages = new List<object>();
        var device = SessionContext.GetDeviceConfig(session);
        if (device != null)
        {
            if (device.TransferMode == 0)
            {
                encodeMessages.Add(FxUtility.Delay500Ms);//loraMesh 设备加入等待
            }
            if (propertyName.Equals(nameof(DeviceConfig.Address))) //读站号
            {
                var readAddressParam = new ParameterConfig(0x00, 1, new byte[2]);
                encodeMessages.Add(WrapCommand(device,device.Address,EncodeReadParamCmd(BcAddress, readAddressParam)));//读站号用广播的方式
            }
            else if (propertyName.Equals(nameof(DeviceConfig.NetCheckConfig))) //读网络检查参数
            {
                var readNetCheckParam = new ParameterConfig(0x17, 2, new byte[6]);
                encodeMessages.Add(WrapCommand(device,device.Address,EncodeReadParamCmd(device.Address, readNetCheckParam)));//读网络检查参数
            }
            else if (propertyName.Equals(nameof(DeviceConfig.BatVoltage))) //读电量
            {
                var readBatVolParam = new ParameterConfig(0x13, 2, new byte[2]);
                encodeMessages.Add(WrapCommand(device,device.Address,EncodeReadParamCmd(device.Address, readBatVolParam)));//读网络检查参数
            }
        }
        return encodeMessages;
    }
    public override IReadOnlyList<object> EncodeReadChannelParameter(IoSession session, string propertyName, ushort channel = 1, ushort address = 1)
    {
        var encodeMessages = new List<object>();
        var device = SessionContext.GetDeviceConfig(session);
        if (device != null)
        {
            if (propertyName.Equals(nameof(ChannelConfig.ProbeFreq))) //读探头频率
            {
                var readProbeFreqConfig = new ParameterConfig(0x00, 1, new byte[1]);
                encodeMessages.Add(WrapCommand(device, device.Address,EncodeReadParamCmd(address, readProbeFreqConfig, Convert.ToByte(channel))));
            }
            if (propertyName.Equals(nameof(ChannelConfig.EchoWin))) //读窗口时间
            {
                var readEchoWidthConfig = new ParameterConfig(0x01, 1, new byte[1]);
                encodeMessages.Add(WrapCommand(device, device.Address,EncodeReadParamCmd(address, readEchoWidthConfig, Convert.ToByte(channel))));
            }
            if(propertyName.Equals(nameof(ChannelConfig.EchoWidth))) //读回波窗口宽度
            {
                var echoWidthConfig = new ParameterConfig(0x02, 1, new byte[2]);
                encodeMessages.Add(WrapCommand(device, device.Address,EncodeReadParamCmd(address, echoWidthConfig, Convert.ToByte(channel))));
            }
            if (propertyName.Equals(nameof(ChannelConfig.PulseConfig))) //读脉冲参数
            {
                var readPulseConfig = new ParameterConfig(0x03, 1, new byte[1]);
                encodeMessages.Add(WrapCommand(device, device.Address,EncodeReadParamCmd(address, readPulseConfig, Convert.ToByte(channel))));
            }
            if (propertyName.Equals(nameof(ChannelConfig.Gain))) //读增益
            {
                var readGainConfig = new ParameterConfig(0x04, 1, new byte[1]);
                encodeMessages.Add(WrapCommand(device, device.Address,EncodeReadParamCmd(address, readGainConfig, Convert.ToByte(channel))));
            }
            if (propertyName.Equals(ChannelRegistersParam)) //一帧读全部通道寄存器
            {
                var readAllConfig = new ParameterConfig(0x00, ChannelRegisterCount, new byte[1]);
                encodeMessages.Add(WrapCommand(device, device.Address,EncodeReadParamCmd(address, readAllConfig, Convert.ToByte(channel))));
            }
            
            if (propertyName.Equals(nameof(ChannelConfig.RefTime))) //初始化值
            {
                encodeMessages.Add(WrapCommand(device, device.Address, EncodeReadData(address, Convert.ToByte(channel))));
            }
        }
        return encodeMessages;
    }
    public override IReadOnlyList<object> EncodeOutputConfig(IoSession session, AnalogOutputConfig outputConfig, ushort channel = 1)
    {
        //忽略
        return new[] {FxUtility.Delay10Ms};
    }
    /// <summary>
    /// 构建设备模式命令
    /// </summary>
    private byte[] BuildModeCommand(ushort address, byte modeCommand, byte[]? additionalData = null)
    {
        var length = 2 + 1 + 2; // Address (2) + Command Code (1) + CRC (2) - Default Length
        if (additionalData != null)
        {
            length += additionalData.Length; //单通道模式及全波模式时增加通道号
        }

        var lengthBytes = BitConverter.GetBytes((ushort) (length | LenMask));
        var addressBytes = BitConverter.GetBytes(address);
        //帧头(4)+length(2)+address(2)+命令码(1)
        var writeBytes = new List<byte>(FrameHead);
        writeBytes.AddRange(new[] {lengthBytes[0], lengthBytes[1], addressBytes[0], addressBytes[1], modeCommand}); //

        if (additionalData != null) //+通道号(如果是单通道模式)
        {
            writeBytes.AddRange(additionalData);
        }

        var crcBytes = ByteUtility.CRC16_CCITT(writeBytes.ToArray());
        writeBytes.AddRange(crcBytes);
        writeBytes.AddRange(FrameTail);
        writeBytes.InsertRange(0, WakeupHead); //增加唤醒码
        return writeBytes.ToArray();
    }

    private const byte WriteParamCmdCode = 0x21; //写参数命令码
    private const byte ReadParamCmdCode = 0x22; //读参数命令码
    private const byte WriteStorageCmdCode = 0x41; //写存储命令码
    private const byte ReadStorageCmdCode = 0x42; //读存储命令码
    private const byte DefaultParamTarget = 0; //参数目标(默认设备参数目标码为 0)

    /// <summary>
    /// 编码写参数命令
    /// </summary>
    /// <param name="address">地址码</param>
    /// <param name="paramConfig">参数配置对象</param>
    /// <param name="paramTarget">参数目标码 0代表设备参数 1~48代表对应的通道参数</param>
    /// <returns>编码后的字节数组</returns>
    private byte[] EncodeWriteParamCmd(ushort address, ParameterConfig paramConfig, byte paramTarget = DefaultParamTarget)
    {
        var addressBytes = BitConverter.GetBytes(address);
        var paramLen = paramConfig.ParameterValues.Length;
        var totalLength =
            2 + 1 + 1 + 1 + 1 + paramLen + 2; // 地址码(2) + 命令码(1) + 参数目标码(1) + 开始地址(1) + 参数个数(1) + 参数字节码(N) + CRC(2)
        var lengthBytes = BitConverter.GetBytes((ushort) (totalLength | LenMask));

        var cmdBytes = new List<byte>(FrameHead)
        {
            lengthBytes[0], lengthBytes[1], addressBytes[0], addressBytes[1],
            WriteParamCmdCode, paramTarget, (byte) paramConfig.StartAddress, paramConfig.NumberOfParameters
        };
        cmdBytes.AddRange(paramConfig.ParameterValues); // 添加参数值

        var crcBytes = ByteUtility.CRC16_CCITT(cmdBytes.ToArray());
        cmdBytes.AddRange(crcBytes); //添加CRC校验字节

        cmdBytes.AddRange(FrameTail);
        cmdBytes.InsertRange(0, WakeupHead); // 插入唤醒字节
        return cmdBytes.ToArray();
    }
    
    /// <summary>
    /// 编码存储设备数据(sn 数据记录号)
    /// Id(2)+指令码(1)+记录号(1)+数据(N)+CRC(2) 
    /// </summary>
    private byte[] EncodeWriteData(ushort address, byte sn, byte[] dataBytes)
    {
        var addressBytes = BitConverter.GetBytes(address);
        var len = 2 + 1 + 1 + dataBytes.Length + 2;
        var lenBytes = BitConverter.GetBytes(Convert.ToUInt16(len | LenMask));
        var writeBytes = new List<byte>(FrameHead);
        writeBytes.AddRange(new[]
        {
            lenBytes[0], lenBytes[1], addressBytes[0], addressBytes[1], WriteStorageCmdCode,sn
        });
        writeBytes.AddRange(dataBytes);//加入自定义存储数据
        var crcBytes = ByteUtility.CRC16_CCITT(writeBytes.ToArray());
        writeBytes.AddRange(crcBytes);//加入CRC字节
        writeBytes.AddRange(FrameTail);
        writeBytes.InsertRange(0,WakeupHead);//插入唤醒字节
        return writeBytes.ToArray();
    }
    /// <summary>
    /// 编码读参数指令
    /// </summary>
    /// <param name="address">地址码</param>
    /// <param name="paramConfig">读参数配置</param>
    /// <param name="paramTarget">参数目标码 0代表设备参数 1~48代表对应的通道参数</param>
    private byte[] EncodeReadParamCmd(ushort address, ParameterConfig paramConfig, byte paramTarget = DefaultParamTarget)
    {
        var addressBytes = BitConverter.GetBytes(address);
        var totalLen = 2 + 1 + 1 + 1 + 1 + 2; //Id(2)+指令码(1)+通道0(1)+Reg启始(1)+Reg个数(1)+CRC(2) 设备参数通道号设置为0
        var lengthBytes = BitConverter.GetBytes(Convert.ToUInt16(totalLen | LenMask));
        var cmdBytes = new List<byte>(FrameHead)
        {
            lengthBytes[0], lengthBytes[1], addressBytes[0], addressBytes[1], ReadParamCmdCode, paramTarget,
            (byte) paramConfig.StartAddress, paramConfig.NumberOfParameters
        };
        var crcBytes = ByteUtility.CRC16_CCITT(cmdBytes.ToArray());
        cmdBytes.AddRange(crcBytes); //加入CRC字节
        cmdBytes.AddRange(FrameTail);
        cmdBytes.InsertRange(0, WakeupHead); //插入唤醒字节
        return cmdBytes.ToArray();
    }
    /// <summary>
    /// 编码读取设备数据命令(sn 数据记录号)
    /// Id(2)+指令码(1-0x42)+Sn(1-数据记录号)+CRC(2) 
    /// </summary>
    private byte[] EncodeReadData(ushort address, byte sn)
    {
        var addressBytes = BitConverter.GetBytes(address);
        var totalLen = 2 + 1  + 1 + 2;
        var lengthBytes = BitConverter.GetBytes(Convert.ToUInt16(totalLen | LenMask));
        var cmdBytes = new List<byte>(FrameHead)
        {
            lengthBytes[0], lengthBytes[1], addressBytes[0], addressBytes[1], ReadStorageCmdCode,sn
        };
        var crcBytes = ByteUtility.CRC16_CCITT(cmdBytes.ToArray());
        cmdBytes.AddRange(crcBytes);//加入CRC字节
        cmdBytes.AddRange(FrameTail);
        cmdBytes.InsertRange(0,WakeupHead);//插入唤醒字节
        return cmdBytes.ToArray();
    }
    /// <summary>
    /// 包装
    /// 0.Lora Mesh传输 增加地址和信道字节
    /// 
    /// </summary>
    private byte[] WrapCommand(DeviceConfig config, ushort address, byte[] dataBytes)
    {
        var wrapBytes = new List<byte>(dataBytes);
        if (config.TransferMode == 0) //lora传输模式
        {
            var loraCh = (byte) (address % LoraChMax); //信道计算
            var addrBytes = BitConverter.GetBytes(address);
            wrapBytes.InsertRange(0, new[] {addrBytes[1], addrBytes[0], loraCh});
        }
        return wrapBytes.ToArray();
    }
    #endregion
    #region 按照协议解码数据
    private string? _lastCrcMismatchSignature;
    private DateTime _lastCrcMismatchLogTime = DateTime.MinValue;

    protected override List<DecodedResult> DoDecode(IoSession session, CancellationToken token)
    {
        var decodedResults = new List<DecodedResult>();
        bool fullWaveReadActive;
        lock (_fullWaveList)
        {
            fullWaveReadActive = _fullWaveReadActive;
        }
        if (fullWaveReadActive)
        {
            decodedResults.AddRange(DecodeFullWaveResults(session, token));
            return decodedResults;
        }

        var deviceMode = SessionContext.GetDeviceMode(session);
        switch (deviceMode)
        {
            case WorkMode.LWaveRead:
            case WorkMode.SingleMeas:
            case WorkMode.MultiMeas:
            case WorkMode.ReadParam:
            default://保留时 解析数据帧并触发结果
                decodedResults.AddRange(DecodeResults(session, token,false));
                break;
            // case WorkMode.Standby:
            // default:
            //     ClearContext();//待机或者未知状态不处理数据
            //     break;
        }
        return decodedResults;
    }
    private int _headBeginIdx = -1; //动态解析头索引
    /// <summary>
    /// 解码结果
    /// </summary>
    private List<DecodedResult> DecodeResults(IoSession session, CancellationToken token, bool rawData = true)
    {
        var decodedList = new List<DecodedResult>(); //解码结果列表
        //最小数据帧长度 帧头(4) + 长度(2) + Id(2) + 指令码(1) + 通道/标识(1) + CRC(2)
        //帧尾不计入：否则一条最短的应答帧单独到达时会一直攒着不解，等到下一帧才被处理
        var minFrameSize = FrameHead.Length + 2 + 2 + 1 + 1 + 2;
        while (!token.IsCancellationRequested && ContextData.Count >= minFrameSize)
        {
            token.ThrowIfCancellationRequested(); 
            try
            {
                if (_headBeginIdx < 0) //如果之前还未找到帧头
                {
                    _headBeginIdx = ByteUtility.IndexOf(ContextData, FrameHead);
                    if (_headBeginIdx < 0)
                    {
                        var removeLength = ContextData.Count - FrameHead.Length; //保留帧头个数字节确保帧头不被清理
                        Logger.Warn(
                            "Multi-channel protocol discarded bytes without frame head. RemovedBytes={RemovedBytes}, BufferBytes={BufferBytes}, Preview={Preview}",
                            removeLength, ContextData.Count, FormatHexPreview(ContextData));
                        ConsumeContext(removeLength);
                        break; //找不到帧头，清理帧头前的无效字节，然后退出循环等待完整帧数据
                    }
                }

                //帧头已找到，清理帧头前面的无效字节
                if (_headBeginIdx > 0)
                {
                    ConsumeContext(_headBeginIdx);
                    _headBeginIdx = 0;
                }

                //如果找到了帧头，则继续长度字节的寻找
                if (ContextData.Count > (FrameHead.Length + 2)) //帧头(4)+长度(2)
                {
                    var lengthBytes = ContextData.Skip(FrameHead.Length).Take(2).ToArray();
                    var payloadLength = BitConverter.ToUInt16(lengthBytes) & 0x0FFF; //保留低12位的实际长度部分 有效信息长度
                    var frameSize = FrameHead.Length + 2 + payloadLength; //数据帧总长度 帧头(4) + 长度(2) + 有小信息长度
                    if (ContextData.Count >= frameSize) //检索到长度符合要求的数据帧
                    {
                        var decodedFrame = ContextData.Take(FrameHead.Length + 2 + payloadLength).ToList(); //(帧头4 + 长度2 )从长度字节后面开始
                        var crcBytes = decodedFrame.GetRange(0, decodedFrame.Count - 2); //需要进行校验的字节是从帧头到CRC
                        var crcCalcBytes = ByteUtility.CRC16_CCITT(crcBytes.ToArray());
                        var crcGetBytes = decodedFrame.GetRange(decodedFrame.Count - 2, 2);
                        if (crcCalcBytes.SequenceEqual(crcGetBytes)) //上下位机校验一致
                        {
                            var isComplete = false; //解码数据是否完整
                            try
                            {
                                var address = BitConverter.ToUInt16(new[] {decodedFrame[6], decodedFrame[7]});
                                var channel = decodedFrame[9];
                                switch (decodedFrame[8])
                                {
                                    case 0x82:
                                    case 0x92:
                                    case 0x84: //全波、多通道和单通道结果帧一致
                                        var chResult = DecodeChannelResult(decodedFrame, out isComplete); //解码通道结果
                                        if (isComplete)
                                        {
                                            chResult!.Channel = channel;
                                            chResult.Address = address;
                                            ProcessResult(session,chResult, rawData); //结果预处理
                                            decodedList.Add(chResult);
                                        }
                                        break;
                                    case 0xA2: //读参数回应
                                        var paramList = DecodeDeviceParameters(decodedFrame, out isComplete); //解码参数结果
                                        if (isComplete) //解码数据帧完整
                                        {
                                            decodedList.AddRange(paramList);
                                        }
                                        break;
                                    case 0xC2: //读存储回应
                                        var storageParamList = DecodeStorageParameters(decodedFrame, out isComplete); //解码参数结果
                                        if (isComplete) //解码数据帧完整
                                        {
                                            decodedList.AddRange(storageParamList);
                                        }
                                        break;
                                    case 0x33://状态帧参数指令
                                        var statusList = DecodeStatusParameters(decodedFrame, out isComplete); //解码状态帧参数结果
                                        if (isComplete) //解码数据帧完整
                                        {
                                            decodedList.AddRange(statusList);
                                        }
                                        break;
                                    case 0x32://询问指令,收到后下发挂起的指令
                                        isComplete = true;
                                        var pendingCommands = SessionContext.GetPendingCommands(session, address);
                                        while (pendingCommands.TryDequeue(out var command))
                                        {
                                            _ = session.SendMsgAsync(new List<object> { command });
                                            var devParameter = new DeviceParameter(nameof(DeviceConfig.PendingCmdCount),
                                                pendingCommands.Count) {Address = address};
                                            session.EnqueueDecodedResults(new DecodedResult[] {devParameter});
                                            FxUtility.Sleep(50);
                                        }
                                        break;
                                    case 0x81:
                                    case 0x91: //模式命令回应忽略，但必须消费完整帧，避免破坏后续帧头
                                        isComplete = true;
                                        break;
                                    case 0xA1: //写参数回应
                                    case 0xC1: //写存储回应
                                        isComplete = true;
                                        decodedList.Add(new DeviceParameter(WriteAckParam, decodedFrame[8])
                                        {
                                            Address = address, Channel = channel, ReceiveTime = DateTime.Now,
                                        });
                                        break;
                                }

                                if (isComplete)
                                {
                                    ConsumeContext(decodedFrame.Count);                     
                                    _headBeginIdx = -1;
                                }
                            }
                            catch (Exception)
                            {
                                Logger.Warn(
                                    "Multi-channel protocol frame decode failed. FrameBytes={FrameBytes}, Command=0x{Command:X2}, Address={Address}, Channel={Channel}, Preview={Preview}",
                                    decodedFrame.Count,
                                    decodedFrame.Count > 8 ? decodedFrame[8] : 0,
                                    decodedFrame.Count > 7 ? BitConverter.ToUInt16(new[] {decodedFrame[6], decodedFrame[7]}) : 0,
                                    decodedFrame.Count > 9 ? decodedFrame[9] : 0,
                                    FormatHexPreview(decodedFrame));
                                isComplete = false;
                            }
                            if (!isComplete) //收到错误数据帧
                            {
                                // CRC 已验证，帧边界可信；丢弃当前完整帧，避免重复解析。
                                ConsumeContext(decodedFrame.Count);
                                _headBeginIdx = -1;
                                continue;
                            }
                        }
                        else
                        {
                            var expectedCrc = ByteUtility.BytesToHexStr(crcCalcBytes);
                            var actualCrc = ByteUtility.BytesToHexStr(crcGetBytes.ToArray());
                            var preview = FormatHexPreview(decodedFrame);
                            var signature = $"{decodedFrame.Count}|{expectedCrc}|{actualCrc}|{preview}";
                            if (ShouldLogCrcMismatch(signature))
                            {
                                Logger.Warn(
                                    "Multi-channel protocol CRC mismatch. FrameBytes={FrameBytes}, Command=0x{Command:X2}, Address={Address}, Channel={Channel}, ExpectedCrc={ExpectedCrc}, ActualCrc={ActualCrc}, Preview={Preview}",
                                    decodedFrame.Count,
                                    decodedFrame.Count > 8 ? decodedFrame[8] : 0,
                                    decodedFrame.Count > 7 ? BitConverter.ToUInt16(new[] {decodedFrame[6], decodedFrame[7]}) : 0,
                                    decodedFrame.Count > 9 ? decodedFrame[9] : 0,
                                    expectedCrc,
                                    actualCrc,
                                    preview);
                            }
                            ResynchronizeAfterCorruptFrame();
                            continue;
                        }
                    }
                    else
                    {
                        break; //等待数据帧完整
                    }
                }
                else
                {
                    break; //等待数据帧完整
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(
                    ex,
                    "Multi-channel protocol decode loop failed. Resynchronizing. BufferBytes={BufferBytes}, Preview={Preview}",
                    ContextData.Count,
                    FormatHexPreview(ContextData));
                ResynchronizeAfterCorruptFrame();
                continue;
            }
        }
        return decodedList;
    }

    /// <summary>
    /// 坏帧长度字段也可能不可信，因此从当前帧头之后重新寻找下一帧头。
    /// 未找到完整帧头时只保留可能跨接收批次的帧头前缀。
    /// </summary>
    private void ResynchronizeAfterCorruptFrame()
    {
        var nextHeadIndex = ByteUtility.IndexOf(ContextData, FrameHead, 1);
        if (nextHeadIndex > 0)
        {
            ConsumeContext(nextHeadIndex);
        }
        else
        {
            var retainCount = Math.Min(FrameHead.Length - 1, ContextData.Count);
            ConsumeContext(ContextData.Count - retainCount);
        }
        _headBeginIdx = -1;
    }

    private bool ShouldLogCrcMismatch(string signature)
    {
        var now = DateTime.UtcNow;
        if (_lastCrcMismatchSignature == signature && now - _lastCrcMismatchLogTime < RepeatedFrameLogInterval)
        {
            return false;
        }

        _lastCrcMismatchSignature = signature;
        _lastCrcMismatchLogTime = now;
        return true;
    }

    private static string FormatHexPreview(IReadOnlyCollection<byte> bytes)
    {
        if (bytes.Count == 0)
        {
            return string.Empty;
        }

        var preview = bytes.Take(LogPreviewByteCount).ToArray();
        var suffix = bytes.Count > LogPreviewByteCount ? " ..." : string.Empty;
        return $"{ByteUtility.BytesToHexStr(preview)}{suffix}";
    }
    /// <summary>
    /// 通过波形计算结果
    /// </summary>
    private void ProcessResult(IoSession session, ChannelResult chResult, bool rawData = true)
    {
        var channelConfig = SessionContext.GetChannelConfig(session, chResult.Address, chResult.Channel);
        chResult.RawTemp = ResolveSourceChannelTemperature(session, chResult.Address, chResult.Channel, chResult.SpTemp);
        ResolveEffectiveChannelTemperature(session, chResult, channelConfig, sampleTime: chResult.ReceiveTime);
        // 动态特征法和阈值法仍要求配置有效探头频率后再做带通处理；自动法允许频率为 0，
        // 此时 BandPassFilter 只去除直流分量，再由主回波的稳定上升沿间距估算实际周期。
        var canProcessWithoutProbeFrequency = channelConfig?.MeasMode == 0;
        if (!rawData && channelConfig != null &&
            (channelConfig.ProbeFreq > 0 || canProcessWithoutProbeFrequency) &&
            chResult.EchoWave is {WaveData.Count: > 2}) 
        {
            var waveDataDouble = chResult.EchoWave?.WaveData!.Select(Convert.ToDouble).ToArray()!;
            var probeFreq = Convert.ToDouble(channelConfig.ProbeFreq * 1000000);
            //回波波形分析
            var waveDataList = SignalProcessor.BandPassFilter(waveDataDouble.ToList(), SampleRate, probeFreq);
            var interpolationSignal = SignalProcessor.InterpolateAndResample(waveDataList, OriginalInterval, InterpolationInterval);
            //计算回波时间
            EchoAssist assist;
            var zeroIdx = -1d;
            // 回波区域检测的最小连续宽度。这里是去除短尖峰的形态学门槛，
            // 不是“一个载波周期”；真实周期由自动法在主回波内实测或按探头频率解析。
            var consecutive = 20;
            EnvelopeTrackingContext? trackingContext = null;
            var isEnvelopeScan = false;
            if(channelConfig.MeasMode == 0) //自动法(显著回波法)
            {
                trackingContext = SessionContext.GetEnvelopeTrackingContext(session, chResult.Address, chResult.Channel);
                isEnvelopeScan = SessionContext.GetDeviceMode(session, chResult.Address) == WorkMode.LWaveRead;
                // 扫波必须重新捕获当前工件的 F0/Z0，不使用旧锚点；正式测量和监测优先使用
                // 本轮扫波锚点，没有运行期锚点时再使用置零后持久化的 RefEnvTime/RefTime。
                trackingContext.ReferenceEchoTime = isEnvelopeScan
                    ? null
                    : trackingContext.TrackingEchoTime ?? channelConfig.RefTime;
                trackingContext.ReferenceEnvTime = isEnvelopeScan
                    ? null
                    : trackingContext.TrackingEnvTime ?? channelConfig.RefEnvTime;
                // 扫波阶段每帧独立实测周期并形成中位周期；正式测量/监测优先使用
                // 本轮扫波的稳定周期，重启后再使用置零持久化的 RefPeriodNs。
                trackingContext.ReferencePeriodNs = isEnvelopeScan
                    ? null
                    : trackingContext.TrackingPeriodNs ?? channelConfig.RefPeriodNs;
                trackingContext.EchoStart = chResult.EchoWave!.EchoStart;
                trackingContext.ProbeFreqMhz = channelConfig.ProbeFreq;
                zeroIdx = CalculateExtensions.CalcZeroIndexByEnvelopeGuidedZero(interpolationSignal,
                    InterpolationInterval, out assist, consecutive, NoiseBase, trackingContext);
            }
            else if(channelConfig.MeasMode == 1)//动态特征法
            {
                zeroIdx = CalculateExtensions.CalcZeroIndexByEnv(interpolationSignal, InterpolationInterval, out assist,
                    consecutive, NoiseBase);
            }
            else//阈值法
            {
                zeroIdx = CalculateExtensions.CalcZeroIndexByThrSignal(interpolationSignal, InterpolationInterval, out assist,
                    NoiseBase, channelConfig.Threshold);
            }
            // 即使最终过零点未通过筛选，也保留主回波区域和 F/Fy 等诊断信息，
            // 配置界面可以明确区分“未找到主回波”和“已找到主回波但未找到合格过零点”。
            chResult.Assist = assist;
            if (zeroIdx >= 0)
            {
                var rawEchoTime = chResult.EchoWave!.EchoStart + (decimal) zeroIdx * InterpolationInterval;
                chResult.EchoTime = rawEchoTime;
                if (channelConfig.MeasMode == 0 && trackingContext != null && assist.Fx.HasValue)
                {
                    var hasPersistentAnchor = channelConfig.RefTime.HasValue && channelConfig.RefEnvTime.HasValue;
                    if (isEnvelopeScan || (!trackingContext.HasTrackingAnchor && !hasPersistentAnchor))
                    {
                        var envelopeTime = chResult.EchoWave.EchoStart + assist.Fx.Value * InterpolationInterval;
                        SessionContext.StoreEnvelopeTrackingAnchor(session, chResult.Address, chResult.Channel,
                            envelopeTime, rawEchoTime, assist.PeriodNs);
                        // 将当前最多 5 帧的中位周期带到 UI，置零保存的是稳定 T0，
                        // 而不是恰好最后一帧的单次估算值。
                        assist.PeriodNs = trackingContext.TrackingPeriodNs ?? assist.PeriodNs;
                    }
                }
                //小窗口在线中值滤波
                chResult.EchoTime = (decimal) GetMedianValue(session, chResult.Address, chResult.Channel, 
                    nameof(ChannelResult.EchoTime), (double) chResult.EchoTime!);
                chResult.DeltaTof = CalculateDeltaTof(channelConfig, (decimal) chResult.EchoTime,
                    chResult.SpTemp);
            }
            chResult.StatusCode = MeasStatusCodeExtensions.FromZeroIndex(zeroIdx);
            chResult.EchoWave!.WaveData =
                interpolationSignal.Select(Convert.ToInt16).ToList();
            chResult.EchoWave.ClockInt =
                Convert.ToUInt16(InterpolationInterval);
            chResult.EchoWave.Fc = Convert.ToUInt16(channelConfig.ProbeFreq);
            chResult.EchoWave.Thr = channelConfig.Threshold;
        }
    }
    /// <summary>
    /// 解码通道结果
    /// </summary>
    private ChannelResult? DecodeChannelResult(List<byte> decodedFrame, out bool isComplete)
    {
        isComplete = false;
        try
        {
            var payloadIdx = FrameHead.Length + 2 + 2 + 1 + 1; //有效数据开始索引
            var waveStartIdx = payloadIdx + 2 + 4 + 2 + 2 + 2;
            if (decodedFrame.Count > waveStartIdx)
            {
                var decodedTime = DateTime.Now;//使用上位机时间
                #region 时间解码(暂时不用)
                /*var secBytes = decodedFrame.GetRange(payloadIdx + 2, 4).ToArray();
                var msBytes = decodedFrame.GetRange(payloadIdx + 6, 2).ToArray();
                var recSec = (int) BitConverter.ToUInt32(secBytes) % 60; //获取秒 
                var recMilSec = (int) BitConverter.ToUInt16(msBytes); //获取毫秒 
                if (recSec is >= 0 and <= 60 && recMilSec is >= 0 and <= 999)
                {
                    decodedTime = new DateTime(decodedTime.Year, decodedTime.Month, decodedTime.Day,
                        decodedTime.Hour, decodedTime.Minute, recSec, recMilSec);
                }*/
                #endregion
                var ptsCount = (decodedFrame.Count - 2 - waveStartIdx) / 2;
                var waveData = new List<short>();
                for (var i = 0; i < ptsCount; i++)
                {
                    waveData.Add(BitConverter.ToInt16(new[]
                    {
                        decodedFrame[i * 2 + waveStartIdx],
                        decodedFrame[i * 2 + waveStartIdx + 1]
                    }));
                }
                //解码温度
                var spTemp = BitConverter.ToInt16(new[]
                {
                    decodedFrame[payloadIdx + 10],
                    decodedFrame[payloadIdx + 11]
                }) * 0.01M;
                //解码回波开始时间
                var echoStart = Convert.ToUInt32(BitConverter.ToUInt16(new[]
                {
                    decodedFrame[payloadIdx + 8], decodedFrame[payloadIdx + 9]
                }) * 1000);
                isComplete = true; //匹配到完整数据
                var channelResult = new ChannelResult
                {
                    HexString = BuildDiagnosticHexString(decodedFrame),
                    SpTemp = spTemp,
                    EchoWave = new EchoWave
                    {
                        EchoStart = echoStart,
                        ClockInt = Convert.ToUInt16(OriginalInterval),
                        WaveData = waveData, //原始数据
                    },
                    ReceiveTime = decodedTime,
                };
                return channelResult;
            }
        }
        catch (Exception){ /* ignore */ }
        return null;
    }
    /// <summary>
    /// 解码参数结果
    /// </summary>
    private List<DeviceParameter> DecodeDeviceParameters(List<byte> decodedFrame, out bool isComplete)
    {
        isComplete = false;
        var paramList = new List<DeviceParameter>();
        try
        {
            var address = BitConverter.ToUInt16(new[] {decodedFrame[6], decodedFrame[7]});
            var channel = decodedFrame[9];
            var payloadIdx = FrameHead.Length + 2 + 2 + 1 + 1; //有效数据开始索引
            var startAddress = decodedFrame[payloadIdx];
            if (channel > 0)//通道参数
            {
                //回应：起始地址(1)+寄存器个数(1)+每个寄存器 2 字节。个数字节只在与帧内实际字节数吻合时才采信，
                //否则按单寄存器解（老固件单读回应的个数字节不可靠时，不能把 CRC 解成下一个寄存器）。
                var valueBytes = decodedFrame.Count - 2 - (payloadIdx + 2);
                var countByte = decodedFrame[payloadIdx + 1];
                var count = countByte > 1 && valueBytes == countByte * 2 ? countByte : 1;
                var receiveTime = DateTime.Now;
                for (var i = 0; i < count; i++)
                {
                    var param = DecodeChannelRegister((byte)(startAddress + i), decodedFrame, payloadIdx + 2 + i * 2);
                    if (param == null) continue;
                    param.Channel = channel;
                    param.Address = address;
                    param.ReceiveTime = receiveTime;
                    paramList.Add(param);
                }
                isComplete = true;
            }
            else
            {
                if (startAddress == 0x00) //读终端地址码
                {
                    var addressBytes = decodedFrame.GetRange(payloadIdx + 2, 2).ToArray();
                    var addressParam = new DeviceParameter(nameof(DeviceConfig.Address),
                        BitConverter.ToUInt16(addressBytes, 0))
                    {
                        Address = address,ReceiveTime = DateTime.Now,
                    };
                    paramList.Add(addressParam);
                }
                else if (startAddress == 0x17)//读取网络参数
                {
                    var netCheckPeriodBytes = decodedFrame.GetRange(payloadIdx + 4, 4).ToArray();
                    var netCheckVal = new NetCheckConfig
                    {
                        Enabled = decodedFrame[payloadIdx + 3] == 1,
                        CheckPeriod = BitConverter.ToUInt32(netCheckPeriodBytes)
                    };
                    var netCheckParam = new DeviceParameter(nameof(DeviceConfig.NetCheckConfig), netCheckVal)
                    {
                        Address = address, ReceiveTime = DateTime.Now,
                    };
                    paramList.Add(netCheckParam);
                }
                else if (startAddress == 0x13) //读电池电量
                {
                    var batVolBytes = decodedFrame.GetRange(payloadIdx + 2, 2).ToArray();
                    var decBatVol = BitConverter.ToUInt16(batVolBytes, 0) / 1000M;
                    var batVolParam = new DeviceParameter(nameof(DeviceConfig.BatVoltage), decBatVol)
                    {
                        Address = address,ReceiveTime = DateTime.Now,
                    };
                    paramList.Add(batVolParam);
                }
                isComplete = true;
            }
        }
        catch (Exception)
        {
            //Console.WriteLine(ex);
        }
        return paramList;
    }
    /// <summary>
    /// 解码单个通道寄存器(2 字节)：0x00 探头频率、0x01 窗口时间、0x02 窗口宽度、0x03 脉冲、0x04 增益
    /// </summary>
    private static DeviceParameter? DecodeChannelRegister(byte register, List<byte> decodedFrame, int at)
    {
        var raw = BitConverter.ToUInt16(new[] {decodedFrame[at], decodedFrame[at + 1]}, 0);
        switch (register)
        {
            case 0x00:
                return new DeviceParameter(nameof(ChannelConfig.ProbeFreq), raw / 1000M);
            case 0x01:
                return new DeviceParameter(nameof(ChannelConfig.EchoWin), raw * 1000M); //to ns
            case 0x02:
                return new DeviceParameter(nameof(ChannelConfig.EchoWidth), raw); //us
            case 0x03:
                return new DeviceParameter(nameof(ChannelConfig.PulseConfig), new PulseConfig
                {
                    Duty = decodedFrame[at], //低字节 占空比
                    Forward = (byte) ((decodedFrame[at + 1] & 0xF0) >> 4), //高 4 位
                    Reverse = (byte) (decodedFrame[at + 1] & 0x0F), //低 4 位
                });
            case 0x04:
                return new DeviceParameter(nameof(ChannelConfig.Gain), raw);
            default:
                return null;
        }
    }
    /// <summary>
    /// 解码设备状态参数结果
    /// </summary>
    private List<DeviceParameter> DecodeStatusParameters(List<byte> decodedFrame, out bool isComplete)
    {
        isComplete = false;
        var paramList = new List<DeviceParameter>();
        try
        {
            var address = BitConverter.ToUInt16(new[] {decodedFrame[6], decodedFrame[7]});
            var statusKey = decodedFrame[9];
            var payloadIdx = FrameHead.Length + 2 + 2 + 1 + 1; //有效数据开始索引
            if (statusKey == 1)//电量状态
            {
                var batVolBytes = new[] {decodedFrame[payloadIdx], decodedFrame[payloadIdx + 1]};
                var batVol = BitConverter.ToUInt16(batVolBytes) / 1000M;//转换单位为v
                var batVolParam = new DeviceParameter(nameof(DeviceConfig.BatVoltage), batVol)
                {
                    Address = address,
                    ReceiveTime = DateTime.Now
                };
                paramList.Add(batVolParam);   
                isComplete = true;
            }
            else //重新上电状态
            {
                isComplete = true;
            }
        }
        catch (Exception)
        {
            //Console.WriteLine(ex);
        }
        return paramList;
    }
    /// <summary>
    /// 解码存储的配置结果
    /// </summary>
    private List<DeviceParameter> DecodeStorageParameters(List<byte> decodedFrame, out bool isComplete)
    {
        isComplete = false;
        var paramList = new List<DeviceParameter>();
        try
        {
            var receiveTime = DateTime.Now;
            var address = BitConverter.ToUInt16(new[] {decodedFrame[6], decodedFrame[7]});
            var sn = decodedFrame[9];//标识号
            var payloadIdx = FrameHead.Length + 2 + 2 + 1; //有效数据开始索引
            if (sn > 0)//通道存储参数
            {
                //测量方法(1)-阈值/位置(2)-参考TOF(4-ns)+参考温度0.01°C(2)+波形起始时间(2)+波形点数(2)+参考波形(120*2)
                var measMode = decodedFrame[payloadIdx + 1];//测量方式
                var thresholdBytes = decodedFrame.GetRange(payloadIdx + 2, 2).ToArray();
                var threshold = BitConverter.ToInt16(thresholdBytes, 0);//阈值
                var refTimeBytes = decodedFrame.GetRange(payloadIdx + 4, 4).ToArray();
                var refTime = BitConverter.ToUInt32(refTimeBytes, 0) * 1M;
                var refTempBytes = decodedFrame.GetRange(payloadIdx + 8, 2).ToArray();
                var refTemp = BitConverter.ToUInt16(refTempBytes, 0) * 0.01M;
                var echoStartBytes = decodedFrame.GetRange(payloadIdx + 10, 2).ToArray();
                var echoStartNs = Convert.ToUInt32(BitConverter.ToUInt16(echoStartBytes, 0) * 1000);
                var echoWave = new EchoWave
                {
                    EchoStart = echoStartNs,
                    WaveData = new List<short>()
                };
                var pointStart = payloadIdx + 12;
                var rawPts = new List<double>();
                for (var i = 0; i < StorageMaxPts; i++)
                {
                    var pointBytes = decodedFrame.GetRange(pointStart + i * 2, 2);
                    rawPts.Add(BitConverter.ToInt16(pointBytes.ToArray(), 0));
                }
                var echoPts = SignalProcessor.InterpolateAndResample(rawPts, OriginalInterval, InterpolationInterval);
                echoWave.WaveData.AddRange(echoPts.Select(Convert.ToInt16));
                echoWave.ClockInt = Convert.ToUInt16(InterpolationInterval);
                paramList.Add(new DeviceParameter(nameof(ChannelConfig.MeasMode), measMode)
                {
                    Channel = sn, Address = address, ReceiveTime = receiveTime,
                });
                paramList.Add(new DeviceParameter(nameof(ChannelConfig.Threshold),threshold)
                {
                    Channel = sn, Address = address, ReceiveTime = receiveTime,
                });
                paramList.Add(new DeviceParameter(nameof(ChannelConfig.RefTime),refTime)
                {
                    Channel = sn, Address = address, ReceiveTime = receiveTime,
                });
                paramList.Add(new DeviceParameter(nameof(ChannelConfig.RefTemp),refTemp)
                {
                    Channel = sn, Address = address, ReceiveTime = receiveTime,
                });
                paramList.Add(new DeviceParameter(nameof(ChannelConfig.LEchoWave),echoWave)
                {
                    Channel = sn, Address = address, ReceiveTime = receiveTime,
                });
                isComplete = true;
            }
        }
        catch (Exception)
        {
            //Console.WriteLine(ex);
        }
        return paramList;
    }
    /// <summary>
    /// 全波列表
    /// </summary>
    private readonly List<ChannelResult> _fullWaveList = new();
    private ushort _fullWaveAddress;
    private ushort _fullWaveChannel;
    private bool _fullWaveReadActive;
    /// <summary>
    /// 解码全波波形
    /// </summary>
    private List<ChannelResult> DecodeFullWaveResults(IoSession session, CancellationToken token)
    {
        var decodedList = new List<ChannelResult>();
        // 兼容现有固件：全波模式下 0x82/0x92/0x84 使用相同结果结构，
        // 再通过目标地址、通道和完整轮次规则排除模式切换时的迟到帧。
        var partList = DecodeResults(session, token);
        var wavePartList = partList.OfType<ChannelResult>().ToList();
        if (wavePartList.Count == 0)
        {
            return decodedList;
        }

        lock (_fullWaveList)
        {
            var targetParts = wavePartList
                .Where(r => r.Address == _fullWaveAddress && r.Channel == _fullWaveChannel)
                .ToList();
            foreach (var partResult in targetParts)
            {
                var echoStart = partResult.EchoWave!.EchoStart;
                if (_fullWaveList.Count > 0)
                {
                    var currentMaxEchoStart = _fullWaveList.Max(r => r.EchoWave!.EchoStart);
                    if (echoStart < currentMaxEchoStart)
                    {
                        // 起点回退表示新一轮开始。上一轮若未收到终止分段，直接丢弃，
                        // 不能把两轮数据混合后发布成一条看似完整的波形。
                        _fullWaveList.Clear();
                    }
                }

                if (_fullWaveList.Count == 0 && echoStart > QUnit)
                {
                    // 模式切换后迟到的中间包或尾包不能作为新一轮起点。
                    continue;
                }

                var existingPartIndex = _fullWaveList.FindIndex(r =>
                    r.EchoWave!.EchoStart == echoStart);
                if (existingPartIndex >= 0)
                {
                    _fullWaveList[existingPartIndex] = partResult;
                }
                else
                {
                    _fullWaveList.Add(partResult);
                }

                // 以连续分段实际覆盖到 EchoWin 作为本轮完成条件。
                // 短窗口可能一个分段就已覆盖完整，不能要求末段起点达到 EchoWin。
                var completedCurrentResult = TryBuildFullWaveResult(session);
                if (completedCurrentResult != null)
                {
                    decodedList.Add(completedCurrentResult);
                    _fullWaveList.Clear();
                }
            }
        }

        return decodedList;
    }

    private ChannelResult? TryBuildFullWaveResult(IoSession session)
    {
        if (_fullWaveList.Count == 0)
        {
            return null;
        }

        var orderedParts = _fullWaveList
            .OrderBy(r => r.EchoWave!.EchoStart)
            .ToList();
        var firstWave = orderedParts[0].EchoWave!;
        var clockInt = firstWave.ClockInt;
        if (clockInt == 0)
        {
            return null;
        }

        var lastPart = orderedParts[^1];
        var config = SessionContext.GetChannelConfig(session, lastPart.Address, lastPart.Channel);
        if (config is not {EchoWin: > 0, ProbeFreq: > 0})
        {
            return null;
        }

        uint coveredEndNs = 0;
        foreach (var part in orderedParts)
        {
            var wave = part.EchoWave!;
            if (wave.ClockInt != clockInt || wave.WaveData == null || wave.EchoStart % clockInt != 0)
            {
                return null;
            }

            // EchoStart 由设备按 1us 量化，允许相邻分段边界有一个量化单位内的空隙；
            // 更大的断档视为缺包，整轮不发布。
            if (wave.EchoStart > coveredEndNs + QUnit)
            {
                return null;
            }

            var waveDurationNs = checked((uint)wave.WaveData.Count * clockInt);
            coveredEndNs = Math.Max(coveredEndNs, checked(wave.EchoStart + waveDurationNs));
        }

        if (coveredEndNs <= config.EchoWin)
        {
            return null;
        }

        var fullWavePointCount = checked((int)Math.Ceiling((double)coveredEndNs / clockInt));
        var fullWaveArray = new short[fullWavePointCount];
        foreach (var part in orderedParts)
        {
            var wave = part.EchoWave!;
            var offset = (int)(wave.EchoStart / clockInt);
            wave.WaveData!.CopyTo(fullWaveArray, offset);
        }

        var fullWaveDouble = fullWaveArray.Select(Convert.ToDouble).ToList();
        fullWaveDouble = SignalProcessor.BandPassFilter(fullWaveDouble, SampleRate,
            Convert.ToDouble(config.ProbeFreq * 1000000));
        var fullWaveSignal = SignalProcessor.InterpolateAndResample(fullWaveDouble, OriginalInterval,
            InterpolationInterval);
        return new ChannelResult
        {
            Address = lastPart.Address,
            Channel = lastPart.Channel,
            ReceiveTime = lastPart.ReceiveTime,
            SpTemp = lastPart.SpTemp,
            EchoWave = new EchoWave
            {
                EchoStart = 0,
                ClockInt = Convert.ToUInt16(InterpolationInterval),
                WaveData = fullWaveSignal.Select(Convert.ToInt16).ToList(),
                Fc = Convert.ToUInt16(config.ProbeFreq),
            },
            EchoWaveKind = EchoWaveKind.LongitudinalFull,
        };
    }
    #endregion
}
