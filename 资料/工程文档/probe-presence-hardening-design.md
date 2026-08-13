# 探头在位链加固设计：重锚事件 + 状态对账

> 状态：**方案 B（Sweep 周期对账）已实施**（2026-08-12，含 7 条集成测试 + 2 条状态机单测，582/582 全绿）。
> 实施中把事件消费重构为与对账共用的 `ApplyChangeAsync`，并顺带落地了 §2.3 规则表第 4 行的克制口径
> （`PersistAsync` 不再把 noisy/suspect 抹成 healthy——事件路径与对账路径同口径）。
> 方案 A（echo_shift 重锚事件）待实施，其消费侧可直接挂到 ApplyChangeAsync 的分流上。
> 前置：`probe-scan-design.md`（通道自动检测）、本次链路审查结论（2026-08-12 会话）、`sdk-1.6-adoption-design.md` §5。
> 触发原因：审查发现在位链两个真实缺口——**快速换插盲区**（换螺栓动作快于拔出静默阈值时全链无信号）
> 和**在位事件丢失路径**（DB 抖动/建行竞态时事件一次性消费即丢，内存与库永久不一致）。

---

## 0. 设计目标与边界

**目标**：

1. 换栓/换线这类"通道内容变了"的物理事件，只要声时上可分辨（μs 级差异），系统必须给出可见信号——不管动作多快；
2. 在位状态的最终一致：DB 抖动、WAL 降级、建行竞态都不允许把"内存已翻转、库没跟上"的不一致留到下次插拔；
3. 不碰采集热路径的性能特征（Observe 仍是字典查找 + 计数，零 IO）。

**明确不做（物理与范围边界）**：

- **同规格螺栓的区分**。两根同长同材质的螺栓声时差可能 < 温度漂移，TOF 上物理不可分辨——主识别永远靠点位表 + 接入验收流程（与 WiringGuard 的能力边界声明一致）；
- **自动法周期跳变检测**（≈500ns@2MHz，低于本设计的判定下限）。那是标定完整性问题，已列入 `sdk-1.6-adoption-design.md` §2.2 自动法试点观测项，不塞进在位链；
- **事件流持久化**（消息队列/outbox）。对账兜底后事件丢失只影响时效不影响终态，为秒级时效上重型机制不值。

---

## 1. 方案 A：重锚升级为 `echo_shift` 事件（补快速换插盲区）

### 1.1 现状与依据

`ChannelPresenceTracker.Observe` 已经能看到换栓：声时跳出稳定带宽（2000ns）时"以新值为锚重新累计"，注释明说"回波稳定到新值=真实接入了另一根螺栓"。但重锚不对外发信号——`Present` 已是 true，无状态翻转就无事件。本方案把这个已有的内部观察变成对外事件。

**为什么 2000ns 以上的稳态迁移几乎必然是物理事件**（阈值论证）：

| 正常来源 | 量级 | 能否触发 |
|---|---|---|
| 温度漂移（钢栓 ±30°C） | ±200 ns | 否（且是缓变，带宽内漂移属同一序列） |
| 松开→复紧（维护） | ±数百 ns | 否 |
| 自动法周期跳变（2MHz） | ≈500 ns | 否（刻意排除，见 §0 边界） |
| 换接不同长度螺栓 | μs 级 | **是** |
| 探头挪到另一根螺栓/换线 | μs 级 | **是** |

### 1.2 状态机改动（`ChannelPresenceTracker`）

`ChannelState` 增加一个字段：

```csharp
/// <summary>最近一次确认的稳定声时电平（ns）：插入确认时定格，echo_shift 确认时更新。
/// 与 StreakMin/Max 不同——它是跨序列的"上一个已知稳态"，重锚不清它。</summary>
public double? ConfirmedLevelNs;
```

判定规则（全部在既有 `lock (state)` 内，无新增同步开销）：

1. 插入确认（Present→true）时：`ConfirmedLevelNs = (StreakMinNs + StreakMaxNs) / 2`；
2. `Present == true` 且新序列凑满 `PlugConsecutiveEchoes` 条稳定回波时：
   若 `|新序列中点 − ConfirmedLevelNs| > ShiftThresholdNs` → 发 `echo_shift` 事件，并把 `ConfirmedLevelNs` 更新为新中点；
3. 拔出（Present→false）时清 `ConfirmedLevelNs`——拔掉再插回，走的是 plug 事件语义（那条链已有 hold/建档处理），不算 shift。

**注意第 2 条的触发点不在重锚瞬间**，而在新锚点上凑满稳定序列之后——单条毛刺跳出带宽又跳回来（重锚→下一条又重锚回旧值附近）不会发事件；只有真正在新电平上站稳的迁移才算。这个"站稳再报"的口径与插入判定完全一致，复用同一个连击计数器，不加新状态。

### 1.3 事件模型

扩展现有 `ChannelPresenceChange`（不另开事件流——shift 与 plug/unplug 有先后语义，单流保序）：

