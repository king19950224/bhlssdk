using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BoltPreload.Application.Alarm;

/// <summary>
/// 静态阈值告警参数，触发/恢复双阈值防抖。appsettings 的 Alarm:StaticThreshold 节是出厂兜底；
/// 库内 alarm_rules 可按 global/group/bolt 三级覆盖，经 <see cref="IAlarmRuleProvider"/> 解析。
/// </summary>
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

    /// <summary>
    /// 触发判定是否把扩展不确定度 U(k=2) 算进去。取值 <c>off</c> / <c>guardband</c> / <c>sensitive</c>，默认 <c>off</c>。
    /// <para>
    /// <b>为什么默认 off，交付前必须由人明确选一个：</b>plan 里这件事有两处互相冲突的规定。
    /// §9.15.4 写“下限报警当 F + U &lt; F_low 时才触发（保守一侧）……避免不确定度造成的边界震荡”，
    /// 按这个做，U 相当于一条护带，报警比现在<b>更难触发</b>——以 U≈5%F 计，下限实际从 0.70 后移到约 0.667，
    /// 松动会晚一步被发现。而 §9.15.3 把整个系统的基调定成“宁可多报，不可漏报”，方向正好相反。
    /// </para>
    /// <para>
    /// 边界震荡这件事，评估器已经用回差（0.70/0.72）+ on-delay（连续 5 条）两道机制在管，
    /// 不依赖不确定度护带；所以默认保持 <c>off</c>（＝历史行为，现有测试锁的也是这套），
    /// 不在没人拍板的情况下把一个安全监测系统的报警改钝。
    /// </para>
    /// <list type="bullet">
    /// <item><c>off</c>：用实测 ratio 判定，U 只做展示（当前行为）。</item>
    /// <item><c>guardband</c>：按 §9.15.4 字面实现，下限用 ratio+u、上限用 ratio−u，更难触发、误报更少、漏报风险更高。</item>
    /// <item><c>sensitive</c>：反向取边界，下限用 ratio−u、上限用 ratio+u，更早触发，贴合 §9.15.3 的“宁可多报”。</item>
    /// </list>
    /// 三档都只作用于<b>触发</b>侧；恢复侧一律用实测 ratio，避免护带把已开启的告警顶住不闭合。
    /// </summary>
    public string UncertaintyMode { get; set; } = "off";
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
/// 阈值每次评估时按 boltId 向 <see cref="IAlarmRuleProvider"/> 取（bolt &gt; group &gt; global &gt; 兜底）；
/// 规则切换不重置状态机——streak 计数保留、按新阈值继续判，语义等价于运行中调整阈值，
/// 回差带保证边界不抖。
/// 告警抑制/搁置由告警中心接口（AlarmEndpoints）在管理层实现，本评估器保持纯阈值判定；
/// 趋势类判据（CUSUM/EWMA）为后续增强方向。
/// </para>
/// </summary>
public sealed class StaticThresholdAlarmEvaluator : IAlarmEvaluator
{
    private readonly IAlarmRuleProvider _rules;
    private readonly ILogger<StaticThresholdAlarmEvaluator> _logger;
    private readonly ConcurrentDictionary<long, BoltAlarmState> _states = new();

    public StaticThresholdAlarmEvaluator(IAlarmRuleProvider rules, ILogger<StaticThresholdAlarmEvaluator> logger)
    {
        _rules = rules;
        _logger = logger;
    }

