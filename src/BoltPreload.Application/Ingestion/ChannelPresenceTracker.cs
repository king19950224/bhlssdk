using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace BoltPreload.Application.Ingestion;

/// <summary>探头热插拔配置（Fx:HotPlug）。</summary>
public sealed class ChannelPresenceOptions
{
    /// <summary>总开关：启用全通道轮询 + 在位判定。关掉则回到"只轮询 sampling_enabled 通道"的旧行为。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>连续多少条有回波的测量才判"插入"（防单次噪声误报）。</summary>
    public int PlugConsecutiveEchoes { get; set; } = 3;

    /// <summary>在位通道连续多少秒无回波判"拔出"。注意这是 UI 在位显示阈值，不是 signal_lost 告警阈值（后者 120s，另有国标依据）。</summary>
    public int UnplugSilenceSeconds { get; set; } = 20;

    /// <summary>静默扫描周期（秒）。</summary>
    public int SweepIntervalSeconds { get; set; } = 5;
}

/// <summary>通道在位状态变化事件。</summary>
/// <param name="DeviceId">设备 ID</param>
/// <param name="ChannelNo">通道号（物理口）</param>
/// <param name="Present">true=插入（检测到探头回波），false=拔出（回波静默超阈值）</param>
/// <param name="At">判定时刻</param>
/// <param name="Initial">true=进程启动后对该通道的首次在位确认（前端只刷新状态、不弹"新探头"提示，避免每次重启后端都刷一屏通知）</param>
public sealed record ChannelPresenceChange(
    long DeviceId,
    ushort ChannelNo,
    bool Present,
    DateTimeOffset At,
    bool Initial);

/// <summary>
/// 通道在位状态机（探头热插拔识别的核心）。
/// <para>
/// 挂在采集热路径上：每条 ChannelResult 喂入 <see cref="Observe"/>（无锁竞争路径为字典查找 + 计数，开销可忽略）。
/// 判定规则：
/// - 插入：此前静默/未知的通道连续 ≥ PlugConsecutiveEchoes 条有回波 → plugged；
/// - 拔出：在位通道连续 UnplugSilenceSeconds 无回波 → unplugged（由 <see cref="Sweep"/> 周期判定）。
/// </para>
/// <para>
/// 误判防护：设备离线/重连、通道自动检测（probe-scan 期间设备只轮询单通道）都会造成"假静默"，
/// 调用方在 Sweep 时把这类设备标记为 suspended，本状态机对其抬高静默地板（floor），恢复后重新起算。
/// </para>
/// <para>事件经有界队列（满丢最旧）向外发布，消费方（ChannelPresenceService）负责落库 + SignalR 推送；
/// 状态机本身不做 IO，保证热路径零阻塞。</para>
/// </summary>
public sealed class ChannelPresenceTracker
{
    private sealed class ChannelState
    {
        /// <summary>null = 进程启动后尚未确认过（未知态）。</summary>
        public bool? Present;
        public int ConsecutiveEchoes;
        public DateTimeOffset LastEchoAt;

        /// <summary>
        /// 通道级静默地板：通道不在当前轮询集（不可观测）期间持续抬高。
        /// 重新入集后静默从此刻起算，避免用"没被轮询"的陈旧 LastEchoAt 立刻误判拔出。
        /// </summary>
        public DateTimeOffset SilenceFloor;
    }

    private readonly ConcurrentDictionary<(long DeviceId, ushort ChannelNo), ChannelState> _states = new();
    // 每设备的"静默地板"：suspended（离线/扫描中）期间持续抬高，拔出判定按 max(LastEchoAt, floor) 起算
    private readonly ConcurrentDictionary<long, DateTimeOffset> _silenceFloor = new();
    private readonly Channel<ChannelPresenceChange> _events;
    private readonly ChannelPresenceOptions _options;
    private long _droppedEvents;