```csharp
public enum ChannelPresenceKind { Plugged, Unplugged, EchoShift }

public sealed record ChannelPresenceChange(
    long DeviceId, ushort ChannelNo, bool Present, DateTimeOffset At, bool Initial,
    ChannelPresenceKind Kind = ChannelPresenceKind.Plugged,   // 兼容既有构造：Present 语义不变
    double? ShiftFromNs = null,
    double? ShiftToNs = null);
```

既有消费方（`PersistAsync`/publisher/前端）按 Kind 分流，Plugged/Unplugged 行为一字不改。

### 1.4 消费侧动作矩阵（`ChannelPresenceService`）

| 通道状态 | 动作 |
|---|---|
| 已绑定螺栓 | `hardware_state = 'suspect'` + **触发 `echo_shift` 告警**（severity=warn）+ SignalR 推送 + WARN 日志 |
| 已绑定且 `meas_mode=0`（自动法） | 同上，告警文案追加"自动法通道必须重新扫波+置零，否则按旧锚点测出的值不可信" |
| 未绑定 | 只写 `hardware_state = 'suspect'` + SignalR + INFO 日志（没人消费它的数据，不值一条告警） |

告警落既有管线（`alarms.type='echo_shift'`，走 `IAlarmSink`/通知路由/维护抑制，与 wiring_suspect 同构）。**不自动闭合**：声时迁移是瞬时事件不是持续状态，闭合语义交给人——重新验收（刷新 `accepted_tof_ns` 指纹）或人工核实后手动清除。文案里写清这两条出路。

**限频**：同通道 `echo_shift` 事件最短间隔 `ShiftMinIntervalMinutes`（默认 10）。探头松动晃荡会反复重锚——第一条告警已经把人叫来了，连环告警只会淹没告警页。限频在 tracker 内做（记 `LastShiftAt`），事件根本不出状态机。

### 1.5 与 WiringGuard 的关系（互补不重叠）

| | echo_shift（本设计） | wiring_suspect（既有） |
|---|---|---|
| 覆盖面 | 所有在位通道（含自动建档未验收的） | 仅已验收+已绑定 |
| 时效 | 秒级（连击凑满即报） | 分钟级（60s 周期 + 5min 窗口过半） |
| 基准 | 上一个运行期稳态（进程内存） | 验收指纹（库，跨重启） |
| 盲区 | 重启后第一个稳态无从比较（ConfirmedLevel 进程态） | 未验收通道 |

两者都在才闭环：echo_shift 管"运行中突变"，WiringGuard 管"跨重启/慢漂移到别的螺栓"。**echo_shift 不持久化基准电平**——跨重启的基准就该是验收指纹，不该由内存稳态代劳，否则两套基准打架。

---

## 2. 方案 B：Sweep 周期对账（补事件丢失路径）

### 2.1 为什么选对账而不是事件重投

考虑过失败重投（consume 失败 → 延迟后写回队列）：DB 长宕时事件在队列里空转，重投延迟阻塞后续事件消费（单消费者保序），DropOldest 还可能把别的通道事件挤掉。**对账是幂等、无序、自愈一切丢失路径的**——包括审查里点名的两条：PersistAsync 抛异常丢事件、`channelId=null` 建行竞态跳过建档（行建出来后下一轮对账自然补上），也顺带覆盖"没想到的第三条"。事件流保持现状，只负责时效；终态一致由对账兜底。

### 2.2 状态机暴露快照

```csharp
/// <summary>对账用只读快照：仅含已确认态（Present 非 null）的通道。</summary>
public IReadOnlyList<(long DeviceId, ushort ChannelNo, bool Present, DateTimeOffset At)> Snapshot();
```

实现：遍历 `_states`，每项短暂加既有 per-state 锁读取。快照频率低（分钟级），无热路径影响。

### 2.3 对账规则（`ChannelPresenceService.SweepLoopAsync` 内，每 `ReconcileEverySweeps` 轮跑一次）

一条批量 SELECT 取全部相关通道的 `(hardware_state, auto_enroll_hold, bolt绑定)`，与快照比对：

| 内存 | 库 | 动作 |
|---|---|---|
| Present=true | `hardware_state ∉ {healthy, noisy, suspect}` | 走既有 `PersistAsync(Plugged)` 补写 + 未绑定则补建档（同事件消费路径，复用同一方法） |
| Present=true | ∈ present 词表但未绑定且无 hold | 只补建档（覆盖 channelId=null 竞态：状态早写上了、建档漏了） |
| Present=false | ∈ present 词表 | 走 `PersistAsync(Unplugged)` 补写（顺带清 hold，与事件路径同语义） |
| 其余 | — | 不动。**特别是 present 且库为 noisy/suspect 时不写 healthy**——不覆盖 probe-scan/echo_shift 的细分判定（顺带修掉审查小项 J：现在的 plug 事件会把 noisy 打回 healthy，对账口径比事件口径更克制，事件路径也一并改成"仅在非 present 词表态时写 healthy"） |

**幂等与并发**：补写走既有 `PersistAsync`（UPDATE 幂等），补建档走 `BoltAutoEnroller`（事务内 `FOR UPDATE` + 已绑定/hold/在位三重复核，天然幂等）。对账与事件消费并发无害——两边最终写的是同一个状态。