    public IReadOnlyList<AlarmEvent> Evaluate(AlarmInput input)
    {
        if (!input.PreloadRatio.HasValue)
            return Array.Empty<AlarmEvent>();

        var ratio = input.PreloadRatio.Value;
        var opt = _rules.Resolve(input.BoltId);
        // 触发侧用的比值可能带不确定度护带（见 UncertaintyMode 注释）；恢复侧一律用实测 ratio
        var (lowTriggerRatio, highTriggerRatio) = TriggerRatios(ratio, input, opt);
        var state = _states.GetOrAdd(input.BoltId, _ => new BoltAlarmState());

        lock (state)
        {
            var events = new List<AlarmEvent>();

            // === 预紧力脱落（critical，不走 on-delay；这是危及安全的状态，宁可误报 5 条也不能漏）===
            // 注意：旧版本曾把 ratio==0 当"无效数据"跳过，那其实是"螺栓松脱"的最严重情况，必须报警。
            if (ratio <= opt.PreloadLossRatio)
            {
                if (!state.LowActive)
                {
                    events.Add(BuildTriggerEvent("preload_loss", "critical", input, 1, opt));
                    state.LowActive = true;
                    state.LowAlarmType = "preload_loss";   // 记住 low 槽当前是 loss，恢复时关闭正确的 alarm 行
                    state.LowTriggerStreak = opt.TriggerConsecutive;
                    state.LowClearStreak = 0;
                }
                else if (state.LowAlarmType != "preload_loss")
                {
                    // 升级：已开 preload_low，继续跌进脱落区 → 关闭 low 行并升级为 preload_loss critical。
                    // 真实失效多是"渐松→脱落"的渐变过程；若不升级，最危险的"脱落，立即停机"告警会被
                    // 先开的 preload_low 永久压住（loss 触发被 !LowActive 挡掉），导致漏报最严重状态。
                    events.Add(BuildClearEvent(state.LowAlarmType, input, opt));
                    events.Add(BuildTriggerEvent("preload_loss", "critical", input, opt.TriggerConsecutive, opt));
                    state.LowAlarmType = "preload_loss";
                    state.LowClearStreak = 0;
                }
                return events;
            }

            // === 下限分支 ===
            if (state.LowActive)
            {
                if (ratio >= opt.ClearLowRatio)
                {
                    state.LowClearStreak++;
                    if (state.LowClearStreak >= opt.ClearConsecutive)
                    {
                        // 关闭当前 low 槽实际开启的类型（preload_low 或 preload_loss），不要写死 preload_low
                        events.Add(BuildClearEvent(state.LowAlarmType, input, opt));
                        state.LowActive = false;
                        state.LowAlarmType = "preload_low";
                        state.LowSeverity = "warn";
                        state.LowTriggerStreak = 0;
                        state.LowClearStreak = 0;
                    }
                }
                else
                {
                    state.LowClearStreak = 0;
                    // 已开启的告警随恶化升级 severity（否则静默窗豁免 critical 的逻辑永远套不到这条上）
                    if (state.LowAlarmType == "preload_low")
                    {
                        var sev = ClassifySeverity(ratio, opt);
                        if (SeverityRank(sev) > SeverityRank(state.LowSeverity))
                        {
                            events.Add(BuildEscalateEvent(state.LowAlarmType, sev, input, opt));
                            state.LowSeverity = sev;
                        }
                    }
                }
            }
            else
            {
                if (lowTriggerRatio < opt.TriggerLowRatio)
                {
                    state.LowTriggerStreak++;
                    if (state.LowTriggerStreak >= opt.TriggerConsecutive)
                    {
                        var sev = ClassifySeverity(ratio, opt);
                        events.Add(BuildTriggerEvent("preload_low", sev, input, state.LowTriggerStreak, opt));
                        state.LowActive = true;
                        state.LowAlarmType = "preload_low";
                        state.LowSeverity = sev;
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
                if (ratio <= opt.ClearHighRatio)
                {
                    state.HighClearStreak++;
                    if (state.HighClearStreak >= opt.ClearConsecutive)
                    {
                        events.Add(BuildClearEvent("preload_high", input, opt));
                        state.HighActive = false;
                        state.HighSeverity = "warn";
                        state.HighTriggerStreak = 0;
                        state.HighClearStreak = 0;
                    }
                }
                else
                {
                    state.HighClearStreak = 0;
                    var sev = ClassifySeverity(ratio, opt);
                    if (SeverityRank(sev) > SeverityRank(state.HighSeverity))
                    {
                        events.Add(BuildEscalateEvent("preload_high", sev, input, opt));
                        state.HighSeverity = sev;
                    }
                }
            }
            else
            {
                if (highTriggerRatio > opt.TriggerHighRatio)
                {
                    state.HighTriggerStreak++;
                    if (state.HighTriggerStreak >= opt.TriggerConsecutive)
                    {
                        var sev = ClassifySeverity(ratio, opt);
                        events.Add(BuildTriggerEvent("preload_high", sev, input, state.HighTriggerStreak, opt));
                        state.HighActive = true;
                        state.HighSeverity = sev;
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
        var opt = _rules.Resolve(input.BoltId);

        // 预紧力脱落：最高危，立即 critical（与有状态分支一致，脱落不走 on-delay）
        if (ratio <= opt.PreloadLossRatio)
            return BuildTriggerEvent("preload_loss", "critical", input, 1, opt);

        // 严重偏低 / 偏高：越过触发阈值且偏差达 critical 档（默认 30%）
        if (ratio < opt.TriggerLowRatio && Math.Abs(1m - ratio) >= opt.CriticalDeviation)
            return BuildTriggerEvent("preload_low", "critical", input, 1, opt);
        if (ratio > opt.TriggerHighRatio && Math.Abs(1m - ratio) >= opt.CriticalDeviation)
            return BuildTriggerEvent("preload_high", "critical", input, 1, opt);

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

        var opt = _rules.Resolve(ev.BoltId);
        lock (state)
        {
            switch (ev.Action)
            {
                // 撤销 low 槽触发（preload_low / preload_loss 共用 LowActive）
                case AlarmAction.Trigger when ev.Type is "preload_low" or "preload_loss":
                    state.LowActive = false;
                    state.LowAlarmType = "preload_low";
                    state.LowTriggerStreak = Math.Max(0, opt.TriggerConsecutive - 1);
                    state.LowClearStreak = 0;
                    break;

                case AlarmAction.Trigger when ev.Type == "preload_high":
                    state.HighActive = false;
                    state.HighTriggerStreak = Math.Max(0, opt.TriggerConsecutive - 1);
                    state.HighClearStreak = 0;
                    break;

                // 撤销 low 槽恢复：重新置 active，下一条恢复样本立即重试 Clear
                case AlarmAction.Clear when ev.Type is "preload_low" or "preload_loss":
                    state.LowActive = true;
                    state.LowAlarmType = ev.Type;
                    state.LowClearStreak = Math.Max(0, opt.ClearConsecutive - 1);
                    break;

                case AlarmAction.Clear when ev.Type == "preload_high":
                    state.HighActive = true;
                    state.HighClearStreak = Math.Max(0, opt.ClearConsecutive - 1);
                    break;

                // 撤销升级：severity 退回 warn，后续同向样本可再次 Escalate
                case AlarmAction.Escalate when ev.Type is "preload_low" or "preload_loss":
                    state.LowSeverity = "warn";
                    break;
                case AlarmAction.Escalate when ev.Type == "preload_high":
                    state.HighSeverity = "warn";
                    break;
            }
        }
    }

    /// <summary>
    /// 算触发侧用的比值。<c>UncertaintyMode=off</c>（默认）时原样返回实测 ratio，
    /// 否则按 U(k=2) 折算成比值护带后取相应一侧。缺 U 或缺目标预紧力时一律退回实测 ratio ——
    /// 没有不确定度就当没有护带，不能拿一个猜的 U 去挪安全阈值。
    /// </summary>
    private static (decimal Low, decimal High) TriggerRatios(
        decimal ratio, AlarmInput input, StaticThresholdAlarmOptions opt)
    {
        if (string.Equals(opt.UncertaintyMode, "off", StringComparison.OrdinalIgnoreCase))
            return (ratio, ratio);

        if (input.UncertaintyN is not { } u || u <= 0m
            || input.TargetPreloadN is not { } target || target <= 0m)
            return (ratio, ratio);

        var uRatio = u / target;
        return opt.UncertaintyMode.ToLowerInvariant() switch
        {
            // §9.15.4 字面：两侧都取"离阈值更远"的那一端，报警更难触发
            "guardband" => (ratio + uRatio, ratio - uRatio),
            // §9.15.3 基调：两侧都取"离阈值更近"的那一端，报警更早触发
            "sensitive" => (ratio - uRatio, ratio + uRatio),
            // 配置写错时按 off 处理并保持实测值，不要因为一个拼错的字符串改变安全判定
            _ => (ratio, ratio),
        };
    }

    /// <summary>
    /// 根据偏离目标的程度分级（GB/T 41261 三级警告：info / warn / critical）。
    /// |1 - ratio| ≥ CriticalDeviation 即 critical（默认 30% 偏离）。
    /// </summary>
    private static string ClassifySeverity(decimal ratio, StaticThresholdAlarmOptions opt)
    {
        var deviation = Math.Abs(1m - ratio);
        return deviation >= opt.CriticalDeviation ? "critical" : "warn";
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

    // 事件里快照的是本次评估实际用的阈值（可能来自 bolt/group/global 任一级），落到 alarms 行可追溯
    private static AlarmEvent BuildTriggerEvent(string type, string severity, AlarmInput input, int streak, StaticThresholdAlarmOptions opt) =>
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
            ThresholdLowRatio: opt.TriggerLowRatio,
            ThresholdHighRatio: opt.TriggerHighRatio,
            ConsecutiveCount: streak,
            Message: BuildMessage(type, input));

    private static AlarmEvent BuildClearEvent(string type, AlarmInput input, StaticThresholdAlarmOptions opt) =>
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
            ThresholdLowRatio: opt.TriggerLowRatio,
            ThresholdHighRatio: opt.TriggerHighRatio,
            ConsecutiveCount: 0,
            Message: $"{TypeLabel(type)}已恢复（当前为目标的 {(input.PreloadRatio != null ? (input.PreloadRatio.Value * 100m).ToString("F1") + "%" : "—")}）");

    private static AlarmEvent BuildEscalateEvent(string type, string severity, AlarmInput input, StaticThresholdAlarmOptions opt) =>
        new(
            Action: AlarmAction.Escalate,
            BoltId: input.BoltId,
            ChannelId: input.ChannelId,
            Type: type,
            Severity: severity,
            Ts: input.Ts,
            PreloadN: input.PreloadN,
            PreloadRatio: input.PreloadRatio,
            TargetPreloadN: input.TargetPreloadN,
            ThresholdLowRatio: opt.TriggerLowRatio,
            ThresholdHighRatio: opt.TriggerHighRatio,
            ConsecutiveCount: 0,
            Message: $"{TypeLabel(type)}已升级为{severity}：{BuildMessage(type, input)}");

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" or "crit" => 3,
        "warn" or "warning" => 2,
        "info" => 1,
        _ => 0,
    };

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
        /// <summary>当前 low 槽 severity 快照，用于检测 warn→critical 升级。</summary>
        public string LowSeverity = "warn";
        public int LowTriggerStreak;
        public int LowClearStreak;
        public bool HighActive;
        public string HighSeverity = "warn";
        public int HighTriggerStreak;
        public int HighClearStreak;
    }
}
