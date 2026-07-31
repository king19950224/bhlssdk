# 现场调试报告 — 2026-05-21

> 张备实验风场设备 `192.168.0.7:23`，通道 9（已拧紧螺栓）
> 工控机所在地：张备实验风场
> 设备所在地：实验室（远程连通）

## 一、问题陈述

设备已上线，TCP 心跳正常，`ChannelResult` 持续上行，但 **`EchoTimeNs` 大部分为空**，导致系统无法计算 TOF 变化和预紧力。

| 指标 | 数值 |
|---|---|
| 测量条数（总） | 20,947 条 |
| `EchoTimeNs` 非空 | 840 条（约 **4%**）|
| `EchoTimeNs` 范围 | 32236.64 ~ 32244.24 ns |
| 当前 `SpTemp` | 23.30~23.42 °C |
| 设备配置 `RefTemp` | **16.71 °C** ← 关键 |
| 设备配置 `RefTime` | 32142 ns |
| `Gain` | 16 |
| `ProbeFreq` 索引 | 2 |
| `MeasMode` | 0 (自动) |
| `Threshold` | -490 |
| `LEchoWave` | 完整波形可上传（~500 采样点，回波峰清晰）|

## 二、关键证据

### 2.1 设备波形是好的

通过 SDK `ReadChannelParam(LEchoWave)` 上传的通道 9 当前回波，500 个采样点内可清楚分辨：

- 主回波峰位于约采样索引 320-350（峰峰值 ~900 LSB）；
- 第二次回波在 380-415（峰峰值 ~500 LSB）；
- 噪声基底约 ±30 LSB；
- **信噪比目测 ≥ 30 dB**。

这说明 **物理通路（探头/胶层/导线/AFE 模拟前端）完全正常**。
不是探头脱落、不是放大坏掉、不是导线断。

### 2.2 软件链路是通的

启动 12:13 之后 2 分钟内，1198 条 `ChannelResult` 全部按时入库，无掉包：

```sql
SELECT COUNT(*) FROM measurements WHERE ts > '2026-05-21 04:13:00+00';
-- 1198
```

API 端 `device_parameter_history` 表也正确记录了
`BatVoltage` / `Gain` / `RefTime` / `RefTemp` / `MeasMode` / `Threshold` / `LEchoWave` 等参数上行。

### 2.3 设备 onboard TOF 算法选择性地不出值

`ChannelResult` 协议字段齐全（`SpTemp`/`Gain`/`Address`/`Channel` 都有），唯独 `EchoTime` 时有时无：

- 之前 24 小时累计：840 / 20947 ≈ **4% 通过率**；
- 今天最近 2 分钟：**0 / 1198 = 0%**。

成功的那 4% 里，`EchoTime` 都落在 32236-32244 ns，相对 `RefTime=32142 ns` **偏离约 +100 ns**。

## 三、根因分析

### 3.1 主因：温度偏离 RefTemp 太远

设备出厂标定温度 16.71 °C，当前室温 23.4 °C，**温差 ≈ 6.7 °C**。

钢材声速温度系数约 −0.07‰/°C（典型值，见 GB/T 43232-2023 附录 B），即温度每升高 1 °C，TOF 增大约 0.07‰。

对 32142 ns 的基线，6.7 °C 温升带来的 TOF 漂移：

```
ΔTOF ≈ 32142 × 0.07e-3 × 6.7 ≈ 15.1 ns
```

加上螺栓真实预紧力变化（典型几十 ns），叠加起来轻易能让 `EchoTime` 偏离 `RefTime` 超过 100 ns。

设备 onboard 算法可能有以下行为之一（无 SDK 算法源码、仅靠协议反推）：

- **波形匹配窗口过窄**：当回波相对 `RefTime` 漂移超过窗口宽度时，算法找不到匹配位置直接放弃；
- **波形相似度阈值过严**：温度大幅变化导致波形微小畸变（声速分布不均匀），相似度降到接受阈值以下；
- **温度补偿没生效**：`TcMode` 默认值未启用，算法只看几何匹配不做温度修正。

### 3.2 次因：MeasMode = 0（自动模式）

`MeasMode = 0` 在 SDK 文档里是"自动"。但实际行为受 `Threshold` 配合影响。
`Threshold = -490` 在 SDK 内是有符号 16 位整数，对应 **去-490 LSB 以下** 的阈值搜索逻辑，
属于厂家默认值，对当前噪声基底 ±30 LSB 来说不算严格。

### 3.3 不是的可能性（已排除）

| 假设 | 验证方法 | 结论 |
|---|---|---|
| 探头脱落 | 看 `LEchoWave` 波形 | 排除：回波清晰，物理 OK |
| AFE 损坏 | 看噪声基底和信号幅度 | 排除：SNR 高，AFE OK |
| 软件没把 EchoTime 解码出来 | 看协议 NLog 原始帧 + DTO 字段 | 排除：协议里就是 null |
| 网络丢包 | 测量条数对得上心跳节奏 | 排除：无丢包 |
| 温度传感器坏 | `SpTemp` 稳定在 23.4 °C，符合室温 | 排除：传感器 OK |

