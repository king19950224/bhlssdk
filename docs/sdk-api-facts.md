# FxSDK C# 1.4 真实 API 事实清单

这是从厂家 C# SDK 1.4 源码（`external/FxSDK/`）抽出来的事实清单。开发中遇到 SDK 字段、状态码、常量问题先查这里。

**为什么单独写**：方案 plan.md 早期版本基于 Java SDK 1.0 写，跟 C# SDK 1.4 有差异。本文档以 C# SDK 1.4 源码为准，任何不一致以本文档为准。

**更新规则**：升级 SDK 版本时（`external/FxSDK/` 替换后）必须同步更新本文档，否则 PR 不许合。

---

## 1. ChannelResult 字段

文件：[external/FxSDK/Models/MeasResult.cs](../external/FxSDK/Models/MeasResult.cs)

| 字段 | C# 类型 | 必/可空 | 说明 |
|---|---|---|---|
| `Address` | `ushort` | 必（默认 1） | 设备地址，**不是** Java 1.0 的 `short` |
| `Channel` | `ushort` | 必（默认 1） | 通道号 |
| `StatusCode` | `short` | 必（默认 0） | 状态码，见 §2 |
| `EchoTime` | `decimal?` | 可空 | 纵波 TOF（ns） |
| `EchoTime2` | `decimal?` | 可空 | **第二回波 TOF（双波法用），Java 1.0 没有** |
| `SpTemp` | `decimal?` | 可空 | 温度（°C） |
| `DeltaTof` | `decimal?` | 可空 | 声时差（ns），SDK 已做温补 |
| `EchoWave` | `EchoWave?` | 可空 | 波形（见 §3） |
| `LowAi` | `ushort?` | 可空 | 模拟低电平回读 |
| `HighAi` | `ushort?` | 可空 | 模拟高电平回读 |
| `Angle` | `int?` | 可空 | 角度计数 |
| `ParamsArray` | `decimal[]` | **可能 null** | 解码参数数组，SDK 没初始化默认值 |
| `MidResults` | `decimal[]` | **可能 null** | 算法中间结果，SDK 没初始化默认值 |
| `ReceiveTime` | `DateTime` | 非 null | 来自基类 `DecodedResult`（**不是 nullable**） |
| `HexString` | `string?` | 可空 | 来自基类，原始字节 16 进制 |

**注意**：

- `ChannelResult` **没有** `Gain` 字段（增益在 `ChannelConfig.Gain`，不是测量结果的一部分）
- `ParamsArray` 和 `MidResults` 是 `decimal[]` 不是 `decimal[]?`，但 SDK 没初始化，**实际可能为 null**，访问前要判 null
- `EchoTime2` 在 Java SDK 1.0 没有，C# SDK 1.4 才有
- `EchoTime` **不是设备直接给的最终值**。在 `MultiChIotProtocolHandler.ProcessResult()` 中，SDK 会拿上行波形按 `ChannelConfig` 计算 `EchoTime`。如果 `SessionContext` 里没有对应 `(address, channel)` 的 `ChannelConfig`，或者 `ProbeFreq <= 0`，SDK 不会进入计算分支，`EchoTime` 会一直是 `null`，但 `StatusCode` 仍可能是默认 `0`。

## 1.1 EchoTime 计算前置条件（非常关键）

文件：

- `external/FxSDK/Codec/MultiChIotProtocolHandler.cs`
- `external/FxSDK/Core/SessionContext.cs`
- `external/FxSDK/FxClient.cs`

关键逻辑：

```csharp
var channelConfig = SessionContext.GetChannelConfig(session, chResult.Address, chResult.Channel);

if (!rawData && channelConfig is { ProbeFreq: > 0 } && chResult.EchoWave is { WaveData.Count: > 2 })
{
    // BandPassFilter → InterpolateAndResample → CalcZeroIndex...
    chResult.EchoTime = ...
}
```

实际接真机时必须做：

1. `ConfigureDeviceAsync(..., sendCommand:false)` 把 `DeviceConfig` 存到 SDK `SessionContext`。
2. `ConfigureChannelAsync(..., sendCommand:false)` 把每个正在监测的 `ChannelConfig` 存到 SDK `SessionContext`。
3. 再下发 `SetWorkModeAsync(MultiMeas)`。

`sendCommand:false` 只写 SDK 本地会话，不写设备参数。这个步骤缺失时，真实设备仍会持续上行波形和温度，但 `EchoTime` 会是 `null`。

