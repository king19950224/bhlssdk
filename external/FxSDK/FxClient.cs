using System.Collections.Concurrent;
using System.Net.Sockets;
using FxSDK.Codec;
using FxSDK.Core;
using FxSDK.Models;
using FxSDK.Utility;
using NLog;

namespace FxSDK;

public partial class FxClient
{
    private static readonly ILogger Logger = LogManager.GetLogger(nameof(TcpSession));
    #region 公共事件 
    /// <summary>
    /// 会话连接成功事件
    /// <p>事件参数包含连接建立的时间（<see cref="DateTime"/>）</p>
    /// </summary>
    public event EventHandler? OnConnected;
    /// <summary>
    /// 会话连接断开事件
    /// <p>事件参数包含断开原因等</p>
    /// </summary>
    public event EventHandler<SessionDisconnectedEventArgs>? OnDisconnected;
    /// <summary>
    /// 会话收到解码结果事件
    /// <p>事件参数包含解码结果数组（<see cref="DecodedResult"/>）</p>
    /// </summary>
    public event EventHandler<DecodedResultsReceivedEventArgs>? OnResultReceived;
    #endregion
    /// <summary>
    /// AGC上下文控制
    /// </summary>
    private readonly ConcurrentDictionary<string,AgcContext> _sessionAgcContext = new ();
    /// <summary>
    /// SDK client实例
    /// </summary>
    public FxClient(){ }
    /// <summary>
    /// 异步连接指定配置的设备。
    /// </summary>
    /// <param name="config">
    /// 设备连接配置对象，包含以下关键信息：
    /// <para>- <b>设备类型</b>：支持“3.多通道监测版”或“2.实时24通道版”等设备型号；</para>
    /// <para>- <b>通讯方式</b>：0 表示串口通信，1 表示 TCP 客户端模式（SDK 作为 Client）。TCP Server 模式请使用 <c>FxSDK.Server.FxServer</c>。</para>
    /// </param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="ArgumentNullException">当 <paramref name="config"/> 为 null 时抛出。</exception>
    /// <exception cref="ArgumentException">当 <paramref name="config"/> 中包含无效的设备类型或通讯方式时抛出。</exception>
    /// <remarks>
    /// 该方法不会阻塞调用线程，应使用 <c>await</c> 等待连接完成。
    /// </remarks>
    public async Task ConnectAsync(DeviceConfig config, CancellationToken cancellationToken = default)
    {
        try
        {
            await OpenConnectionAsync(config, cancellationToken);
        }
        catch (Exception ex)
        {
            throw ex.ToCommunicationException(config.CommunicationParameters);
        }
    }
    /// <summary>
    /// 异步断开指定会话的连接并释放相关资源。
    /// </summary>
    /// <param name="sessionId">要关闭的会话唯一标识符。</param>
    /// <exception cref="CommunicationException">
    /// 当会话不存在或关闭过程中发生通信错误时抛出。
    /// </exception>
    /// <remarks>
    /// 调用此方法后，该会话将不再接收数据，且无法再次使用。
    /// 若传入的 <paramref name="sessionId"/> 不存在，则方法静默返回（不抛异常）。
    /// </remarks>
    public async Task CloseAsync(string sessionId)
    {
        try
        {
            await CloseSessionAsync(sessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw ex.ToCommunicationException(ex.Message);
        }
    }

    public async Task CloseFastAsync(string sessionId)
    {
        try
        {
            await CloseSessionAsync(sessionId, fastClose: true, delayAfterClose: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw ex.ToCommunicationException(ex.Message);
        }
    }
    /// <summary>
    /// 异步配置设备的基本参数。
    /// </summary>
    /// <param name="sessionId">
    /// 目标会话的唯一标识符。
    /// </param>
    /// <param name="config">
    /// 包含设备配置信息的对象，如测量频率、采样深度等。
    /// </param>
    /// <param name="propertyName">
    /// 可选。若指定，则仅更新并发送该属性对应的配置指令；若为 <see langword="null"/>，则发送完整配置。
    /// </param>
    /// <param name="sendCommand">
    /// 指示是否立即将配置指令发送至设备。若为 <see langword="false"/>，仅更新本地会话缓存。
    /// </param>
    /// <exception cref="CommunicationException">
    /// 当会话不存在或发送指令过程中发生通信错误时抛出。
    /// </exception>
    /// <remarks>
    /// 配置会先存储到会话上下文中，再根据 <paramref name="sendCommand"/> 决定指令是否下发。
    /// </remarks>
    public async Task ConfigureDeviceAsync(string sessionId, DeviceConfig config, string? propertyName = null, bool sendCommand = true)
    {
        try
        {
            var session = GetSession(sessionId);
            if(session == null) 
                return;
            //更新配置到会话
            SessionContext.StoreToSession(session,config);
            //写入设备
            if (sendCommand)
            {
                await session.SendMsgAsync(session.ProtocolHandler.EncodeDevice(session, config, propertyName));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ex.ToCommunicationException(ex.Message);
        }
    }
    /// <summary>
    /// 异步配置超声通道的参数（如增益、滤波、频率等）。
    /// </summary>
    /// <param name="sessionId">
    /// 目标会话的唯一标识符。
    /// </param>
    /// <param name="config">
    /// 超声通道配置对象，包含增益、带宽等参数。
    /// </param>
    /// <param name="propertyName">
    /// 可选。若指定，则仅更新并发送该属性对应的通道配置指令；若为 <see langword="null"/>，则发送完整配置。
    /// </param>
    /// <param name="sendCommand">
    /// 指示是否立即将配置指令发送至设备。若为 <see langword="false"/>，仅更新本地会话缓存。
    /// </param>
    /// <exception cref="CommunicationException">
    /// 当会话不存在或发送指令过程中发生通信错误时抛出。
    /// </exception>
    /// <remarks>
    /// 该方法支持细粒度更新，适用于动态调整单个通道参数（如 AGC 过程中调整增益）。
    /// </remarks>
    public async Task ConfigureChannelAsync(string sessionId, ChannelConfig config, string? propertyName = null, bool sendCommand = true)
    {
        try
        {
            var session = GetSession(sessionId);
            if(session == null) 
                return;
            //存储到会话对象
            SessionContext.StoreToSession(session,config);
            //发送指令到设备
            if (sendCommand)
            {
                await session.SendMsgAsync(session.ProtocolHandler.EncodeChannel(session, config, propertyName));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ex.ToCommunicationException(ex.Message);
        }
    }

    public void ClearEnvelopeTracking(string sessionId, ushort? address = null, ushort? channel = null)
    {
        var session = GetSession(sessionId);
        if (session == null)
        {
            return;
        }
        if (address.HasValue && channel.HasValue)
        {
            SessionContext.ClearEnvelopeTrackingContext(session, address.Value, channel.Value);
            return;
        }
        SessionContext.ClearEnvelopeTrackingContexts(session, address);
    }

    /// <summary>
    /// 启动自动增益控制（AGC）流程，自动调节通道增益以达到目标峰值。
    /// </summary>
    /// <param name="sessionId">
    /// 目标会话的唯一标识符。
    /// </param>
    /// <param name="config">
    /// 当前通道配置，用于提取初始增益并构造调节指令。
    /// </param>
    /// <param name="targetPeak">
    /// 期望达到的信号峰值（单位：dB 或归一化幅度，具体取决于设备协议）。
    /// </param>
    /// <param name="gainRange">
    /// 增益调节的有效范围，格式为 <c>[min, max]</c>。
    /// </param>
    /// <param name="token">外部取消令牌</param>
    /// <param name="maxIterations">
    /// 最大迭代次数，默认为 20 次。
    /// </param>
    /// <param name="gainStep">
    /// 增益步长
    /// </param>
    /// <returns>
    /// 返回包含最终增益值、是否收敛、迭代次数等信息的 <see cref="AgcResult"/> 对象。
    /// </returns>
    /// <exception cref="CommunicationException">
    /// 当会话不存在、通信失败或 AGC 流程异常中断时抛出。
    /// </exception>
    /// <remarks>
    /// 该方法内部会取消同一会话上正在进行的其他 AGC 任务。
    /// AGC 过程依赖 <see cref="OnResultReceived"/> 事件反馈的解码结果来计算当前峰值。
    /// </remarks>
    public async Task<AgcResult?> RunAgcControlAsync(string sessionId, ChannelConfig config, decimal targetPeak, double[] gainRange
       , decimal gainStep , CancellationToken token, int maxIterations = 20)
    {
        var session = GetSession(sessionId);
        if (session == null) 
            return null;
        
        // 从当前增益开始搜索；AGC 内部会按设备范围夹逼，避免每次都从最小值重新爬坡。
        config.Gain = ClampGain(config.Gain, gainRange);
        var agcContext = new AgcContext(config.Gain, (double)targetPeak, gainRange, token, gainStep, maxIterations);
        AgcResult? result = null;
        try
        {
            // 取消旧的 AGC（如果存在）
            if (_sessionAgcContext.TryGetValue(sessionId, out var oldContext))
            {
                oldContext.Cancel(); //
            }
            // 注册新的 AGC 上下文
            _sessionAgcContext.AddOrUpdate(sessionId, agcContext, (_, _) => agcContext);
            var stableDelayMs = ResolveAgcSettleDelayMs(session.ProtocolHandler); // 等待增益指令生效后的新回波
            var waveTimeout = TimeSpan.FromMilliseconds(stableDelayMs + 1200);
            var nextGain = agcContext.Gain;
            for (var i = 1; i <= maxIterations; i++)
            {
                // 检查是否已被取消
                if (token.IsCancellationRequested)
                {
                    agcContext.Cancel();
                    break;
                }
                
                // 检查是否已内部取消
                if (agcContext.Status != AgcContext.AgcStatus.Running)
                    break;

                try
                {
                    token.ThrowIfCancellationRequested();
                    // 更新设备增益
                    config.Gain = nextGain;
                    await session.SendMsgAsync(session.ProtocolHandler.EncodeChannel(session, config, nameof(ChannelConfig.Gain)));
                    token.ThrowIfCancellationRequested();
                    agcContext.BeginGainProbe(TimeSpan.FromMilliseconds(stableDelayMs));
                    agcContext.Gain = nextGain;
                    // 等待并处理下一轮回波数据
                    nextGain = await agcContext.WaitNextGainAsync(waveTimeout, TimeSpan.Zero);
                    //检查状态决定是否继续(收敛成功、达到最大迭代次数以及超时取消等都退出)
                    if (agcContext.Status != AgcContext.AgcStatus.Running)
                    {
                        break; // AGC 已完成或终止
                    }
                }
                catch (OperationCanceledException)
                {
                    // 被取消，退出循环
                    agcContext.Cancel();
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error during AGC iteration in RunAgcControlAsync");
                    agcContext.Cancel(); // 标记为取消/错误
                    break;
                }
            }
            agcContext.CompleteIfStillRunning();
            // 开发者模式记录 AGC 最终结果
            if (FxUtility.DeveloperMode)
            {
                Logger.Info(
                    $"AGC finished for session {sessionId}: Status={agcContext.Status}, FinalGain={agcContext.Gain}, Iterations={agcContext.FinalIterations}");
            }

            if (agcContext.Status != AgcContext.AgcStatus.Cancelled && config.Gain != agcContext.Gain)
            {
                // 二分搜索最终可能选中历史最优点，结束前把设备增益切回该点。
                config.Gain = agcContext.Gain;
                await session.SendMsgAsync(session.ProtocolHandler.EncodeChannel(session, config, nameof(ChannelConfig.Gain)));
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to initialize or run AGC for session {sessionId}");
            agcContext.Cancel();
        }
        finally
        {
            result = agcContext.ToResult();
            // 只移除当前 AGC，避免旧任务结束时误删同一会话上的新 AGC。
            ((ICollection<KeyValuePair<string, AgcContext>>)_sessionAgcContext)
                .Remove(new KeyValuePair<string, AgcContext>(sessionId, agcContext));
            agcContext.Dispose();
        }
        return result;
    }

    private static decimal ClampGain(decimal gain, double[] gainRange)
    {
        var minGain = (decimal)gainRange[0];
        var maxGain = (decimal)gainRange[1];
        if (gain < minGain)
        {
            return minGain;
        }

        return gain > maxGain ? maxGain : gain;
    }

    private static int ResolveAgcSettleDelayMs(FxProtocolHandler protocolHandler)
    {
        return protocolHandler is MultiChIotProtocolHandler ? 350 : 250;
    }
    /// <summary>
    /// 异步配置设备的模拟输出通道参数。
    /// </summary>
    /// <param name="sessionId">
    /// 目标会话的唯一标识符。
    /// </param>
    /// <param name="outputConfig">
    /// 模拟输出配置，包括输出类型、量程等。
    /// </param>
    /// <param name="outputChannel">
    /// 模拟输出通道编号（从 1 开始，具体依设备而定）。
    /// </param>
    /// <exception cref="CommunicationException">
    /// 当会话不存在或发送配置指令失败时抛出。
    /// </exception>
    /// <remarks>
    /// 该功能仅在支持模拟输出的设备型号上有效。
    /// </remarks>
    public async Task ConfigureAnalogOutputAsync(string sessionId, AnalogOutputConfig outputConfig, ushort outputChannel)
    {
        try
        {
            var session = GetSession(sessionId);
            if(session == null) return;
            //发送指令到设备
            await session.SendMsgAsync(session.ProtocolHandler.EncodeOutputConfig(session, outputConfig, outputChannel));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ex.ToCommunicationException(ex.Message);
        }
    }
    /// <summary>
    /// 设置设备的工作模式（如采集、待机、多通道监测等）。
    /// </summary>
    /// <param name="sessionId">
    /// 目标会话的唯一标识符。
    /// </param>
    /// <param name="mode">
    /// 要设置的设备工作模式。
    /// </param>
    /// <param name="channel">
    /// 可选。某些模式需指定物理通道号（如单通道采集模式）。
    /// </param>
    /// <param name="address">
    /// 终端设备地址，用于多设备组网场景，默认为 1。
    /// </param>
    /// <exception cref="CommunicationException">
    /// 当会话不存在或发送模式切换指令失败时抛出。
    /// </exception>
    /// <remarks>
    /// 模式切换可能影响设备数据流状态，请确保在安全时机调用。
    /// </remarks>
    public async Task SetDeviceModeAsync(string sessionId, WorkMode mode, ushort channel = 0, ushort address = 1)
    {
        try
        {
            var session = GetSession(sessionId);
            if(session == null) return;
            if (mode == WorkMode.LWaveRead && channel > 0)
            {
                SessionContext.BeginEnvelopeScan(session, address, channel);
            }
            // 按地址保存工作模式，多终端设备解码时需要使用对应终端的模式上下文。
            SessionContext.StoreToSession(session, mode, address);
            //发送指令到设备
            await session.SendMsgAsync(session.ProtocolHandler.EncodeWorkMode(session, mode, channel, address));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ex.ToCommunicationException(ex.Message);
        }
    }
    /// <summary>
    /// 请求读取设备的某个全局参数（如序列号、固件版本、采样率等）。
    /// </summary>
    /// <param name="sessionId">
    /// 目标会话的唯一标识符。
    /// </param>
    /// <param name="propertyName">
    /// 要读取的参数名称（需与设备协议定义一致）。
    /// </param>
    /// <param name="address">
    /// 终端设备地址，用于多设备组网场景，默认为 1。
    /// </param>
    /// <exception cref="CommunicationException">
    /// 当会话不存在或发送读取请求失败时抛出。
    /// </exception>
    /// <remarks>
    /// 读取结果将通过 <see cref="OnResultReceived"/> 事件异步返回。
    /// 请确保设备支持该参数的读取操作。
    /// </remarks>
    public async Task ReadDeviceParamAsync(string sessionId, string propertyName, ushort address = 1)
    {
        try
        {
            var session = GetSession(sessionId);
            if(session == null) 
                return;
            //存储到会话对象
            SessionContext.StoreToSession(session,propertyName);
            SessionContext.StoreToSession(session, WorkMode.ReadParam, address);//
            //发送指令到设备
            await session.SendMsgAsync(session.ProtocolHandler.EncodeReadDeviceParameter(session, propertyName, address));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ex.ToCommunicationException(ex.Message);
        }
    }
    /// <summary>
    /// 请求读取指定通道的某个参数（如当前增益、滤波设置等）。
    /// </summary>
    /// <param name="sessionId">
    /// 目标会话的唯一标识符。
    /// </param>
    /// <param name="propertyName">
    /// 要读取的通道参数名称（需与设备协议定义一致）。
    /// </param>
    /// <param name="channel">
    /// 物理通道号（从 1 开始），默认为 1。
    /// </param>
    /// <param name="address">
    /// 终端设备地址，用于多设备组网场景，默认为 1。
    /// </param>
    /// <exception cref="CommunicationException">
    /// 当会话不存在或发送读取请求失败时抛出。
    /// </exception>
    /// <remarks>
    /// 读取结果将通过 <see cref="OnResultReceived"/> 事件异步返回。
    /// </remarks>
    public async Task ReadChannelParamAsync(string sessionId, string propertyName, ushort channel = 1 , ushort address = 1)
    {
        try
        {
            var session = GetSession(sessionId); 
            if(session == null) return;
            //存储到会话对象
            SessionContext.StoreToSession(session,propertyName);
            //发送指令到设备
            await session.SendMsgAsync(session.ProtocolHandler.EncodeReadChannelParameter(session, propertyName, channel, address));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ex.ToCommunicationException(ex.Message);
        }
    }
    /// <summary>
    /// 直接向设备发送原始字节数组数据。
    /// </summary>
    /// <param name="sessionId">
    /// 目标会话的唯一标识符。
    /// </param>
    /// <param name="data">
    /// 要发送的原始字节数据，必须符合设备通信协议格式。
    /// </param>
    /// <exception cref="CommunicationException">
    /// 当会话不存在或底层发送失败时抛出。
    /// </exception>
    /// <remarks>
    /// 此方法绕过协议编码层，适用于调试或发送自定义指令。
    /// 使用时需确保 <paramref name="data"/> 的合法性，否则可能导致设备异常。
    /// </remarks>
    public async Task SendDataAsync(string sessionId, byte[] data)
    {
        try
        {
            var session = GetSession(sessionId);
            if(session == null) return;
            await session.SendMsgAsync(new[] {data});
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ex.ToCommunicationException(ex.Message);
        }
    }
    /// <summary>
    /// 关闭所有活动会话，彻底清理 SDK 资源。
    /// </summary>
    /// <exception cref="CommunicationException">
    /// 在关闭任一会话过程中发生通信错误时抛出。
    /// </exception>
    /// <remarks>
    /// 调用此方法后，当前 <see cref="FxClient"/> 实例将不可再用。
    /// 如需继续使用，必须创建新的 <see cref="FxClient"/> 实例。
    /// 该操作不可逆，请谨慎调用。
    /// </remarks>
    public async Task StopAllAsync()
    {
        // 关闭所有会话
        foreach (var session in _sessions.Values.ToList())
        {
            try
            {
                UnSubscribeSessionEvents(session);
                await session.CloseAsync();
            }
            catch (Exception ex)
            {
                throw ex.ToCommunicationException(ex.Message);
            }
        }
        _sessions.Clear();
    }
    /// <summary>
    /// 检查指定会话是否处于已连接状态。
    /// </summary>
    /// <param name="sessionId">要检查的会话唯一标识符。</param>
    /// <returns>
    /// 如果存在对应会话且其连接有效，则返回 <see langword="true"/>；否则返回 <see langword="false"/>。
    /// </returns>
    /// <remarks>
    /// 此方法仅检查会话是否存在于内部会话字典中，并不验证底层物理连接是否仍活跃。
    /// 对于 TCP 连接，可能存在“已断开但未清理”的情况，因此建议结合心跳机制判断真实连接状态。
    /// </remarks>
    public bool IsConnected(string sessionId)
    {
        var session = GetSession(sessionId);
        return session is {IsConnected: true};
    }

    /// <summary>
    /// 获取设备配置对应的会话标识。
    /// </summary>
    public string GetSessionId(DeviceConfig config)
    {
        return config.GetSessionId();
    }

    /// <summary>
    /// 检查指定设备配置对应的会话是否已连接。
    /// </summary>
    public bool IsDeviceConnected(DeviceConfig config)
    {
        return IsConnected(GetSessionId(config));
    }

    /// <summary>
    /// 关闭指定设备配置对应的会话。
    /// </summary>
    public async Task CloseDeviceAsync(DeviceConfig config)
    {
        await CloseAsync(GetSessionId(config));
    }

    public async Task CloseDeviceFastAsync(DeviceConfig config)
    {
        await CloseFastAsync(GetSessionId(config));
    }
}
