using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoltPreload.Application.Alarm;

/// <summary>静态阈值告警配置（appsettings 的 Alarm:StaticThreshold 节），触发/恢复双阈值防抖。</summary>
public sealed class StaticThresholdAlarmOptions
{
    /// <summary>触发下限（preload_ratio）。低于此值连续 N 次 → preload_low 告警。</summary>
    public decimal TriggerLowRatio { get; set; } = 0.70m;

    /// <summary>恢复下限（preload_ratio）。从 low 状态高于此值连续 M 次 → 自动恢复。</summary>
    public decimal ClearLowRatio { get; set; } = 0.72m;

    /// <summary>触发上限（preload_ratio）。高于此值连续 N 次 → preload_high 告警。</summary>
    public decimal TriggerHighRatio { get; set; } = 1.10m;

    /// <summary>恢复上限（preload_ratio）。从 high 状态低于此值连续 M 次 → 自动恢复。</summary>
    public decimal ClearHighRatio { get; set; } = 1.08m;

    /// <summary>触发延时：连续多少条越限才真正触发（on-delay，单位=测量条数）</summary>
    public int TriggerConsecutive { get; set; } = 5;

    /// <summary>恢复延时：连续多少条恢复才闭合（off-delay）</summary>
    public int ClearConsecutive { get; set; } = 10;

    /// <summary>升级到 critical 的 ratio 偏差阈值（|偏差| ≥ 30% 即 critical），对应 GB/T 41261 三级警告中的"严重"。</summary>
    public decimal CriticalDeviation { get; set; } = 0.30m;

    /// <summary>预紧力完全脱落的判定阈值。ratio 低于此值即视为 preload_loss critical（不走 on-delay）。</summary>
    public decimal PreloadLossRatio { get; set; } = 0.05m;
}

/// <summary>
/// 静态阈值告警评估器（最简版，对应方案 §8.7.2 第一档）。
/// <para>
/// 状态机：
/// <code>
///   Normal ──(连续 N 条 ratio &lt; 0.70)──&gt; LowActive ──(连续 M 条 ratio &gt;= 0.72)──&gt; Normal
///   Normal ──(连续 N 条 ratio &gt; 1.10)──&gt; HighActive ──(连续 M 条 ratio &lt;= 1.08)──&gt; Normal
/// </code>
/// </para>
/// <para>
/// 每个 bolt 一个状态机。线程安全：用 lock 保证状态机原子性。
/// 告警抑制/搁置由告警中心接口（AlarmEndpoints）在管理层实现，本评估器保持纯阈值判定；
/// 趋势类判据（CUSUM/EWMA）为后续增强方向。
/// </para>
/// </summary>
public sealed class StaticThresholdAlarmEvaluator : IAlarmEvaluator
{
    private readonly StaticThresholdAlarmOptions _opt;
    private readonly ILogger<StaticThresholdAlarmEvaluator> _logger;
    private readonly ConcurrentDictionary<long, BoltAlarmState> _states = new();

    public StaticThresholdAlarmEvaluator(IOptions<StaticThresholdAlarmOptions> opt, ILogger<StaticThresholdAlarmEvaluator> logger)
    {
        _opt = opt.Value;
        _logger = logger;
    }

