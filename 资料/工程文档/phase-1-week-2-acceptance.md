# Phase 1 第 2 周自验收 — 现场接入与生存性

> 时间：2026-05-21
> 范围：Day 2（设备参数持久化、重标定、现场诊断）+ Day 3（通道自动发现、波形持久化、实时推送）

## 1. Day 2 — 设备参数留存与诊断闭环

### 1.1 `device_parameter_history` 表

- Schema：`deploy/sql/0002_device_parameter_history.sql`
- 字段：`device_id` / `address` / `channel_no(NULL=设备级)` / `param_name` / `param_value_text` / `param_value_jsonb` / `received_at` / `ingested_at`
- 简单类型走 text，复杂对象（PulseConfig / LEchoWave / ChannelConfig）走 JSON
- 索引：`(device_id, param_name, received_at DESC)` + `(device_id, channel_no, received_at DESC) WHERE channel_no IS NOT NULL`

### 1.2 `PostgresMeasurementSink` 路由

| envelope 类型 | 落表 |
|---|---|
| `ChannelResult` | `measurements` 或 `measurement_anomalies` |
| `MultiChannelResult` | 拆开成 N 个 `ChannelResult` 逐个写 |
| `DeviceParameter` | `device_parameter_history` |

实测真机参数读取 9 条全部正确入库：

```
 id | channel_no |   param_name    | param_value_text |          received_at          
----+------------+-----------------+------------------+-------------------------------
  9 |            | BatVoltage      | 0                | 2026-05-21 12:05:09.804416+08
  8 |          9 | ProbeFreq       | 2                | 2026-05-21 12:05:08.144074+08
  7 |          9 | Gain            | 16               | 2026-05-21 12:05:07.313093+08
  4 |          9 | RefTime         | 32142            | 2026-05-21 12:05:05.721968+08
  6 |          9 | LEchoWave       |                  | 2026-05-21 12:05:05.721968+08    ← JSON 2466 字节
  5 |          9 | RefTemp         | 16.71            | 2026-05-21 12:05:05.721968+08
  2 |          9 | MeasMode        | 0                | 2026-05-21 12:05:05.721968+08
  3 |          9 | Threshold       | -490             | 2026-05-21 12:05:05.721968+08
```

### 1.3 重标定（`/api/devices/{id}/channels/{ch}/recalibrate`）

实现：`FxSdkConnector.RecalibrateChannelAsync`

- 缓存策略：connector 内部维护 `ConcurrentDictionary<channel, ChannelResult>` 和 `ConcurrentDictionary<channel, EchoWave>`，每条上行更新
- 自动模式：取最近一次有效 `ChannelResult` 的 `EchoTime / SpTemp` 作为新基线
- 兜底模式：当设备测不出 TOF 时，请求体可传 `{ "refTimeNs": ..., "refTempC": ... }` 强制指定
- SDK 协议约束：一次写整组 `MeasMode + Threshold + RefTime + RefTemp + LEchoWave`（不能拆，见 `MultiChIotProtocolHandler.cs:269`）
- 已限制 `ConfigureChannelAsync` 只能写 SDK 协议层真实支持的 5 个属性（`ProbeFreq`/`EchoWin`/`EchoWidth`/`PulseConfig`/`Gain`），其他属性引导调用方走 `/recalibrate`

### 1.4 现场诊断报告 `资料/工程文档/site-debug-2026-05-21.md`

业主/厂家可读的诊断报告，定位当前 `EchoTime` 接受率仅 4% 的根因（温度偏离 RefTemp 6.7°C），并给出三条解决路径（在线重标定 / 找厂家调阈值 / 重做出厂标定）。

## 2. Day 3 — 数据不丢失 + 波形可回放 + 实时可视化

### 2.1 通道自动发现

- Schema 改动：`deploy/sql/0003_unbound_measurements_and_autodiscover.sql`
  - `channels.state`（manual / auto_discovered / retired），`first_seen_at` / `last_seen_at`
  - 新表 `unbound_measurements`：channel 已发现但 bolt 未绑定时的暂存