    public ChannelPresenceTracker(IOptions<ChannelPresenceOptions> options)
    {
        _options = options.Value;
        _events = Channel.CreateBounded<ChannelPresenceChange>(
            new BoundedChannelOptions(capacity: 256)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
    }

    /// <summary>在位变化事件流（消费方：ChannelPresenceService）。</summary>
    public ChannelReader<ChannelPresenceChange> Events => _events.Reader;

    /// <summary>事件队列满被挤掉的事件数（仅观测；正常运行应恒为 0）。</summary>
    public long DroppedEvents => Interlocked.Read(ref _droppedEvents);

    /// <summary>
    /// 采集热路径喂入一条测量观察。hasEcho = 该条 ChannelResult 的 EchoTime 非空。
    /// </summary>
    public void Observe(long deviceId, ushort channelNo, bool hasEcho, DateTimeOffset at)
    {
        if (!_options.Enabled) return;

        var state = _states.GetOrAdd((deviceId, channelNo), _ => new ChannelState());
        ChannelPresenceChange? change = null;
        lock (state)
        {
            if (hasEcho)
            {
                state.LastEchoAt = at;
                state.ConsecutiveEchoes++;
                if (state.Present != true && state.ConsecutiveEchoes >= Math.Max(1, _options.PlugConsecutiveEchoes))
                {
                    var initial = state.Present is null;
                    state.Present = true;
                    change = new ChannelPresenceChange(deviceId, channelNo, Present: true, at, initial);
                }
            }
            else
            {
                // 有上报但无回波（探头拔了、耦合失效等）：打断"插入"连击。
                // 不在这里立刻判拔出——拔出统一走 Sweep 的静默时长判定，口径一致。
                state.ConsecutiveEchoes = 0;
            }
        }
        if (change is not null) Emit(change);
    }

    /// <summary>
    /// 周期静默扫描：把"在位但静默超阈值"的通道翻成拔出。
    /// </summary>
    /// <param name="now">当前时刻</param>
    /// <param name="isDeviceSuspended">返回 true 的设备本轮跳过判定并抬高静默地板
    /// （设备离线 / probe-scan 扫描中 / 设备级整体静默——待机等，此时静默是设备级条件，不是探头拔出）</param>
    /// <param name="isChannelObservable">返回 false 的通道不做拔出判定并抬高通道级静默地板
    /// （通道不在设备当前 MCR 轮询集内——没被轮询 ≠ 探头拔出）。null = 全部可观测。</param>
    /// <returns>本轮产生的拔出事件（同时也已发布到 <see cref="Events"/>）</returns>
    public IReadOnlyList<ChannelPresenceChange> Sweep(
        DateTimeOffset now,
        Func<long, bool>? isDeviceSuspended = null,
        Func<long, ushort, bool>? isChannelObservable = null)
    {
        if (!_options.Enabled) return Array.Empty<ChannelPresenceChange>();

        List<ChannelPresenceChange>? changes = null;
        var suspendedCache = new Dictionary<long, bool>();

        foreach (var ((deviceId, channelNo), state) in _states)
        {
            if (!suspendedCache.TryGetValue(deviceId, out var suspended))
            {
                suspended = isDeviceSuspended?.Invoke(deviceId) ?? false;
                suspendedCache[deviceId] = suspended;
                if (suspended) _silenceFloor[deviceId] = now;
            }
            if (suspended) continue;

            ChannelPresenceChange? change = null;
            lock (state)
            {
                if (isChannelObservable is not null && !isChannelObservable(deviceId, channelNo))
                {
                    state.SilenceFloor = now;
                    continue;
                }
                if (state.Present != true) continue;
                var floor = _silenceFloor.TryGetValue(deviceId, out var f) ? f : DateTimeOffset.MinValue;
                if (state.SilenceFloor > floor) floor = state.SilenceFloor;
                var lastAlive = state.LastEchoAt > floor ? state.LastEchoAt : floor;
                if ((now - lastAlive).TotalSeconds >= _options.UnplugSilenceSeconds)
                {
                    state.Present = false;
                    state.ConsecutiveEchoes = 0;
                    change = new ChannelPresenceChange(deviceId, channelNo, Present: false, now, Initial: false);
                }
            }
            if (change is not null)
            {
                Emit(change);
                (changes ??= []).Add(change);
            }
        }
        return changes ?? (IReadOnlyList<ChannelPresenceChange>)Array.Empty<ChannelPresenceChange>();
    }

    private void Emit(ChannelPresenceChange change)
    {
        // DropOldest 模式下 TryWrite 恒成功；计数仅观测拥塞
        if (!_events.Writer.TryWrite(change))
            Interlocked.Increment(ref _droppedEvents);
    }
}