    public IReadOnlyList<AlarmEvent> Evaluate(AlarmInput input)
    {
        if (!input.PreloadRatio.HasValue)
            return Array.Empty<AlarmEvent>();

        var ratio = input.PreloadRatio.Value;
        var state = _states.GetOrAdd(input.BoltId, _ => new BoltAlarmState());

        lock (state)
        {
            var events = new List<AlarmEvent>();

            // === 预紧力脱落（critical，不走 on-delay；这是危及安全的状态，宁可误报 5 条也不能漏）===
            // 注意：旧版本曾把 ratio==0 当"无效数据"跳过，那其实是"螺栓松脱"的最严重情况，必须报警。
            if (ratio <= _opt.PreloadLossRatio)
            {
                if (!state.LowActive)
                {
                    events.Add(BuildTriggerEvent("preload_loss", "critical", input, 1));
                    state.LowActive = true;
                    state.LowAlarmType = "preload_loss";   // 记住 low 槽当前是 loss，恢复时关闭正确的 alarm 行
                    state.LowTriggerStreak = _opt.TriggerConsecutive;
                    state.LowClearStreak = 0;
                }
                else if (state.LowAlarmType != "preload_loss")
                {
                    // 升级：已开 preload_low，继续跌进脱落区 → 关闭 low 行并升级为 preload_loss critical。
                    // 真实失效多是"渐松→脱落"的渐变过程；若不升级，最危险的"脱落，立即停机"告警会被
                    // 先开的 preload_low 永久压住（loss 触发被 !LowActive 挡掉），导致漏报最严重状态。
                    events.Add(BuildClearEvent(state.LowAlarmType, input));
                    events.Add(BuildTriggerEvent("preload_loss", "critical", input, _opt.TriggerConsecutive));
                    state.LowAlarmType = "preload_loss";
                    state.LowClearStreak = 0;
                }
                return events;
            }

            // === 下限分支 ===
            if (state.LowActive)
            {
                if (ratio >= _opt.ClearLowRatio)
                {
                    state.LowClearStreak++;
                    if (state.LowClearStreak >= _opt.ClearConsecutive)
                    {
                        // 关闭当前 low 槽实际开启的类型（preload_low 或 preload_loss），不要写死 preload_low
                        events.Add(BuildClearEvent(state.LowAlarmType, input));
                        state.LowActive = false;
                        state.LowAlarmType = "preload_low";
                        state.LowTriggerStreak = 0;
                        state.LowClearStreak = 0;
                    }
                }
                else
                {
                    state.LowClearStreak = 0;
                }
            }
            else
            {
                if (ratio < _opt.TriggerLowRatio)
                {
                    state.LowTriggerStreak++;
                    if (state.LowTriggerStreak >= _opt.TriggerConsecutive)
                    {
                        var sev = ClassifySeverity(ratio);
                        events.Add(BuildTriggerEvent("preload_low", sev, input, state.LowTriggerStreak));
                        state.LowActive = true;
                        state.LowAlarmType = "preload_low";
                        state.LowClearStreak = 0;
                    }
                }
                else
                {
                    state.LowTriggerStreak = 0;
                }
            }

            // === 上限分支（与下限对称）===
            if (state.HighActive)
            {
                if (ratio <= _opt.ClearHighRatio)
                {
                    state.HighClearStreak++;
                    if (state.HighClearStreak >= _opt.ClearConsecutive)
                    {
                        events.Add(BuildClearEvent("preload_high", input));
                        state.HighActive = false;
                        state.HighTriggerStreak = 0;
                        state.HighClearStreak = 0;
                    }
                }
                else
                {
                    state.HighClearStreak = 0;
                }
            }
            else
            {
                if (ratio > _opt.TriggerHighRatio)
                {
                    state.HighTriggerStreak++;
                    if (state.HighTriggerStreak >= _opt.TriggerConsecutive)
                    {
                        var sev = ClassifySeverity(ratio);
                        events.Add(BuildTriggerEvent("preload_high", sev, input, state.HighTriggerStreak));
                        state.HighActive = true;
                        state.HighClearStreak = 0;
                    }
                }
                else
                {
                    state.HighTriggerStreak = 0;
                }
            }

            return events;
        }
    }

