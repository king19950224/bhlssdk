using FxSDK.Core;
using FxSDK.Models;
using MathNet.Filtering;
using MathNet.Filtering.Median;

namespace FxSDK.Codec;

/// <summary>
/// 超声设备抽象协议处理器
/// </summary>
public abstract class FxProtocolHandler : IDisposable
{
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
    /// <summary>
    /// 解码上下文数据
    /// </summary>
    protected readonly List<byte> ContextData = new();
    /// <summary>
    /// 上下文数据最大缓存容量
    /// </summary>
    private const int ContextMaxCount = 100_000;

    /// <summary>
    /// 将原始数据压入内部缓冲区（线程安全）
    /// </summary>
    public void Enqueue(byte[]? data)
    {
        if (data == null || data.Length == 0) return;

        lock (_handlerLock)
        {
            ContextData.AddRange(data);
            if (ContextData.Count > ContextMaxCount)
            {
                var removeCount = ContextData.Count - ContextMaxCount;
                ContextData.RemoveRange(0, removeCount);
            }
        }
    }

    /// <summary>
    /// 尝试从当前缓冲区中解码一批结果
    /// </summary>
    public List<DecodedResult> TryDecode(IoSession session, CancellationToken token)
    {
        lock (_handlerLock)
        {
            return ContextData.Count > 0 ? DoDecode(session, token) : new List<DecodedResult>();
        }
    }

    /// <summary>
    /// 清空内部缓冲区
    /// </summary>
    protected void ClearContext()
    {
        lock (_handlerLock)
        {
            ContextData.Clear();
        }
    }

    /// <summary>
    /// 按协议解码从设备读取到的数据（子类实现）
    /// 注意：此方法在锁内调用，不要做耗时或异步操作
    /// </summary>
    protected abstract List<DecodedResult> DoDecode(IoSession session, CancellationToken token);

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

    public void Dispose()
    {
        ClearContext();
    }
}