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

    /// <summary>
    /// 检测即螺栓：通道确认在位且无绑定螺栓时自动建档（编号自动、缺省规格，可后补）。
    /// 归属组取 devices.default_group_id，未设置且设备同项目恰有一个活跃组时兜底。
    /// 正式交付若坚持纯点位表建档流程可关闭。
    /// </summary>
    public bool AutoEnroll { get; set; } = true;

    /// <summary>
    /// 回波声时下限（ns）：低于此值按"无回波"处理，不参与插入判定、不刷新在位。
    /// 物理依据：声时下限对应螺栓最短声程，2000ns 约合十几毫米钢件——不存在这么短的监测螺栓；
    /// 真机空通道/悬空探头会偶发 1..2000ns 的杂散触发，不挡会把空口点亮成"在位"。≤0 关闭此门。
    /// </summary>
    public int MinPlausibleEchoNs { get; set; } = 2000;

    /// <summary>
    /// 插入判定的稳定性门（ns）：连续 PlugConsecutiveEchoes 条回波的最大-最小差不超过本值才判"插入"。
    /// 物理依据：探头真实耦合在螺栓上时声时短期抖动仅几十 ns；空通道杂散回波是 µs~几十 µs 级乱跳，
    /// 连不成稳定序列。跳出带宽即以最新值为锚重新累计（回波稳定到新值=真实接入了另一根螺栓，照常识别）。
    /// ≤0 关闭此门（退化为只数条数）。
    /// </summary>
    public int PlugStabilityBandNs { get; set; } = 2000;

    /// <summary>
    /// 在位通道连续多少秒无回波判"拔出"的【下限】。注意这是 UI 在位显示阈值，不是 signal_lost 告警阈值（后者 120s，另有国标依据）。
    /// 实际生效值随设备通道数自适应放大（见 <see cref="UnplugSilenceCycles"/>），取两者较大者——
    /// 全通道轮询下每通道要 channelCount×单次测量时长 才被轮到一次，固定 20s 在 48 通道下只容 4 条测量，
    /// 余量为零，任何几条缺回波（耦合稍差/重测）就会把在位通道误翻成"拔出"再翻回来，状态灯抖动。
    /// </summary>
    public int UnplugSilenceSeconds { get; set; } = 20;

    /// <summary>
    /// 单通道单次测量的估算耗时（毫秒），用于按通道数折算全通道轮转周期。
    /// 真机实测：40 通道全轮询约 4s/通道/条 → 约 100ms/次。现场若实测偏差大，改这里即可全局生效。
    /// </summary>
    public int PerChannelMeasureMs { get; set; } = 100;

    /// <summary>拔出判定需要静默满多少个"全通道轮转周期"（自适应阈值 = 周期 × 本值，与 UnplugSilenceSeconds 取大）。</summary>
    public int UnplugSilenceCycles { get; set; } = 5;

    /// <summary>静默扫描周期（秒）。</summary>
    public int SweepIntervalSeconds { get; set; } = 5;

    /// <summary>按设备通道数计算生效的拔出静默阈值（秒）：max(下限, 轮转周期×圈数)。channelCount≤0 时用下限。</summary>
    public int EffectiveUnplugSilenceSeconds(int channelCount)
    {
        if (channelCount <= 0) return UnplugSilenceSeconds;
        var cycleSeconds = channelCount * Math.Max(10, PerChannelMeasureMs) / 1000.0;
        var adaptive = (int)Math.Ceiling(cycleSeconds * Math.Max(1, UnplugSilenceCycles));
        return Math.Max(UnplugSilenceSeconds, adaptive);
    }
}