## 2. StatusCode 状态码

文件：[external/FxSDK/Models/MeasResult.cs:27-36](../external/FxSDK/Models/MeasResult.cs)

| 值 | C# SDK 1.4 含义 | Java SDK 1.0 含义 | 处理建议 |
|---|---|---|---|
| `0` | 正常 | 正常 | 入 measurements 表 |
| `-1` | 信号过小 | 信号过小 | 入 measurement_anomalies；建议检查耦合或增益 |
| `-2` | 阈值异常 | 阈值异常 | 入异常表；建议调整 threshold 或跑 AGC |
| `-3` | **信号溢出** | （Java 没有此码） | 入异常表；建议降低 gain |
| `-4` | **相似度低** | 未找到过零点 | 入异常表；可能耦合不良或螺栓状态变化 |
| `-5` | 未知异常 | 未知异常 | 入异常表；进一步排查 |
| `-6` | 信噪比低 | 信号 SNR 太差 | 入异常表；建议重涂耦合剂 |

**`-3` 和 `-4` 的差异是 C# SDK 跟 Java SDK 关键不一样的点**。我们用 C# SDK，所以以 C# 定义为准。

## 3. EchoWave 字段

文件：[external/FxSDK/Models/MeasResult.cs:82-104](../external/FxSDK/Models/MeasResult.cs)

| 字段 | C# 类型 | 默认值 | 说明 |
|---|---|---|---|
| `EchoStart` | `uint` | 0 | 波形开始时间（ns），**不是 Java 的 int** |
| `ClockInt` | `ushort` | 10 | 时钟间隔（ns），默认值 10 仅占位，**实际由设备返回**（model=3 = 125ns，model=2 = 10ns） |
| `WaveData` | `List<short>?` | null | 波形数据（16-bit 有符号） |
| `Fc` | `ushort` | 0 | **中心频率（0 = 原始未处理）**，类型是 ushort 不是 decimal/BigDecimal |
| `Thr` | `short?` | null | 测量阈值 |

## 4. SignalProcessor 常量

文件：[external/FxSDK/Utility/SignalProcessor.cs:14-32](../external/FxSDK/Utility/SignalProcessor.cs)

| 常量 | 值 | 说明 |
|---|---|---|
| `FilterOrder` | 21 | 默认 FIR 滤波器阶数 |
| `SIGNAL_EXCELLENT` | 30.0 dB | 信号优秀 |
| `SIGNAL_GOOD` | 20.0 dB | 信号良好 |
| `SIGNAL_FAIR` | 10.0 dB | 信号一般 |
| `SIGNAL_POOR` | 6.0 dB | 信号较差（低于此 SDK 返回 statusCode = -6） |

## 5. CalculateExtensions 常量

文件：[external/FxSDK/Utility/CalculateExtensions.cs](../external/FxSDK/Utility/CalculateExtensions.cs)

| 常量 | 值 | 说明 |
|---|---|---|
| `SoundVelocity` | 5890.0 m/s | 碳钢默认纵波声速 |
| `DefaultTempCoeff` | 0.00010269 (即 1.0269e-4) /°C | 综合温度补偿系数（碳钢工程实测值，包含声速温度系数+热膨胀+弹模温变的综合效应） |
| `StandardTemperature` | 22.22 °C | 标定参考温度 |

## 6. MultiChIotProtocolHandler 常量（model=3 多通道监测版）

文件：[external/FxSDK/Codec/MultiChIotProtocolHandler.cs](../external/FxSDK/Codec/MultiChIotProtocolHandler.cs)

| 常量 | 值 | 说明 |
|---|---|---|
| `SAMPLE_RATE` | 8,000,000 Hz (8 MHz) | **原始采样率，不是 100 MHz** |
| `ORIGINAL_INTERVAL` | 125 ns | 对应 8 MHz 的采样间隔 |
| `INTERPOLATION_INTERVAL` | 25 ns | SDK 内部插值后等效 40 MHz |
| `NOISE_BASE` | 50 | 噪声基线 |
| `SIGNAL_MAX` | 700 | 信号最大值 |
| `SIGNAL_AUTO` | 600 | AGC 默认目标峰值 |
| `GAIN_RANGE` | [1, 47] | 棋子平台增益范围（**model=3 限制**） |
| `GAIN_STEP` | 1.0 | 增益步长 |
| `BOARD_CHANNEL_MAX` | 48 | 单板最多通道数 |
| `STORAGE_MAX_PTS` | 120 | 设备本地存储最大条数 |
| `QUANTIZATION_UNIT` | 1000 ns | 回波窗口量化单位 |
| `LORA_WAIT_MS` | 400 | LoRa 等待 |
| `LORA_CH_MAX` | 79 | LoRa 信道上限 |

