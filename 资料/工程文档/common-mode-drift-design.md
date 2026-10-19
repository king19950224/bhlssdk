# 同组共模抑制：让"这颗螺栓松了还是紧了"这个答案可用（2026-08-27）

> 状态：**已实施**。算法 + 落库 + 告警 + 端点 + 主视图页全部落地，单测 16 条通过，
> 集成测试 8 条随库跑（本机无 PG，待有库环境验证）。
> 相关：`field-consistency-study-2026-08-25.md` §4.1（温补残差，本文是它的落地）、
> `product-design.md` §8.2（趋势告警，前置依赖本文）、`sdk-api-facts.md` §12（K_L 量纲约定）。

---

## 1. 为什么做这件事：温度噪声和松动信号是同一量级

客户对这套系统只问一个问题：**螺栓松了还是紧了。** 在做任何功能之前，先把这个答案的
可信度算清楚。全部参数取自代码与真机快照，不引外部假设：

| 量 | 值 | 出处 |
|---|---|---|
| 基准声时 t₀ | 31 747 ns | `tmp/snaps/snap_now.json`，CH1 的 RefTime（实测） |
| 温度系数 α | 1.0269e-4 /°C | `BoltPhysicsDefaults.SdkTempCoefficientPerC`，注释自述为"SDK 默认碳钢兜底值" |
| 温度对声时的影响 | **3.26 ns/°C** | t₀ × α |
| M20 8.8 满载声时变化 | **138 ~ 207 ns** | K_L ≈ (1+声弹因子)·t₀/(E·A)（`CalibrationEndpoints.PeerMedianKlAsync` 注释给的关系），取声弹因子 2~3、A=245mm²、E=206GPa、目标 110 kN |
| 折算 | **1°C ≈ 满载的 1.6% ~ 2.4%** | 两者相除 |
| 告警线 | ratio < 0.70（跌 30%） | `appsettings.json` → `Alarm:StaticThreshold:TriggerLowRatio` |
| **等效结论** | **约 13 ~ 19°C 的温补残差 = 一个"松了 30%"的假告警** | 0.30 ÷ 上一行 |

这个量级关系不是新发现——`WiringGuardOptions.ToleranceNs` 的注释里早就写着
"容差要盖过温度漂移（钢栓约 ±200ns/±30°C）与预紧力变化（松开→复紧约 ±数百 ns）"。
接线守卫用它来解释为什么容差要放到 500ns；但**同一个物理事实在预紧力判定链上一直没有被正面处理**。

温补公式（`GbSingleWaveCalculator.cs`：`normalizedTof = echoTime / (1 + α·ΔT)`）能扣掉大部分，
残差取决于 α 的准确度，而 α 的两个误差源都压不下去：

1. 用的是 SDK 默认碳钢值，不是这批螺栓实测——不同钢种差 10~20%；
2. Pt1000 测的是**端面温度**，不是声程平均温度——日照、停机、夜间散热工况下螺栓内部有梯度。

风电塔筒法兰季节温差 40~60°C。α 偏 20% 即产生约 10°C 等效残差，偏 30% 就踩到告警线上。

**所以：在温度被处理掉之前，绝对阈值判定既会假报，也会漏报真实的小幅松动。**
这条链之上的任何东西——GB 不确定度合成、三级告警规则覆盖、出站通知、验收报告——
都不能改变这个事实。

## 2. 原设计的方向性问题：共模抑制做在了告警之后

系统里本来就有"同组共模"判据，位置在 `AlarmEndpoints.cs` 的**告警诊断**里：

```
else if (peerAlsoAbnormal) { verdict = "common_mode"; recommendation = "同组其他螺栓中位读数也在越限：更像工况变化（温度/载荷）……" }
```

也就是：告警已经落库、通知已经出站、值班手机已经响了，页面才解释"这可能是温度"。
判定环节（`StaticThresholdAlarmEvaluator.Evaluate` 只吃单颗 `PreloadRatio`，
`AssetEndpoints.BoltStatusCte` 的 8 种状态全按单颗 ratio 绝对值）完全不看同组。

同一根因的第二处显形：`DashboardEndpoints` 的"X 天后到限值"用 `regr_slope` 对未扣共模的
`preload_n` 做 7 天线性回归。`R² ≥ 0.5` 只能挡随机噪声，挡不住入秋降温这种**系统性温度趋势**——
全组螺栓会一起给出"高置信度线性下降"。

## 3. 方案：温度是共模，松动是差模

温度、风载、塔筒摆动、整组目标配错，作用在同一法兰的所有螺栓上是**同步同向**的；
单颗螺栓松动只作用在它自己身上。取同组中位数当参考系扣掉共模，剩下的差模灵敏度
不再受温补残差限制。