- `PostgresMeasurementSink.ResolveOrDiscoverAsync`：4 种路由
  1. channel 已知 + bolt 已绑定 → `measurements`
  2. channel 已知 + bolt 未绑定 → `unbound_measurements`
  3. channel 未知 → 自动 INSERT（state='auto_discovered'）→ 走情况 2
  4. INSERT 失败（极端） → 真丢，但有 ERROR 日志
- 接口：
  - `GET /api/channels/?state=auto_discovered` 列自动发现的通道
  - `GET /api/channels/unbound/summary` 哪些通道在等 bolt 绑定
  - `GET /api/channels/unbound` 未绑定测量明细

实测：临时把数据库种子 channel_no=9 改成 99，让真机 ch=9 触发自动发现，**219 条数据全部进 unbound_measurements，0 丢失**。验证完恢复种子。

### 2.2 波形持久化

- 抽象 `IWaveStore`（`BoltPreload.Application.Ingestion.IWaveStore`）
- 实现 `LocalFileWaveStore`（`BoltPreload.Infrastructure.Ingestion`）
  - 路径规则：`{RootPath}/{deviceId}/{channelNo}/{yyyyMMdd}/{tsUnixMs}_{ptCount}.json`
  - JSON 用 camelCase，便于前端 / 算法工具直接读
  - 安全：拒绝 `..` 路径遍历，`Path.IsPathRooted` 防止绝对路径注入
  - 容量保护：超 `MaxFileBytes`（默认 1MB）自动截断 + 警告
- `PostgresMeasurementSink.WriteMeasurementAsync` 调用 `IWaveStore.SaveAsync` 拿相对路径填入 `measurements.wave_blob_url`，失败不阻塞主入库
- 接口：
  - `GET /api/waves/recent?boltId=&limit=` 最近带波形的测量
  - `GET /api/waves/file?path=...` 下载波形 JSON

实测（模拟器 15 秒，3 通道 × 5Hz，每 10 个 tick 附一次波形）：
- 22 个波形文件落盘 `data/waves/1/{2,3}/20260521/*.json`
- 60 条 `measurements.wave_blob_url` 非空
- 单元测试 4 个全通过（含路径遍历防护、文件不存在、超大文件截断）

### 2.3 实时推送（SignalR Hub 雏形）

- 抽象 `IMeasurementPublisher`（Application 层不依赖 SignalR）
  - `SignalRMeasurementPublisher`（Api 层）— SignalR 实现
  - `NoopMeasurementPublisher` — 无 web 部署时的零开销实现
- Hub 端点：`/hubs/measurements`
- 推送事件：
  - `OnMeasurement`：ChannelResult / MultiChannelResult 拆单（含 EchoTimeNs / SpTempC / hasWave）
  - `OnDeviceParameter`：参数读取上行
- 失败不阻塞主流程（IngestionService 用 `_ = publisher.PublishAsync(...)` fire-and-forget）
- 演示页：`src/BoltPreload.Api/wwwroot/realtime.html`（原生 SignalR.js，自动重连）

实测：
- `POST /hubs/measurements/negotiate?negotiateVersion=1` 返回 connectionId + 三种传输方式可选
- `GET /realtime.html` 200 OK，4999 字节
- 后端日志确认 `IMeasurementPublisher` 被注入并随每条 envelope 调用

## 3. 单元测试

```
失败: 0  通过: 11  跳过: 0  总计: 11
```

新增测试：
- `LocalFileWaveStoreTests.Save_then_Read_round_trips` — 写读 round-trip
- `LocalFileWaveStoreTests.Read_rejects_path_traversal` — 安全性
- `LocalFileWaveStoreTests.Read_returns_null_for_missing_file` — 健壮性
- `LocalFileWaveStoreTests.Save_truncates_when_over_limit` — 容量保护

## 4. 数据库当前状态

