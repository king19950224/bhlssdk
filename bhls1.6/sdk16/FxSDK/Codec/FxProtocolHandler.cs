using System.Collections.Concurrent;
using FxSDK.Core;
using FxSDK.Models;
using MathNet.Filtering;
using MathNet.Filtering.Median;
using FxSDK.Utility;
using NLog;

namespace FxSDK.Codec;

/// <summary>
/// 超声设备抽象协议处理器
/// </summary>
public abstract class FxProtocolHandler : IDisposable
{
    private static readonly ILogger Logger = LogManager.GetLogger(nameof(FxProtocolHandler));
    /// <summary>
    /// 解码延迟时间（仅用于兼容旧字段，实际由 IoSession 控制轮询频率）
    /// </summary>
    protected int DelayMilliseconds { get; set; } = 10;

    /// <summary>
    /// 当前采集时间（如需跨调用保持状态，请使用外部上下文）
    /// </summary>
    protected DateTime? CurrentAcqTime = DateTime.Now;

    /// <summary>
    /// 采集时间锁
    /// </summary>
    protected readonly object AcqTimeLock = new();
    private readonly object _handlerLock = new();
    private readonly ConcurrentQueue<byte[]> _pendingBuffers = new();
    private readonly SemaphoreSlim _dataSignal = new(0, 1);
    private int _dataSignalPending;
    private int _pendingByteCount;
    private string? _lastDecodeWarningSignature;
    private DateTime _lastDecodeWarningLogTime = DateTime.MinValue;
    private static readonly TimeSpan DecodeWarningLogInterval = TimeSpan.FromSeconds(5);
    /// <summary>
    /// 解码上下文数据
    /// </summary>
    protected readonly List<byte> ContextData = new();
    /// <summary>
    /// 上下文数据最大缓存容量
    /// </summary>
    protected virtual int MaxContextBytes => 100_000;

    /// <summary>
    /// 将原始数据压入内部缓冲区（线程安全）
    /// </summary>
    public void Enqueue(byte[]? data)
    {
        if (data == null || data.Length == 0) return;
        Enqueue(data.AsSpan());
    }

    public void Enqueue(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;

        var buffer = data.ToArray();
        _pendingBuffers.Enqueue(buffer);
        Interlocked.Add(ref _pendingByteCount, buffer.Length);

        SignalDataAvailable();
    }

    internal async ValueTask WaitForDataAsync(CancellationToken token)
    {
        await _dataSignal.WaitAsync(token).ConfigureAwait(false);
        Interlocked.Exchange(ref _dataSignalPending, 0);
    }

    private void SignalDataAvailable()
    {
        if (Interlocked.Exchange(ref _dataSignalPending, 1) == 0)
        {
            _dataSignal.Release();
        }
    }

    /// <summary>
    /// 尝试从当前缓冲区中解码一批结果
    /// </summary>
    public List<DecodedResult> TryDecode(IoSession session, CancellationToken token)
    {
        lock (_handlerLock)
        {
            DrainPendingBuffers();
            return ContextData.Count > 0 ? DoDecode(session, token) : new List<DecodedResult>();
        }
    }

    /// <summary>
    /// 清空内部缓冲区
    /// </summary>
    internal void ClearContext()
    {
        lock (_handlerLock)
        {
            while (_pendingBuffers.TryDequeue(out var buffer))
            {
                Interlocked.Add(ref _pendingByteCount, -buffer.Length);
            }
            ContextData.Clear();
        }
    }

    private void DrainPendingBuffers()
    {
        var maxContextBytes = MaxContextBytes;
        while (_pendingBuffers.TryDequeue(out var data))
        {
            Interlocked.Add(ref _pendingByteCount, -data.Length);
            var beforeCount = ContextData.Count;
            ContextData.EnsureCapacity(Math.Min(maxContextBytes, ContextData.Count + data.Length));
            foreach (var b in data)
            {
                ContextData.Add(b);
            }

            if (ContextData.Count <= maxContextBytes)
            {
                continue;
            }

            var removeCount = ContextData.Count - maxContextBytes;
            ConsumeContext(removeCount);
            Logger.Warn(
                "Protocol buffer overflow. ReceivedBytes={ReceivedBytes}, PendingBytes={PendingBytes}, BufferBefore={BufferBefore}, RemovedBytes={RemovedBytes}, BufferAfter={BufferAfter}, MaxBytes={MaxBytes}",
                data.Length, Volatile.Read(ref _pendingByteCount), beforeCount, removeCount, ContextData.Count, maxContextBytes);
        }
    }

    /// <summary>
    /// 按协议解码从设备读取到的数据（子类实现）
    /// 注意：此方法在锁内调用，不要做耗时或异步操作
    /// </summary>
    protected abstract List<DecodedResult> DoDecode(IoSession session, CancellationToken token);

    protected void ConsumeContext(int count)
    {
        if (count <= 0) return;

        if (count >= ContextData.Count)
        {
            ContextData.Clear();
            return;
        }

        ContextData.RemoveRange(0, count);
    }