```
相对漂移ᵢ = driftᵢ / t₀ᵢ                        ← 归一化，纯温度作用下 = α·ΔT，与螺栓长度无关
共模      = median{ 相对漂移ⱼ : j ∈ 参考池 }
共模份额ᵢ = 共模 × t₀ᵢ                           ← 按各自声程摊，长螺栓摊得多
差模ᵢ     = driftᵢ − 共模份额ᵢ
松动比ᵢ   = −差模ᵢ / (K_Lᵢ · F_targetᵢ)          ← 正值 = 相对同组松了多少
```

`driftᵢ` 直接用 `measurements.delta_tof_ns`（温补后相对标定基准的声时变化），不引入新采集。

### 3.1 为什么必须先按 t₀ 归一化

温度效应正比于声程（t₀·α·ΔT），而同一法兰上螺栓长度并不相同——
`PeerMedianKlAsync` 的注释记着真机实测两颗同为 M20 的螺栓声时 27 204 与 32 650 ns（差 20%）。
直接对 Δt 取中位数，会把长螺栓的温度份额摊给短螺栓，凭空造出与长度相关的假松动。
单测 `UnnormalizedMedian_WouldFabricateLengthCorrelatedLooseness` 把这个代价钉成了可执行的反例。

### 3.2 参考池：刻意不因 preload_* 告警排除

这条反直觉但关键。温度共模最典型的后果就是**全组一起越限报 preload_low**；
若因"这颗有告警"把它们全排除，参考池恰好在最需要共模抑制的时刻变空。

排除的只是"这颗的 Δt 数值本身不可信"：信号质量不达标（SNR < 12dB，与建档门/标定向导/
告警诊断同口径）、链路可疑（`signal_lost` / `wiring_suspect`）、窗口样本不足。
这类螺栓**仍然拿到自己的差模结论**，只是不投票——信号差不等于免检，最该看的螺栓不能没人管。

中位数（而非均值）承担抗离群：一颗耦合失效的探头漂移可达微秒级
（真机 CH7 假过零 50 356 ns 那个量级），均值会被彻底带偏，中位数最多挪一个序位。

### 3.3 Δt 白名单：一个容易踩的数据语义坑

`measurements.delta_tof_ns` 的语义**依赖 `algo_version`**。查 `PostgresMeasurementSink.TryComputePreload`：

- 本地算过预紧力的路径（`phase1:GbSingleWave*` / `phase1:TorqueFitSingleWave*`）→ 温补后的本地计算值；
- 未标定 / 标定过期 / 未达合格线（`phase1:raw:*`、`calibration_expired`、`calibration_not_passed`）
  → 落的是 **SDK 原始 DeltaTof**，语义不同、未必温补。

混进窗口聚合会污染共模，所以按前缀**正向白名单**放行，宁可少判不可错判。
`out_of_gb_range` / `negative_preload` / `no_target_preload` 这些尾缀仍在白名单内——
它们的 Δt 是本地算的有效值，而且这类螺栓恰恰最可能是真松的，漏不得。

### 3.4 基准时刻跨度守卫

`drift` 是"自各自基准以来的累积变化"，只有基准时刻接近才可直接互比。
风电现场整个法兰一起安装、一起在役锚定，正常跨度应接近 0；但某颗事后单独重标定过，
它的累积起点就与同组不同，差模里会混进"安装时长不同"的成分。
跨度超过 `MaxBaselineSpanDays`（默认 7 天）的组**只落库不告警**，并在页面显性说明——
报了会指错人。每颗的 `baseline_at` 一并落库与上屏，读者才不会把累积量当瞬时量。

### 3.5 参考温度守卫：一个只有差模判据才会被咬到的坑

`CachedChannelResolver` 在 `channels.ref_temp_c` 缺失时按 **20°C 兜底**（注释理由是"GB/T 通用标准
实验室温度"）。热路径照样算出 Δt，看着有数——但那是相对一个**假定**参考温度的值。

真机快照实测参考温度是 24.03°C。差 4°C，在 t₀≈31747 ns 上产生约 13 ns 的系统性偏移，
折算约**满载的 7.5%** —— 正好落在差模默认触发线 0.08 附近，会稳定刷出一颗假松动。

这个坑对绝对判定无害（30% 阈值盖得住 7.5%），**只有灵敏度提高之后才会显形**。
所以差模链路必须自己挡：缺 `ref_temp_c` 的螺栓不进共模、不出松动比，显式落 `no_baseline`
（"基准不完整"），并在结论里说清补什么。集成测试
`RunOnce_MissingRefTemp_IsExcludedInsteadOfFabricatingLooseness` 钉住这条。

一般规律：**把判据灵敏度从 30% 提到 5% 量级，等于把原先被阈值盖住的所有兜底值、缺省值、
推测值一起暴露出来。** 每一个"缺失时用默认值"都要重新过一遍——这是提高灵敏度的必付成本，
也是 `field-consistency-study` §1 R2（推测值冒充事实）在新精度下的复发面。

### 3.6 能力边界（必须随结论一起呈现）