## 四、解决路径（按风险/收益排序）

### 路径 A：在线重标定（推荐，零硬件改动）

把当前实际温度（23 °C）和当前能测到的 TOF 写成新的 `RefTime`/`RefTemp`，让设备 onboard 算法重新对齐基线。

**前置条件**：螺栓必须处于已知预紧力状态（拧紧到目标力矩或自由状态），否则基线没意义。

**操作**（系统已实现）：

```bash
# 路径 A1：自动用最近一次有效测量做基线（推荐，最准确）
POST /api/devices/1/channels/9/recalibrate

# 路径 A2：手动指定（设备完全测不出 TOF 时的兜底）
POST /api/devices/1/channels/9/recalibrate
Content-Type: application/json
{
  "refTimeNs": 32240,
  "refTempC": 23.4
}
```

SDK 协议层会一次性把 `MeasMode + Threshold + RefTime + RefTemp + LEchoWave` 整组写到设备
（这是协议硬约束，参见 `MultiChIotProtocolHandler.cs:269-294`）。

预期：写完 10 秒后，`measurements.echo_time_ns` 应稳定有值，且围绕新 RefTime 的 ±50 ns 之内。

### 路径 B：找厂家调"波形匹配相似度阈值"

当前 SDK 没有暴露这个参数。**需联系上海法柯**：

1. 提供今天的 `LEchoWave` 实测样本（JSON 已落库 `device_parameter_history`）；
2. 提供 `EchoTime` 通过率统计（4%）；
3. 询问能否下调相似度匹配阈值或扩大波形匹配窗口。

### 路径 C：把设备搬到实际安装环境再标定

最稳妥但成本最高。出厂标定温度 16.71 °C 离风场实际工况偏低，建议**在实际工况下重做出厂标定**。

## 五、本次调试期间软件侧已经做的事

| # | 事项 | 状态 | 文件 |
|---|---|---|---|
| 1 | `device_parameter_history` 表，永久保留设备参数读取流水 | 已完成 | `deploy/sql/0002_device_parameter_history.sql` |
| 2 | `PostgresMeasurementSink` 自动落 `DeviceParameter`（含 LEchoWave JSON） | 已完成 | `src/BoltPreload.Infrastructure/Ingestion/PostgresMeasurementSink.cs` |
| 3 | API：参数历史查询、通道当前快照 | 已完成 | `src/BoltPreload.Api/Endpoints/MeasurementEndpoints.cs` |
| 4 | API：远程读设备/通道参数（运维不去现场） | 已完成 | `src/BoltPreload.Api/Endpoints/DiagnosticEndpoints.cs` |
| 5 | API：远程改通道参数（`Gain`/`ProbeFreq`/`EchoWin` 等） | 已完成 | 同上 |
| 6 | API：通道重标定（自动或手动指定基线） | 已完成 | 同上 |
| 7 | `IFxConnector` 抽象，真机/模拟器同接口 | 已完成 | `src/BoltPreload.Fx/IFxConnector.cs` |
| 8 | `MultiChannelResult` 拆分入库 | 已完成 | 同 `PostgresMeasurementSink` |

## 六、给业主/厂家的话

- 软件侧的接入工作**已完成**，数据通路、解码、存储、查询都验证过。
- 当前问题**不在软件侧**，软件能"看到"波形但等不到设备算出 TOF。
- 推荐先走**路径 A**（在线重标定）：到现场后用 API 触发一次重标定，10 秒内见效。
- 如果路径 A 失败，再走**路径 B**联系厂家或路径 C 重新出厂标定。

## 七、附录：参考接口示例

### 7.1 查看通道当前参数（远程，无需到现场）

```bash
curl http://<controller>:5000/api/devices/1/parameters/channel/9/snapshot
```

返回包含：`Gain` / `ProbeFreq` / `MeasMode` / `RefTime` / `RefTemp` / `Threshold` / `LEchoWave`。

### 7.2 触发上行读取（按需）

```bash
# 读电池电压（设备级）
curl -X POST 'http://<controller>:5000/api/devices/1/diagnostic/read-device-param/BatVoltage'

# 读通道 9 当前波形（最有用的诊断接口）
curl -X POST 'http://<controller>:5000/api/devices/1/diagnostic/read-channel-param/LEchoWave?channel=9'
```

参数响应 5 秒内回来，自动落到 `device_parameter_history` 表。

### 7.3 通道当前 EchoTime 接受率

```sql
SELECT
  DATE_TRUNC('hour', ts) AS hour,
  COUNT(*) AS total,
  COUNT(echo_time_ns) AS valid,
  ROUND(100.0 * COUNT(echo_time_ns) / COUNT(*), 1) AS valid_pct
FROM measurements
WHERE channel_id = 1 AND ts > now() - interval '24 hours'
GROUP BY 1 ORDER BY 1 DESC;
```

---

**报告人**：BoltPreload 软件项目组
**时间**：2026-05-21
**版本**：v1（实验室设备初次接入）
