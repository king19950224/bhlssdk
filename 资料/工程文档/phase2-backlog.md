# Phase 2+ 受控待办（Roadmap Backlog）

代码里散落着一批 `Phase 2 / Phase 2+ / Phase 3` 标记——这些是**有意为之的分阶段规划**，不是隐藏债务。
本文把它们汇总为一处受控清单，便于跟踪，避免"看似有、实则没接线"的误解。

## 已接线 / 已闭环（本轮确认完成，非债务）
- 告警维护抑制（shelve）：已接入评估派发（`PostgresMeasurementSink.EvaluateAndDispatchAlarmsAsync` → `IsAlarmShelvedAsync`），生效抑制吞掉触发并回滚状态机，过期后自动恢复。
- 绑定/迁移标定动作 `keep_uncomputed`：已实现为"保留目标通道现有标定（no-op）"，与 `invalidate` 区分。
- 前端 `TrendPoint` 重名：已拆为 `DashboardTrendPoint` 与 `TrendPoint`。
- 管理员免审批：`configure-channel` 提交按角色分流（admin 直落 approved + 审计留痕，且请求写入与审计条目同一事务提交）。

以上不再是"半接线"，无需在 Phase 2 处理。

## 代码逻辑评审 · Phase 1–3（已完成）

三轮质量提升已落地，均通过后端 `build` + 108 单测、前端 `vue-tsc`；不变量口径见 [concurrency-model.md](./concurrency-model.md)（§1 会话串行 / §6 标定生命周期 / §7 审计同事务 / §8 前端时间刷新）。

- **Phase 1（CRITICAL/HIGH）**：会话锁覆盖所有会话级命令；看门狗重连判定可测化（本次连接起点计时 + 慢写不重连 + 扫描期跳过）；
  扫描生命周期（`Task.Run(CancellationToken.None)` 保证 finally 释放 + `mcr_single` 快照失败先失败）；`signal_lost` 扫描期跳过；
  入库去重不推进告警连击；shelve 抑制；backfill 清理 `unbound_measurements`；标定 intercept 显式置空、`torque_fit` 仅 R² 达标才激活、按 bolt 温度系数归一。
- **Phase 2（MEDIUM）**：move 源通道清标定；正式标定 `expires_at` 会签起算；生效计数按 `review_state` 过滤；新增 `reject` 接口（含前端驳回按钮）；
  前端 `useNow` 时间心跳（health / `fmtAgo` / Alarms `ago`）；`LivePreload` 剔除失标定通道 + cutoff 随 `now` 滑动。
- **Phase 3（LOW）**：`num()` 防 `NaN`；`boltId` 校验；Diagnostics 显示物理 `channelNo`（后端 `features/recent` 补 `channel_no`）；
  `GroupTrend` 竞态令牌；`configure-channel` 审计并入事务；会话 provisioning 串行。

**剩余（软件外）**：真机/现场验收——`FxSdkConnector` 的会话串行、断线重连时序、真实标定/回波 TOF、probe-scan 等属真机代码，
需接真实设备端跑一遍确认（本地 `SimulatedFxConnector` 不覆盖）。DB 集成测试需 CI 上的 Postgres 执行。

## Phase 2+ 待办（按主题）

### 采集 / 持久化
- 批量 COPY 入库替代单条 INSERT，提吞吐（`PostgresMeasurementSink`，方案 §5.3）。
- 多连接管理 / ConnectorService（`IngestionService`，方案 §5.2）。
- `ChannelResult.Gain` 从 ChannelConfig 带过来（`FxSdkConnector.ToDto`，SDK 1.4 结果无 Gain 字段）。

### 告警 / 实时
- 告警状态机持久化改 Redis（当前进程内内存 + 重启从 DB 重建）（`IAlarmEvaluator`）。
- 实时推送可换 MQTT / Kafka / WebSocket bridge（`IMeasurementPublisher`，当前 SignalR）。
- SignalR 按 `bolt_id` / `device_id` 分 group 订阅（`MeasurementsHub`）。
- 告警中心审查遗留低优先级项（M4 审查确认、暂不修）：
  - preload_low→preload_loss 升级时旧 low 行记 `cleared_reason='auto_recovered'`（实际是恶化非恢复，历史语义误导；改法需给 AlarmEvent 加 reason 字段）；
  - HighActive 期间 ratio 骤降至脱落区时 loss 分支提前 return，preload_high 恢复计数不推进（高+脱落双开直至修复）；
  - 前端告警列表 limit=300 静默截断（摘要卡片是全量口径，可能对不上）；
  - SignalHealth 监测窗 360min：服务停机期间开始且超窗的静默，重启后不补报 signal_lost。

### 算法 / 标定
- GB 单波法 Phase 2、双波法 Phase 3+（`IPreloadCalculator` / `GbBiWaveCalculator` 目前为安全 stub，`IsAvailable=false` 自动回退单波法）。
- 严格多点最小二乘标定工作站（含 R²/不确定度/双签）（`CalibrationEndpoints` 注释所述）。

### 平台 / 运维
- License 正式签名校验（RSA/ECDSA）+ 授权生成工具（`ILicenseService`）。
- 查询走 MediatR + Application 层 Query Handler（`MeasurementEndpoints`）。
- 上线前关闭 SDK 协议帧调试日志（`NLog.config` / `Program.cs`，Phase 3）。
- `bolt_status` 每螺栓 LATERAL 最新测量是资产页/总览/大屏共用的最热查询模式，规模上千后需物化视图或缓存
  （`AssetEndpoints` / `DashboardEndpoints` / `BoltEndpoints` 列表）。当前规模（8–40 颗）无感。
- `Calibration.vue` / `LivePreload.vue` 仍全量拉 `boltApi.list()`（列表护栏默认 1000 条）；规模上千后应改为按
  组/设备过滤的服务端查询。详情页已改单查 `GET /api/bolts/{id}`，不受影响。

## 安全口径拍板记录（M3，技术负责人确认）
- **报告列表/详情/下载维持 LoggedIn**：报告快照内容（预紧力/标定数据）任何已登录角色在总览、螺栓详情页本来就可见，
  收紧到 reviewer+ 只会堵死现场核数场景；生成/签发仍为 reviewer+（`ReportEndpoints`）。
- **commission 调试直发收紧为 reviewer+**：随诊断组策略走，不再单独放宽到 WriteAccess——初调闭环本来就依赖组内
  其他 reviewer+ 端点（读参/对波形/set-mode），operator 单独可写只剩绕过审批的风险（`DiagnosticEndpoints`）。
- **迁移螺栓允许源通道已软删**：保留。这是把螺栓从报废通道救出来的合法恢复路径（`MeasurementEndpoints.MoveBoltBindingAsync`）。

## 说明
- 这些标记不影响当前生产链路正确性；`GbBiWaveCalculator` 等未实现项都有守卫（`IsAvailable=false` 回退），不会路由到未实现分支。
- 新增此类"未来阶段"占位时，请在代码注释保留 `Phase N` 标记并在本文追加一行，保持单一受控清单。