- 差模判的是"**相对同组其他螺栓**"，不是绝对松动量。全组均匀松弛时共模会把它一起吃掉，
  差模趋近 0 —— 所以**绝对判定必须保留作第二道**，两者互补，谁也不替代谁。
  主视图页脚与端点 description 都写明了这一点。
- 组内过半螺栓同时松动时中位数被污染，差模会低估。中位数只保证抗少数离群。
  更强的做法是 MAD 迭代剔除，第一版有意不做——先要一个可预测、可解释的判据。

## 4. 落地清单

| 件 | 位置 |
|---|---|
| 算法（纯函数） | `src/BoltPreload.Algorithms/Drift/CommonModeDrift.cs` |
| 单测 16 条 | `src/BoltPreload.Tests/Algorithms/CommonModeDriftTests.cs` |
| 迁移（两张表） | `deploy/sql/0037_common_mode_drift.sql` |
| 周期服务 + 告警 | `src/BoltPreload.Api/Runtime/CommonModeDriftService.cs` |
| 配置 | `appsettings.json` → `Alarm:CommonModeDrift` |
| 端点（3 个） | `src/BoltPreload.Api/Endpoints/DriftEndpoints.cs` |
| 主视图页 | `web/src/views/BoltVerdict.vue`（路由 `/verdict`，菜单"松紧判定"） |
| 登录落地页 | `web/src/stores/auth.ts` → `homeName` 统一为 `verdict`（见下） |
| 集成测试 8 条 | `src/BoltPreload.Tests/Integration/CommonModeDriftIntegrationTests.cs` |
| 告警类型 `preload_drift` | 四处词表：`DisplayLabels.cs`、`labels.ts`、`DisplayLabelsTests`、`AlarmEndpoints` 白名单 |

两张表的分工：`bolt_drift_status` 按 bolt UPSERT 最新判定（判定与展示的单一事实源）；
`group_drift_samples` 每轮一行存组级共模时序（见 §6）。

告警默认 **warn-only**（`CriticalEnabled: false`）：这是一条新判据，现场还没跑出长期分布，
先只提示不升级，不用一个未标定的阈值制造 critical 风暴。绝对判定的
`preload_loss` / critical 不受影响，安全底线仍在。触发线 `0.08` 同样是保守起步值——
差模已扣温漂，不需要 0.30 那么大的余量，但要等实测分布再收紧。

结论文字与动作建议全部由后端给（`verdict` / `verdictText` / `action`），前端不自己按阈值判：
"页面显示正常而告警在响"这类问题（`field-consistency-study` §0 第 9 条）就是两套口径造出来的。

**登录落地页统一到 `/verdict`。** 原先按角色分流：操作员→资产监测、复核员→变更记录、
管理员→总览。前两个都有问题——操作员落在资产监测，跳过了唯一给出一句话结论的页面，
得自己钻风机→部位才看得到红卡；复核员落在变更记录，那是 2026-08-19 取消异人审批之前的
落地点，现在那页只剩只读历史。现场问的第一个问题永远是"松了还是紧了"，答案页就该是第一眼。
各角色仍可从侧栏进各自常用页，改的只是"第一眼看什么"。

## 5. 怎么验证它真的有效

1. **算法层**：`dotnet test --filter CommonModeDrift`。其中两条是立论本身——
   `Analyze_DetectsLoosenessThatAbsoluteRatioWouldMiss`（同一组数据，绝对判定连 0.90 预警带
   都进不去，差模精确拎出 8%）、`ResidualCommonMode_OfThirteenDegrees_EqualsTheThirtyPercentAlarmLine`
   （13°C 残差 = 30% 假告警线）。
2. **现场层**：跑满一个昼夜后打开 `/verdict`，看每组的"折合等效温差"。
   温补如果是准的，这个数应当在 0 附近**随昼夜小幅摆动**；如果它跟着气温单向走，
   说明 α 不对——这正是 §6 要做的事。
3. **对照层**：同时看总览的绝对保持率。差模为 0 而绝对保持率整体偏低 = 全组均匀松弛或目标配错；
   差模显著而绝对保持率正常 = 单颗真松，这是原来看不见的那一类。

## 6. 下一步：用共模时序反推有效 α

`group_drift_samples` 同时存了 `common_mode_relative` 与组内实测 `avg_temp_c`。
把前者对后者做线性回归，**斜率就是"系统用的 α 与真实 α 之差"**，由此可反推这批螺栓的
有效 α，再决定要不要改 `bolts.temp_coefficient_per_c`。

这是 `field-consistency-study` §4.1 要求的"真机跑完整昼夜、反推有效 α"所需的数据载体——
此前系统没有任何地方记录温补之后的残差，只能人工翻 `measurements` 逐条比对。
端点 `GET /api/drift/groups/{id}/common-mode?hours=48` 直接吐这两列。

α 收敛之后可以接着做两件现在做不了的事：
把差模触发线从保守的 0.08 收到实测分布支持的水平；给趋势告警
（`product-design.md` §8.2）一个扣掉温漂的输入——那件事的前置依赖就是本文。