    protected static string? BuildDiagnosticHexString(IEnumerable<byte> bytes)
    {
        return FxUtility.DeveloperMode ? ByteUtility.BytesToHexStr(bytes) : null;
    }

    protected void LogDecodeWarning(IoSession session, string errorType, int headBeginIndex, Exception? exception = null)
    {
        var signature = $"{GetType().Name}|{session.SessionId}|{SessionContext.GetDeviceMode(session)}|{errorType}";
        var now = DateTime.UtcNow;
        if (_lastDecodeWarningSignature == signature && now - _lastDecodeWarningLogTime < DecodeWarningLogInterval)
        {
            return;
        }

        _lastDecodeWarningSignature = signature;
        _lastDecodeWarningLogTime = now;
        Logger.Warn(
            exception,
            "Protocol decode warning. Protocol={Protocol}, SessionId={SessionId}, Mode={Mode}, ErrorType={ErrorType}, BufferBytes={BufferBytes}, HeadIndex={HeadIndex}, Preview={Preview}",
            GetType().Name,
            session.SessionId,
            SessionContext.GetDeviceMode(session),
            errorType,
            ContextData.Count,
            headBeginIndex,
            FormatContextPreview());
    }

    private string FormatContextPreview()
    {
        if (ContextData.Count <= 0)
        {
            return string.Empty;
        }

        const int previewByteCount = 32;
        var previewCount = Math.Min(previewByteCount, ContextData.Count);
        var preview = ContextData.GetRange(0, previewCount);
        var suffix = ContextData.Count > previewByteCount ? " ..." : string.Empty;
        return $"{ByteUtility.BytesToHexStr(preview)}{suffix}";
    }

    // ===== 编码接口 =====
    public abstract IReadOnlyList<object> EncodeDevice(IoSession session, DeviceConfig config, string? propertyName = null);
    public abstract IReadOnlyList<object> EncodeChannel(IoSession session, ChannelConfig config, string? propertyName = null);
    public abstract IReadOnlyList<object> EncodeWorkMode(IoSession session, WorkMode mode, ushort channel = 0, ushort address = 1);
    public abstract IReadOnlyList<object> EncodeReadDeviceParameter(IoSession session, string propertyName, ushort address = 1);
    public abstract IReadOnlyList<object> EncodeReadChannelParameter(IoSession session, string propertyName, ushort channel = 1, ushort address = 1);
    public abstract IReadOnlyList<object> EncodeOutputConfig(IoSession session, AnalogOutputConfig outputConfig, ushort channel = 1);

    /// <summary>
    /// 获取小窗口中值（使用 SessionId 作为上下文 key）
    /// </summary>
    protected double GetMedianValue(IoSession session, ushort address, ushort channel, string propertyName, double value, int windowSize = 3)
    {
        var medianKey = $"Median|{address}-{channel}|{propertyName}";
        var onlineFilter = SessionContext.GetSessionAttribute<IOnlineFilter?>(session, medianKey);
        if (onlineFilter == null)
        {
            onlineFilter = new OnlineMedianFilter(windowSize);
            SessionContext.SetSessionAttribute(session, medianKey, onlineFilter);
        }
        return onlineFilter.ProcessSample(value);
    }
    /// <summary>
    /// 获取当前通道应使用的基础温度，会处理借用温度通道的情况。
    /// </summary>
    protected decimal? ResolveBaseChannelTemperature(IoSession session, ushort address, ushort channel)
    {
        var temp = SessionContext.GetChannelTemp(session, address, channel);
        var channelConfig = SessionContext.GetChannelConfig(session, address, channel);
        if (channelConfig is not {UseTempChannel: > 0})
        {
            return temp;
        }
        var useTempValue = SessionContext.GetChannelTemp(session, address, channelConfig.UseTempChannel);
        return useTempValue ?? temp;
    }

    /// <summary>
    /// 获取当前结果用于保护和平滑的来源温度。优先使用本帧解码温度，借用温度通道时优先使用已确认的通道温度。
    /// </summary>
    protected decimal? ResolveSourceChannelTemperature(IoSession session, ushort address, ushort channel,
        decimal? decodedTemperature = null,
        IReadOnlyDictionary<ushort, decimal>? frameTemperatures = null)
    {
        var channelConfig = SessionContext.GetChannelConfig(session, address, channel);
        if (channelConfig is { UseTempChannel: > 0 })
        {
            if (frameTemperatures != null &&
                frameTemperatures.TryGetValue(channelConfig.UseTempChannel, out var frameTemp))
            {
                return frameTemp;
            }
            return SessionContext.GetChannelTemp(session, address, channelConfig.UseTempChannel)
                   ?? decodedTemperature
                   ?? SessionContext.GetChannelTemp(session, address, channel);
        }

        if (decodedTemperature.HasValue)
        {
            return decodedTemperature.Value;
        }
        if (frameTemperatures != null && frameTemperatures.TryGetValue(channel, out var currentFrameTemp))
        {
            return currentFrameTemp;
        }
        return SessionContext.GetChannelTemp(session, address, channel);
    }

