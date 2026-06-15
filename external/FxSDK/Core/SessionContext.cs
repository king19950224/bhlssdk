using System.Collections.Concurrent;
using FxSDK.Models;

namespace FxSDK.Core;

/// <summary>
/// 会话上下文
/// </summary>
public static class SessionContext
{
    #region 标识前缀
    /// <summary>
    /// 设备配置标识
    /// </summary>
    private const string DeviceConfigKey = "DEV|";
    /// <summary>
    /// 通道配置前缀
    /// </summary>
    private const string ChannelPrefix = "CH|";
    /// <summary>
    /// 终端最后通道前缀
    /// </summary>
    private const string LastChannelPrefix = "LAST-CH|";
    /// <summary>
    /// 挂起的指令
    /// </summary>
    private const string PendingKey = "P|";
    /// <summary>
    /// 温度前缀
    /// </summary>
    private const string TempPrefix = "T|";
    /// <summary>
    /// 设备模式标识
    /// </summary>
    private const string DeviceModeKey ="MODE|";
    /// <summary>
    /// 读取参数key
    /// </summary>
    private const string ReadPropertyKey = "RD|";
    

    #endregion
    
    /// <summary>
    /// 获取串口的会话Id
    /// Port name 示例 COM3
    /// </summary>
    public static string GetSerialSessionId(string commParam)
    {
        var paramArray = commParam.Split(new[] { ':', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (paramArray.Length > 0)
        {
            // 第一个参数是串口名称，例如 COM3
            return paramArray[0];
        }
        else
        {
            throw new ArgumentException("Invalid communication parameter for serial port.", nameof(commParam));
        }
    }
    /// <summary>
    /// 获取Tcp连接的会话Id
    /// 远端Ip:Port 示例 192.168.0.7:23
    /// </summary>
    public static string GetTcpSessionId(string commParam)
    {
        var paramArray = commParam.Split(new[] { ':', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (paramArray.Length >= 2)
        {
            return $"{paramArray[0]}:{paramArray[1]}";
        }
        else
        {
            throw new ArgumentException("Invalid communication parameter for socket.", nameof(commParam));
        }
    }
    #region 设备配置缓存读写
    /// <summary>
    /// 保存设备配置到会话对象
    /// </summary>
    public static void StoreToSession(IoSession session, DeviceConfig config)
    {
        SetSessionAttribute(session, DeviceConfigKey, config);
    }
    /// <summary>
    /// 保存通道配置到会话对象
    /// </summary>
    public static void StoreToSession(IoSession session, ChannelConfig config)
    {
        var channelKey = $"{ChannelPrefix}{config.Address}|{config.Channel}";
        SetSessionAttribute(session, channelKey, config);
        StoreToSession(session, config.Channel, config.Address);//保存最后channel
    }
    /// <summary>
    /// 保存最后通道到会话对象
    /// </summary>
    private static void StoreToSession(IoSession session, ushort lastChannel = 1, ushort address = 1)
    {
        var lastChannelKey = $"{LastChannelPrefix}{address}";
        SetSessionAttribute(session, lastChannelKey, lastChannel);
    }
    /// <summary>
    /// 保存设备模式到会话对象
    /// </summary>
    public static void StoreToSession(IoSession session, WorkMode mode, ushort address = 1)
    {
        var modeKey = $"{DeviceModeKey}{address}";
        SetSessionAttribute(session, modeKey, mode);
    }
    /// <summary>
    /// 保存挂起的指令
    /// </summary>
    public static void StoreToSession(IoSession session, byte[] pendingCmd, ushort address = 1)
    {
        var pendingKey = $"{PendingKey}{address}";
        var pendingCmdQueue = GetPendingCommands(session, address);
        pendingCmdQueue.Enqueue(pendingCmd);
        SetSessionAttribute(session, pendingKey, pendingCmdQueue);
    }
    /// <summary>
    /// 保存读取参数名称到会话对象
    /// </summary>
    public static void StoreToSession(IoSession session, string propertyName, ushort address = 1)
    {
        var propertyKey = $"{ReadPropertyKey}{address}";
        SetSessionAttribute(session, propertyKey, propertyName);
    }
    /// <summary>
    /// 获取设备配置
    /// </summary>
    public static DeviceConfig? GetDeviceConfig(IoSession session)
    {
        return GetSessionAttribute<DeviceConfig?>(session, DeviceConfigKey);
    }
    /// <summary>
    /// 获取设备模式
    /// </summary>
    public static WorkMode GetDeviceMode(IoSession session, ushort address = 1)
    {
        var modeKey = $"{DeviceModeKey}{address}";
        return GetSessionAttribute<WorkMode>(session, modeKey);
    }
    /// <summary>
    /// 获取通道配置
    /// </summary>
    public static ChannelConfig? GetChannelConfig(IoSession session, ushort address, ushort channel)
    {
        var channelKey = $"{ChannelPrefix}{address}|{channel}";
        return GetSessionAttribute<ChannelConfig?>(session, channelKey);
    }
    /// <summary>
    /// 获取挂起的指令列表
    /// </summary>
    public static ConcurrentQueue<byte[]> GetPendingCommands(IoSession session, ushort address = 1)
    {
        var pendingKey = $"{PendingKey}{address}";
        var queue = GetSessionAttribute<ConcurrentQueue<byte[]>>(session, pendingKey);
        if (queue == null)
        {
            queue = new ConcurrentQueue<byte[]>();
            SetSessionAttribute(session, pendingKey, queue);
        }
        return queue;
    }
    /// <summary>
    /// 清理挂起的指令列表
    /// </summary>
    public static void ClearPendingCommands(IoSession session, ushort address = 1)
    {
        var pendingKey = $"{PendingKey}{address}";
        var pendingCmdQueue = GetPendingCommands(session, address);
        pendingCmdQueue.Clear();
        SetSessionAttribute(session, pendingKey, pendingCmdQueue);
    }
    /// <summary>
    /// 获取最后通道
    /// </summary>
    public static ChannelConfig? GetLastChannelConfig(IoSession session, ushort address = 1)
    {
        var lastChannelKey = $"{LastChannelPrefix}{address}";
        var lastChannel = GetSessionAttribute<ushort>(session, lastChannelKey);
        if (lastChannel == 0)
        {
            lastChannel = 1;
        }
        return GetChannelConfig(session, address, lastChannel);
    }
    /// <summary>
    /// 存储会话通道温度参数
    /// </summary>
    public static void StoreChannelTemp(IoSession session, ushort address, ushort channel, decimal temperature)
    {
        var channelTempKey = $"{TempPrefix}{address}|{channel}";
        SetSessionAttribute(session, channelTempKey, temperature);
    }
    /// <summary>
    /// 获取通道温度
    /// </summary>
    public static decimal? GetChannelTemp(IoSession session, ushort address, ushort channel)
    {
        var channelTempKey = $"{TempPrefix}{address}|{channel}";
        return GetSessionAttribute<decimal?>(session, channelTempKey);
    }
    #endregion
    #region 会话缓存上下文操作
    /// <summary>
    /// SDK级设备状态缓存
    /// </summary>
    private static readonly ConcurrentDictionary<string,object> ContextCache = new();
    private static readonly ConcurrentDictionary<string, HashSet<string>> SessionKeys = new();
    private static readonly ConcurrentDictionary<string, object> SessionLocks = new();
    internal static void SetSessionAttribute(IoSession session, string key, object value)
    {
        var fullKey = $"{session.SessionId}|{key}";
        ContextCache.AddOrUpdate(fullKey, value, (_, _) => value);
        //注册到会话条目
        var sessionLock = SessionLocks.GetOrAdd(session.SessionId, _ => new object());
        lock(sessionLock)
        {
            var sessionSet = SessionKeys.GetOrAdd(session.SessionId, _ => new HashSet<string>());
            sessionSet.Add(fullKey);
        }
    }
    internal static T? GetSessionAttribute<T>(IoSession session, string key)
    {
        var fullKey = $"{session.SessionId}|{key}";
        return ContextCache.TryGetValue(fullKey, out var value) ? (T?)value : default;
    }
    public static void Remove(IoSession session, string key)
    {
        var fullKey = $"{session.SessionId}|{key}";
        ContextCache.TryRemove(fullKey, out _);
        //从会话条目中清理
        var sessionLock = SessionLocks.GetOrAdd(session.SessionId, _ => new object());
        lock (sessionLock)
        {
            if (SessionKeys.TryGetValue(session.SessionId, out var sessionSet) 
                && sessionSet.Contains(fullKey))
            {
                sessionSet.Remove(fullKey);
            }
        }
    }
    public static void ClearContext(IoSession session)
    {
        var sessionLock = SessionLocks.GetOrAdd(session.SessionId, _ => new object());
        lock(sessionLock)
        {
            if (SessionKeys.TryRemove(session.SessionId, out var sessionSet))
            {
                var setList = sessionSet.ToList();
                foreach (var fullKey in setList)
                {
                    ContextCache.TryRemove(fullKey, out _);
                }
                SessionLocks.TryRemove(session.SessionId, out _); // 清理锁对象
            }
        }
    }
    #endregion
}