    /// <summary>
    /// 无状态 critical 安全网（C1）：不触碰 <see cref="_states"/>，仅判断单条样本是否已处于
    /// "立即危险"档。供 WAL 重放兜底补判用——重放的是历史/乱序数据，喂有状态状态机会误触发/误恢复，
    /// 但 DB 故障窗口内真实发生的脱落 / 严重越限不能因此被吞掉。
    /// 只补最高危的 critical 档（脱落 + 偏差≥CriticalDeviation 的越限），避免重放噪声刷屏。
    /// </summary>
    public AlarmEvent? EvaluateCriticalStateless(AlarmInput input)
    {
        if (!input.PreloadRatio.HasValue)
            return null;

        var ratio = input.PreloadRatio.Value;

        // 预紧力脱落：最高危，立即 critical（与有状态分支一致，脱落不走 on-delay）
        if (ratio <= _opt.PreloadLossRatio)
            return BuildTriggerEvent("preload_loss", "critical", input, 1);

        // 严重偏低 / 偏高：越过触发阈值且偏差达 critical 档（默认 30%）
        if (ratio < _opt.TriggerLowRatio && Math.Abs(1m - ratio) >= _opt.CriticalDeviation)
            return BuildTriggerEvent("preload_low", "critical", input, 1);
        if (ratio > _opt.TriggerHighRatio && Math.Abs(1m - ratio) >= _opt.CriticalDeviation)
            return BuildTriggerEvent("preload_high", "critical", input, 1);

        return null;
    }

    /// <summary>
    /// 回滚最近一次 <see cref="Evaluate"/> 的状态转移（C2）。持久化失败时调用，使状态机退到
    /// "差一格即可重新触发/恢复"的保守态，让下一条同向样本立即重试持久化——
    /// 宁可重复告警，绝不永久漏报（与 preload_loss"宁可误报不漏报"的哲学一致）。
    /// </summary>
    public void RollbackTransition(AlarmEvent ev)
    {
        if (!_states.TryGetValue(ev.BoltId, out var state))
            return;

        lock (state)
        {
            switch (ev.Action)
            {
                // 撤销 low 槽触发（preload_low / preload_loss 共用 LowActive）
                case AlarmAction.Trigger when ev.Type is "preload_low" or "preload_loss":
                    state.LowActive = false;
                    state.LowAlarmType = "preload_low";
                    state.LowTriggerStreak = Math.Max(0, _opt.TriggerConsecutive - 1);
                    state.LowClearStreak = 0;
                    break;

                case AlarmAction.Trigger when ev.Type == "preload_high":
                    state.HighActive = false;
                    state.HighTriggerStreak = Math.Max(0, _opt.TriggerConsecutive - 1);
                    state.HighClearStreak = 0;
                    break;

                // 撤销 low 槽恢复：重新置 active，下一条恢复样本立即重试 Clear
                case AlarmAction.Clear when ev.Type is "preload_low" or "preload_loss":
                    state.LowActive = true;
                    state.LowAlarmType = ev.Type;
                    state.LowClearStreak = Math.Max(0, _opt.ClearConsecutive - 1);
                    break;

                case AlarmAction.Clear when ev.Type == "preload_high":
                    state.HighActive = true;
                    state.HighClearStreak = Math.Max(0, _opt.ClearConsecutive - 1);
                    break;
            }
        }
    }

    /// <summary>
    /// 根据偏离目标的程度分级（GB/T 41261 三级警告：info / warn / critical）。
    /// |1 - ratio| ≥ CriticalDeviation 即 critical（默认 30% 偏离）。
    /// </summary>
    private string ClassifySeverity(decimal ratio)
    {
        var deviation = Math.Abs(1m - ratio);
        return deviation >= _opt.CriticalDeviation ? "critical" : "warn";
    }