    /// <summary>
    /// 计算声时差。温度缺失时仍按参考温度计算，避免温度 N/A 阻断声时差结果。
    /// </summary>
    protected static decimal? CalculateDeltaTof(ChannelConfig? channelConfig, decimal echoTime, decimal? currentTemp)
    {
        if (channelConfig is not {RefTime: not null})
        {
            return null;
        }

        // 温度无效时仍输出声时差；此时相当于先不做有效温补。
        var refTemp = channelConfig.RefTemp ?? CalculateExtensions.StandardTemperature;
        var temp = currentTemp ?? refTemp;
        var tempCoefficient = channelConfig.TempCoefficient ?? CalculateExtensions.DefaultTempCoeff;
        return CalculateExtensions.CalcDeltaTof(
            channelConfig.RefTime.Value,
            refTemp,
            echoTime,
            temp,
            channelConfig.TcMode,
            tempCoefficient);
    }
    /// <summary>
    /// 解析当前结果的有效温度。RawTemp 为原始温度，SpTemp 为范围保护和 Tau 平滑后的业务温度。
    /// </summary>
    protected decimal? ResolveEffectiveChannelTemperature(IoSession session, ChannelResult result,
        ChannelConfig? channelConfig,
        IReadOnlyList<(ushort Address, ushort Channel, decimal Temperature)>? availableChannelTemps = null,
        DateTime? sampleTime = null)
    {
        if (!result.RawTemp.HasValue && result.SpTemp.HasValue)
        {
            result.RawTemp = result.SpTemp;
        }

        var sourceTemperature = result.RawTemp ?? result.SpTemp;
        if (!sourceTemperature.HasValue)
        {
            result.SpTemp = SessionContext.GetChannelTemp(session, result.Address, result.Channel);
            return result.SpTemp;
        }

        result.RawTemp ??= sourceTemperature.Value;
        if (IsTemperatureOutOfRange(sourceTemperature.Value))
        {
            result.SpTemp = ResolveFallbackChannelTemperature(session, result.Address, availableChannelTemps)
                                ?? SessionContext.GetChannelTemp(session, result.Address, result.Channel);
            return result.SpTemp;
        }

        var currentTime = sampleTime ?? result.ReceiveTime;
        if (currentTime == default)
        {
            currentTime = CurrentAcqTime ?? DateTime.Now;
        }

        var tauSeconds = FxUtility.ResolveTempTauSeconds(channelConfig?.TempTauSeconds);
        result.SpTemp = TemperatureStabilizer.ResolveTauTemp(session, result.Address, result.Channel,
            sourceTemperature.Value, currentTime, tauSeconds);
        SessionContext.StoreChannelTemp(session, result.Address, result.Channel, result.SpTemp.Value);
        return result.SpTemp;
    }

    /// <summary>
    /// 当前温度无效时，从同一会话的有效业务温度中选择代表值。
    /// </summary>
    private static decimal? ResolveFallbackChannelTemperature(IoSession session, ushort address,
        IReadOnlyList<(ushort Address, ushort Channel, decimal Temperature)>? availableChannelTemps)
    {
        var snapshots = availableChannelTemps ?? SessionContext.GetChannelTemps(session);
        if (snapshots.Count <= 0)
        {
            return null;
        }
        var sessionRepresentative = GetRepresentativeTemperature(snapshots.Select(s => s.Temperature).ToList());
        if (!sessionRepresentative.HasValue)
        {
            return null;
        }
        var sessionGroups = snapshots.GroupBy(s => s.Address)
            .ToDictionary(g => g.Key, g => g.Select(item => item.Temperature).ToList());
        var addressTemps = sessionGroups.GetValueOrDefault(address, new List<decimal>());
        var useAddressScope = addressTemps.Count > 3;
        var representativeTemp = useAddressScope
            ? GetRepresentativeTemperature(addressTemps) ?? sessionRepresentative
            : sessionRepresentative;
        return representativeTemp;
    }
    private static decimal? GetRepresentativeTemperature(IReadOnlyList<decimal> temperatures)
    {
        if (temperatures.Count <= 0)
        {
            return null;
        }
        var ordered = temperatures
            .Where(t => !IsTemperatureOutOfRange(t))
            .OrderBy(t => t)
            .ToList();
        if (ordered.Count <= 0)
        {
            return null;
        }
        var middle = ordered.Count / 2;
        if (ordered.Count % 2 == 1)
        {
            return ordered[middle];
        }
        return (ordered[middle - 1] + ordered[middle]) / 2M;
    }
    private static bool IsTemperatureOutOfRange(decimal temperature)
    {
        return temperature < FxUtility.MinTemp || temperature > FxUtility.MaxTemp;
    }
    public void Dispose()
    {
        ClearContext();
        _dataSignal.Dispose();
    }
}