/// <summary>通道在位状态变化事件。</summary>
/// <param name="DeviceId">设备 ID</param>
/// <param name="ChannelNo">通道号（物理口）</param>
/// <param name="Present">true=插入（检测到探头回波），false=拔出/无探头（有效回波静默超阈值）</param>
/// <param name="At">判定时刻</param>
/// <param name="Initial">true=进程启动后对该通道的首次状态确认（插入或无探头方向都可能；
/// 前端只刷新状态、不弹通知，避免每次重启后端都刷一屏；服务端也按此降日志级别）</param>
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
/// - 插入：此前静默/未知的通道连续 ≥ PlugConsecutiveEchoes 条【合理且稳定】的回波 → plugged
///   （合理=声时 ≥ MinPlausibleEchoNs；稳定=序列 max-min ≤ PlugStabilityBandNs。空口杂散噪声两道门都过不去）；
/// - 拔出：在位通道连续 UnplugSilenceSeconds 无合理回波 → unplugged（由 <see cref="Sweep"/> 周期判定）。
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
        /// <summary>当前连击序列的声时包络（稳定性门用：max-min ≤ 带宽才算同一稳定序列）。</summary>
        public double StreakMinNs, StreakMaxNs;
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

    /// <summary>
    /// 采集热路径喂入一条测量观察。echoNs = 该条 ChannelResult 的 EchoTime（null=设备没算出声时）。
    /// <para>
    /// 插入判定过两道物理门（"有回波"≠"有螺栓"）：
    /// ① 下限门：echoNs ≥ MinPlausibleEchoNs，杂散短触发按无回波处理；
    /// ② 稳定性门：连续 PlugConsecutiveEchoes 条回波彼此差 ≤ PlugStabilityBandNs 才判插入——
    ///    真实耦合的回波短期稳定（几十 ns），空口噪声 µs 级乱跳连不成序列。
    /// </para>
    /// </summary>
    public void Observe(long deviceId, ushort channelNo, double? echoNs, DateTimeOffset at)
    {
        if (!_options.Enabled) return;

        // 新通道的静默地板从首次观察起算：未知态通道的"无探头确认"（见 Sweep）也要凑满
        // 完整静默阈值，避免设备刚开始轮询、首条恰无回波就被立刻确认成 no_data（真探头会闪断）
        var state = _states.GetOrAdd((deviceId, channelNo),
            static ((long, ushort) _, DateTimeOffset a) => new ChannelState { SilenceFloor = a }, at);
        ChannelPresenceChange? change = null;
        var plausible = echoNs is not null
            && (_options.MinPlausibleEchoNs <= 0 || echoNs.Value >= _options.MinPlausibleEchoNs);
        lock (state)
        {
            if (plausible)
            {
                var v = echoNs!.Value;
                // 合理回波刷新"活着"时刻（拔出静默判定的依据）；稳定性只影响插入转变
                state.LastEchoAt = at;

                if (state.ConsecutiveEchoes <= 0)
                {
                    state.ConsecutiveEchoes = 1;
                    state.StreakMinNs = state.StreakMaxNs = v;
                }
                else
                {
                    var lo = Math.Min(state.StreakMinNs, v);
                    var hi = Math.Max(state.StreakMaxNs, v);
                    if (_options.PlugStabilityBandNs <= 0 || hi - lo <= _options.PlugStabilityBandNs)
                    {
                        state.StreakMinNs = lo;
                        state.StreakMaxNs = hi;
                        state.ConsecutiveEchoes++;
                    }
                    else
                    {
                        // 跳出带宽：以本条为新锚重新累计（真实换接螺栓会在新值上重新稳定）
                        state.ConsecutiveEchoes = 1;
                        state.StreakMinNs = state.StreakMaxNs = v;
                    }
                }

                if (state.Present != true && state.ConsecutiveEchoes >= Math.Max(1, _options.PlugConsecutiveEchoes))
                {
                    var initial = state.Present is null;
                    state.Present = true;
                    change = new ChannelPresenceChange(deviceId, channelNo, Present: true, at, initial);
                }
            }
            else
            {
                // 无回波或杂散短触发（探头拔了、耦合失效、空口噪声）：打断"插入"连击。
                // 不在这里立刻判拔出——拔出统一走 Sweep 的静默时长判定，口径一致。
                state.ConsecutiveEchoes = 0;
            }
        }
        if (change is not null) Emit(change);
    }

    /// <summary>
    /// 周期静默扫描：把"在位或未知、但有效回波静默超阈值"的通道确认为拔出/无探头。
    /// 未知态的确认（Initial=true）兼作陈旧 hardware_state 的自愈：停机期间拔掉的探头、
    /// 模拟器遗留的假在位，重启后一个阈值周期内被翻正。
    /// </summary>
    /// <param name="now">当前时刻</param>
    /// <param name="isDeviceSuspended">返回 true 的设备本轮跳过判定并抬高静默地板
    /// （设备离线 / probe-scan 扫描中 / 设备级整体静默——待机等，此时静默是设备级条件，不是探头拔出）</param>
    /// <param name="isChannelObservable">返回 false 的通道不做拔出判定并抬高通道级静默地板
    /// （通道不在设备当前 MCR 轮询集内——没被轮询 ≠ 探头拔出）。null = 全部可观测。</param>
    /// <param name="unplugSilenceSeconds">按设备返回拔出静默阈值（秒），用于随通道数自适应
    /// （见 <see cref="ChannelPresenceOptions.EffectiveUnplugSilenceSeconds"/>）。null = 用配置固定值。</param>
    /// <returns>本轮产生的拔出事件（同时也已发布到 <see cref="Events"/>）</returns>
    public IReadOnlyList<ChannelPresenceChange> Sweep(
        DateTimeOffset now,
        Func<long, bool>? isDeviceSuspended = null,
        Func<long, ushort, bool>? isChannelObservable = null,
        Func<long, int>? unplugSilenceSeconds = null)
    {
        if (!_options.Enabled) return Array.Empty<ChannelPresenceChange>();

        List<ChannelPresenceChange>? changes = null;
        var suspendedCache = new Dictionary<long, bool>();
        var thresholdCache = new Dictionary<long, int>();

        foreach (var ((deviceId, channelNo), state) in _states)
        {
            if (!suspendedCache.TryGetValue(deviceId, out var suspended))
            {
                suspended = isDeviceSuspended?.Invoke(deviceId) ?? false;
                suspendedCache[deviceId] = suspended;
                if (suspended) _silenceFloor[deviceId] = now;
            }
            if (suspended) continue;

            if (!thresholdCache.TryGetValue(deviceId, out var threshold))
            {
                threshold = unplugSilenceSeconds?.Invoke(deviceId) ?? _options.UnplugSilenceSeconds;
                thresholdCache[deviceId] = threshold;
            }

            ChannelPresenceChange? change = null;
            lock (state)
            {
                if (isChannelObservable is not null && !isChannelObservable(deviceId, channelNo))
                {
                    state.SilenceFloor = now;
                    continue;
                }
                // 已确认拔出的不重复判；在位(true)与未知(null)都参与静默判定——
                // 未知态静默超阈值 = "启动后该通道被轮询过但持续无有效回波"，确认为无探头（Initial=true）。
                // 这一步会自动纠正 DB 里的陈旧 hardware_state（如上次停机期间拔掉的探头、模拟器遗留的假在位），
                // 否则重启后 DB 的 healthy 幽灵永远没人翻。
                if (state.Present == false) continue;
                var floor = _silenceFloor.TryGetValue(deviceId, out var f) ? f : DateTimeOffset.MinValue;
                if (state.SilenceFloor > floor) floor = state.SilenceFloor;
                var lastAlive = state.LastEchoAt > floor ? state.LastEchoAt : floor;
                if ((now - lastAlive).TotalSeconds >= threshold)
                {
                    var initial = state.Present is null;
                    state.Present = false;
                    state.ConsecutiveEchoes = 0;
                    change = new ChannelPresenceChange(deviceId, channelNo, Present: false, now, Initial: initial);
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
        // DropOldest：在位变化只关心最新状态，挤掉最旧的一条不影响判定。
        // 该模式下 TryWrite 恒成功，无需判返回值 —— 原先据此累加的丢弃计数永远是 0，也无人读取。
        _events.Writer.TryWrite(change);
    }
}