    /// <summary>
    /// 进程启动时从 DB 恢复内存状态（避免重启后重新累积 on-delay）。
    /// 当前开启告警 → 直接置 active。连击计数不恢复：触发侧 active 态用不到，
    /// 恢复侧从 0 重数只是自动闭合稍慢一拍。
    /// </summary>
    public void Restore(IEnumerable<(long BoltId, string Type)> openAlarms)
    {
        foreach (var (boltId, type) in openAlarms)
        {
            var state = _states.GetOrAdd(boltId, _ => new BoltAlarmState());
            lock (state)
            {
                // preload_loss 与 preload_low 共用 LowActive 槽：两者都要置 active，
                // 否则重启后 DB 里已开启的 preload_loss 内存状态没恢复 → 下一条越限会被当"全新"再次触发（重复 critical 告警）。
                // 同时记住具体类型，让后续自动恢复时 Clear 关闭正确的 alarm 行（而不是一律 preload_low）。
                if (type is "preload_low" or "preload_loss")
                {
                    state.LowActive = true;
                    state.LowAlarmType = type;
                }
                if (type == "preload_high") state.HighActive = true;
            }
        }
        _logger.LogInformation("从 DB 恢复 {Count} 条开启告警的内存状态", openAlarms.Count());
    }

    private AlarmEvent BuildTriggerEvent(string type, string severity, AlarmInput input, int streak) =>
        new(
            Action: AlarmAction.Trigger,
            BoltId: input.BoltId,
            ChannelId: input.ChannelId,
            Type: type,
            Severity: severity,
            Ts: input.Ts,
            PreloadN: input.PreloadN,
            PreloadRatio: input.PreloadRatio,
            TargetPreloadN: input.TargetPreloadN,
            ThresholdLowRatio: _opt.TriggerLowRatio,
            ThresholdHighRatio: _opt.TriggerHighRatio,
            ConsecutiveCount: streak,
            Message: BuildMessage(type, input));

    private AlarmEvent BuildClearEvent(string type, AlarmInput input) =>
        new(
            Action: AlarmAction.Clear,
            BoltId: input.BoltId,
            ChannelId: input.ChannelId,
            Type: type,
            Severity: "info",
            Ts: input.Ts,
            PreloadN: input.PreloadN,
            PreloadRatio: input.PreloadRatio,
            TargetPreloadN: input.TargetPreloadN,
            ThresholdLowRatio: _opt.TriggerLowRatio,
            ThresholdHighRatio: _opt.TriggerHighRatio,
            ConsecutiveCount: 0,
            Message: $"{TypeLabel(type)}已恢复（当前为目标的 {(input.PreloadRatio != null ? (input.PreloadRatio.Value * 100m).ToString("F1") + "%" : "—")}）");

    private static string TypeLabel(string type) => type switch
    {
        "preload_low" => "预紧力偏低",
        "preload_high" => "预紧力偏高",
        "preload_loss" => "预紧力脱落",
        _ => type,
    };

    private static string BuildMessage(string type, AlarmInput input)
    {
        // message 直接展示给用户（告警中心/大屏/详情履历），用可读中文，不用 ratio=/target= 内部参数拼写
        var pctStr = input.PreloadRatio != null ? (input.PreloadRatio.Value * 100m).ToString("F1") + "%" : "—";
        var preloadStr = input.PreloadN?.ToString("F0") ?? "—";
        var targetStr = input.TargetPreloadN?.ToString("F0") ?? "—";
        return type switch
        {
            "preload_low" => $"预紧力偏低：当前 {preloadStr} N，为目标 {targetStr} N 的 {pctStr}",
            "preload_high" => $"预紧力偏高：当前 {preloadStr} N，为目标 {targetStr} N 的 {pctStr}",
            "preload_loss" => $"预紧力疑似脱落：当前 {preloadStr} N，仅为目标 {targetStr} N 的 {pctStr}，请立即停机巡检",
            _ => $"告警 {type}：当前为目标的 {pctStr}"
        };
    }

    private sealed class BoltAlarmState
    {
        public bool LowActive;
        // low 槽当前开启的告警类型："preload_low" 或 "preload_loss"（两者共用 LowActive）。
        // 用于自动恢复时 Clear 关闭正确的 alarm 行。
        public string LowAlarmType = "preload_low";
        public int LowTriggerStreak;
        public int LowClearStreak;
        public bool HighActive;
        public int HighTriggerStreak;
        public int HighClearStreak;
    }
}