| 表 | 行数 | 说明 |
|---|---|---|
| channels | 3 | 原始种子（ch=9/2/3）|
| bolts | 3 | 原始种子 |
| measurements | 34079 | 累计实测 + 模拟器测试数据 |
| measurements (带 wave_blob_url) | 408 | 模拟器测试样本 |
| unbound_measurements | 0 | 测试后已清理 |
| device_parameter_history | 11 | 真机参数读取历史 |

## 5. 未完成 / 推到下周

- **缓存层**：channels / bolts 解析每条都查 DB（hot path），Day 4 加 `IMemoryCache`
- **通道绑定 API**：业主在前台把 `auto_discovered` channel 绑定到 bolt 后，把对应 `unbound_measurements` backfill 到 `measurements` 的迁移脚本（Day 5）
- **SignalR group**：当前所有客户端订阅一个流，Day 6 加按 device / bolt 分组
- **批量入库**：Phase 2 改 `COPY` 提高吞吐（当前单条 INSERT，约 50/s 单进程）
- **波形云端备份**：Phase 3 把 `LocalFileWaveStore` 替换成 S3/MinIO/OSS 实现

## 6. 配置项汇总

`src/BoltPreload.Api/appsettings.json` 新增：

```json
{
  "WaveStore": {
    "RootPath": "data/waves",
    "MaxFileBytes": 1048576
  }
}
```

## 7. 扭矩法临时标定接口

接口：`POST /api/channels/{channelId}/torque-calibrate`

用途：现场只有扭力扳手读数、没有拉伸机/载荷传感器时，先按经验公式把系统跑起来。

公式：

```text
T = K × F × d
F = T / (K × d)
```

示例：实验室 M20×80 螺栓，扭力扳手拧到 200 N·m，临时取扭矩系数 `K=0.20`：

```text
F = 200 / (0.20 × 0.020) = 50,000 N
```

系统随后取最近真实 EchoTime 均值，按 SDK 温补公式算 `Δt`，再反推：

```text
K_L = Δt / F
```

接口会：

- 写 `calibration_records(method='torque_estimate')`
- 更新 `channels.k_l / ref_time_ns / ref_temp_c / active_calibration_id`
- 更新 `bolts.install_torque_nm / target_preload_n`
- 写 `audit_log`
- 失效通道路由缓存

限制：

- 这是**临时工程估算**，不是 GB/T 43232-2023 严格标定；
- `K` 受螺纹/端面摩擦、润滑、垫片状态影响很大；
- 正式验收必须用多点加载标定替换。

## 8. 采集看门狗（断线/无数据自动恢复）

实现：

- `IIngestionRuntimeStatus`：进程内记录最近 envelope / ChannelResult 时间。
- `IngestionWatchdogService`：定时检查连接状态和最近数据时间。
- `IFxConnector.ReconnectAsync()`：安全重连，不关闭结果通道。
- `FxSdkConnector.DisconnectAsync()` 不再 `TryComplete()` 结果通道，避免 `IngestionService` 消费循环永久退出。

默认配置：

```json
{
  "Ingestion": {
    "Watchdog": {
      "Enabled": true,
      "CheckIntervalSeconds": 10,
      "NoDataTimeoutSeconds": 60,
      "StartupGraceSeconds": 30,
      "ReconnectCooldownSeconds": 30,
      "Address": 1,
      "WorkMode": "MultiMeas"
    }
  }
}
```

真实验证（2026-05-22）：

1. API 正常连接真机，EchoTime 接受率 100%。
2. 手动下发 `Standby`，让设备停止上行测量。
3. 62 秒无 ChannelResult 后，Watchdog 触发重连。
4. 重连完成后自动下发 `MultiMeas`。
5. 真机恢复：

| 指标 | 恢复后 |
|---|---:|
| 最近 1 分钟测量 | 199 条 |
| EchoTime 有效 | 199 条 |
| EchoTime 接受率 | 100% |
| preload 有效 | 202 条 |
| 平均预紧力 | 51.13 kN |

结论：设备断线、模式被误切、TCP 长连接假死后，系统可自动恢复采集。