**帧结构**：

| 标识 | 字节 | 用途 |
|---|---|---|
| `FRAME_HEAD` | `0xDE 0xDE 0xDE 0xDE` | 帧头 |
| `FRAME_TAIL` | `0x00 0x00 0x00 0x00` | 帧尾 |
| `WAKEUP_HEAD` | `0xF0 × 5` | LoRa 唤醒头 |

**命令码**：

| 命令 | 字节 | 用途 |
|---|---|---|
| `WRITE_PARAM_CMD_CODE` | `0x21` | 写参数 |
| `READ_PARAM_CMD_CODE` | `0x22` | 读参数 |
| `WRITE_STORAGE_CMD_CODE` | `0x41` | 写本地存储 |
| `READ_STORAGE_CMD_CODE` | `0x42` | 读本地存储 |

## 7. IotR24ProtocolHandler 常量（model=2 实时 24 通道版）

文件：[external/FxSDK/Codec/IotR24ProtocolHandler.cs](../external/FxSDK/Codec/IotR24ProtocolHandler.cs)

| 常量 | 值 | 说明 |
|---|---|---|
| `OriginalInterval` | 10 ns | **采样间隔 10ns，对应 100 MHz**（跟 model=3 完全不同） |
| `NoiseBase` | 10 | 噪声基线 |
| `SignalAuto` | 90 | AGC 目标 |
| `SignalMax` | 110 | 信号最大 |
| `GainRange` | [0, 80] | 增益范围 |
| `GainStep` | 0.1 | 增益步长 |
| `UltraCount` | 24 | 固定 24 通道 |
| `EchoTimeUnit` | 0.625 ns | 回波时间量化 |
| `TempUnit` | 0.00625 °C | 温度量化 |

**帧头不同于 model=3**：

| 标识 | 字节 |
|---|---|
| 编码帧头 | `0xEF` |
| 编码模拟量帧头 | `0xEE` |
| 解码结果帧头 | `0xDE × 4` |
| 解码波形帧头 | `0x70 × 4` |
| 解码全波帧头 | `0x72 × 4` |
| 解码帧尾 | `0x00 × 4` |

## 8. WorkMode 枚举（关键，方法签名用）

文件：[external/FxSDK/Models/Config/DeviceConfig.cs:258-269](../external/FxSDK/Models/Config/DeviceConfig.cs)

```csharp
public enum WorkMode
{
    Standby,       // 0 待机
    SingleMeas,    // 1 单通道测量
    MultiMeas,     // 2 多通道测量
    DualMeas,      // 3 双波测量
    ReadParam,     // 4 读取参数
    FullWaveRead,  // 5 读全波
    Reboot,        // 6 重启
    LWaveRead,     // 7 纵波扫波
    TWaveRead      // 8 横波扫波
}
```

我们的 `FxWorkMode` 严格对齐这个数值顺序，便于强转。

## 9. DeviceConfig 关键字段

文件：[external/FxSDK/Models/Config/DeviceConfig.cs](../external/FxSDK/Models/Config/DeviceConfig.cs)

| 字段 | 类型 | 说明 |
|---|---|---|
| `Model` | `short` | 设备型号 0/1/2/3（**SDK 1.4 四种都已实现**，但 model=2/3 是主力） |
| `CommunicationType` | `byte` | 0 串口 / 1 TCPClient（默认）/ 2 TCPServer beta |
| `CommunicationParameters` | `string` | TCPClient 格式 `"IP:Port"`，串口 `"COM3:115200,0,8,1"`，TCPServer `"IP:Port,监听端口"` |
| `BaudRate` | `int` | 默认 115200 |
| `TransferMode` | `short` | 0 LoraMesh / 1 直连 |
| `Address` | `short` | 终端地址，默认 1 |

`GetSessionId()` 是 `DeviceConfig` 上的方法（不在 FxClient 上），用于在连接事件回调里拿当前会话标识。