**DB 恢复风暴**：WAL 降级期结束后第一轮对账会补一批通道（等同重启后的启动确认潮）。enroller 每通道独立事务 + 无组节流日志已经扛过启动潮，无需新机制。对账循环本身 catch 异常记日志，下轮再来。

### 2.4 与 `Initial` 语义的交互

对账产生的补写不区分 Initial——它是修复动作不是首次确认。SignalR 推送时带 `reconciled=true` 标记（或复用 Initial=true 的"只刷状态不弹通知"通道），前端不弹横幅。日志降 INFO。

---

## 3. 参数表（新增三个，全挂 `Fx:HotPlug` 节）

| 参数 | 默认 | 依据 |
|---|---|---|
| `ShiftThresholdNs` | 2000 | 与稳定带宽同值：带宽内漂移属同一序列，跳出带宽且新稳态距旧稳态超此值才算迁移。§1.1 的量级表说明正常运行不会触发。**同规格螺栓（差 < 2μs）检测不到——物理边界，告警文案与验收流程兜底** |
| `ShiftMinIntervalMinutes` | 10 | 松动探头反复重锚的告警风暴抑制；第一条已达到叫人的目的 |
| `ReconcileEverySweeps` | 12 | sweep 默认 5s → 对账约每分钟一次。对账是兜底不是主路径，分钟级足够，一条批量查询的成本可忽略 |

---

## 4. 测试计划

**Tracker 单测（扩 `ChannelPresenceTrackerTests`）**：

1. 在位后跳变并在新电平凑满连击 → 单条 `EchoShift`（带 from/to），`ConfirmedLevelNs` 更新；
2. 单条毛刺跳出带宽后回落旧电平 → 无事件（"站稳再报"口径）；
3. 新稳态与旧稳态差 ≤ ShiftThresholdNs（如重锚后回到附近）→ 无事件；
4. 限频：两次真实迁移间隔 < ShiftMinIntervalMinutes → 第二条不发；
5. 拔出→再插 → 走 Plugged 不走 EchoShift（ConfirmedLevel 已清）；
6. `Snapshot()` 只含已确认态、值正确。

**Service 层（集成测试，Postgres 夹具）**：

7. echo_shift 事件：已绑定通道 → hardware_state=suspect + alarms 落一条 echo_shift + 推送载荷带 from/to；
8. 对账修复 PersistAsync 丢失：手工把库 hardware_state 改回 no_data、内存 present → 一轮对账后回 healthy；
9. 对账补建档：通道行后建（模拟建行竞态）→ 下一轮对账建档成功、暂存回填正常；
10. 对账不把 noisy 打回 healthy（规则表第 4 行）。

**回归**：既有 18 条 tracker 单测 + presence 集成测试全绿（Plugged/Unplugged 行为不变）。

---

## 5. 实施顺序与工作量

| 步骤 | 内容 | 量级 |
|---|---|---|
| 1 | Tracker：ConfirmedLevelNs + EchoShift 判定 + 限频 + Snapshot + 单测 | 0.5 天 |
| 2 | 事件模型扩展（Kind/from/to）+ PersistAsync 分流 + 告警落库 + 推送载荷 | 0.5 天 |
| 3 | 对账循环 + 复用建档路径重构（事件消费与对账共用 ApplyPresence 方法）+ 集成测试 | 0.5~1 天 |
| 4 | 前端：识别页 suspect 横幅 + echo_shift 告警文案展示（web 侧小改） | 0.5 天 |
| 5 | 文档：troubleshooting 加"echo_shift 告警处置"条目；probe-scan 阈值与 1.6 AGC 耦合提示一并写入 | 0.25 天 |

合计约 2~2.5 人日。步骤 1~3 后端一个 commit（可独立回滚），步骤 4 前端一个。

---

## 6. 风险与已知限制

| # | 风险/限制 | 处置 |
|---|---|---|
| 1 | 探头重新耦合（拆下清洁再装回同一螺栓）声时变化通常 < 1μs，不触发 shift；但耦合剂厚度大变可能过阈值 → 误报 | 期望行为就是"suspect + 人工确认"——重新装配本来就该复核。文案引导"确认接线无误请重新验收" |
| 2 | 重启后第一个稳态没有比较基准（ConfirmedLevel 是进程态） | 刻意为之（§1.5）：跨重启基准 = 验收指纹，WiringGuard 负责。重启窗口的换栓靠验收流程兜底 |
| 3 | 停机期间换栓 + 声时恰好相近 | 物理不可分辨（§0 边界），任何 TOF 方案都无解，点位表 + 验收是唯一答案 |
| 4 | 对账每分钟一条批量查询 | 单表索引查询，48 通道 × 几设备量级，可忽略；WAL 降级期查询失败照常 catch 跳过 |
| 5 | echo_shift 告警不自动闭合，可能长期挂着 | 有意设计：声时迁移必须有人看过。闭合出路写在文案里（重新验收 / 手动清除），与告警页既有工作流一致 |