## 10. FxClient 关键事件

文件：[external/FxSDK/FxClient.cs](../external/FxSDK/FxClient.cs)

| 事件 | 签名 |
|---|---|
| `OnConnected` | `EventHandler` |
| `OnDisconnected` | `EventHandler<SessionDisconnectedEventArgs>` |
| `OnResultReceived` | `EventHandler<DecodedResultsReceivedEventArgs>` |

`DecodedResultsReceivedEventArgs` 的属性是 **`Results`**（`DecodedResult[]`，见 `external/FxSDK/Models/IoSessionEventArgs.cs:33`），**不是 `Data`**。
旧版 V1.3.3 使用说明文档示例里写的 `args.Data` 已过时；1.4.0 源码与本项目代码（`FxSdkConnector.OnSdkResultReceived`）均用 `args.Results`。
数组元素可能是 `ChannelResult` / `MultiChannelResult` / `DeviceParameter` 实例，按类型分发。

## 11. C# SDK 1.4 独有功能（Java 1.0 没有）

| 功能 | 文件 | 备注 |
|---|---|---|
| 小波降噪 | `Utility/SignalProcessor.cs:DenoiseSignal` | VisuShrink + **针对超声分层缩放**（α=0.3/0.6/0.8/0.9） |
| 小波变换底层 | `Utility/WaveletTransform.cs` | unsafe + stackalloc 性能优化 |
| Savitzky-Golay 平滑/求导 | `Utility/SavitzkyGolayFilter.cs` | 系数缓存优化 |
| 自适应高斯平滑 | `Utility/SignalProcessor.cs:LightGaussianSmooth` | 梯度大处不平滑 |
| 中值平滑 | `Utility/SignalProcessor.cs:MedianSmooth` | 去尖刺 |
| 通用协议处理器 | `Codec/GeneralProtocolHandler.cs` | 70KB，是 Standard/Expert 的基类 |
| 24 通道实时版完整实现 | `Codec/IotR24ProtocolHandler.cs` | 34KB（**Java 注释说"未实现"是过时的**） |

详见方案 §8 算法章节和 §8.13 算法事实核查档案。

## 12. K_L 量纲约定（最终裁定，冲突时以本节为准）

**裁定（2026-06，工程负责人决策）：全系统以“实现约定”为准 = `K_L = Δt/F`，单位 `ns/N`。**

| 约定 | 定义 | 单位 | 量级 | 用法（反解 F） |
|---|---|---|---|---|
| **实现（源码 + 存量数据，权威）** | `K_L = Δt / F` | **ns/N** | ~1e-4 | `F = Δt / K_L` |
| plan §8.3/§8.5/附录C（国标对照） | `K_L = Δt / (F·t₀)` | 1/N | ~1e-11 | `F = Δt / (K_L·t₀)` |

- 换算关系：`K_L(ns/N) = K_L(1/N) × t₀`（t₀ ≈ 3e4 ns）。
- 代码现状（均按 ns/N 自洽）：`GbSingleWaveCalculator` 算 `F = Δt/K_L`；`torque-calibrate`/`apply-torque-fit` 存 `K_L = Δt/F`；`channels.k_l` / `calibration_records.k_l` 全部按 ns/N 存储。
- **为何选 ns/N 而非 1/N**：存量数据与标定链已按 ns/N 自洽，翻转需迁移所有 `k_l` + 重发标定证书，风险大且无收益。
- **plan 文档的两处问题（已知，按本节为准）**：
  1. §8.3 第 310 行 `P = Δt/(t₀/K_L)`（= `Δt·K_L/t₀`）是 **1/N 约定下的错误反解**，正确应为 `F = Δt/(K_L·t₀)`；
  2. §8.5 第 1213 行 `K_L = a/t₀` 属 1/N 约定。
- **操作红线**：`quick-calibrate` 入参的 `kL` 必须按 ns/N（~1e-4）填写，**切勿照标定证书的 1e-11（1/N）填**。`CalibrationEndpoints` 的 `ValidateKLMagnitude` 量级护栏覆盖**全部标定写入路径**——`quick-calibrate`（入参 kL）、`torque-calibrate` / `apply-torque-fit` / `load-calibrate`（计算得到的 kL）——用“屈服力下 Δt 不应 ≥ t₀”“目标预紧力下 Δt 应 > 1ns 噪声底”两道物理边界拦截量级异常。
