# FxSDK 1.4 → 1.6 升级影响评估

> 2026-08-12：升级代码侧已按 `sdk-1.6-upgrade-plan.md` 执行完毕（该文档末尾有执行记录与遗留现场清单），本文保留作评估留档。

评估对象：厂家 iFast FxSDK 从 `1.4.0`（当前 `external/FxSDK/`）升级到 `1.6.0`（新包 `bhls1.6/sdk16/FxSDK/`）。

评估日期：2026-08-11。评估方式：源码三方比对（厂家原始 1.4.0 / 我方 1.4 / 厂家 1.6.0）+ 厂家随包文档核对。**本次评估未改动任何代码。**

参与比对的三份源码：

| 代号 | 路径 | 说明 |
|---|---|---|
| 原厂 1.4 | `资料/FxSDK C#+Java源码+使用示例/c#/1.4.0 源码/` | 厂家原始交付包，未经我方改动 |
| 我方 1.4 | `external/FxSDK/` | 当前生产在用，含我方 6 轮补丁（见 `sdk-changelog.md`） |
| 1.6 | `bhls1.6/sdk16/FxSDK/` | 厂家 2026-08-10 交付的新包 |

---

## 0. 结论

**不能直接换目录。** 这次升级同时踩到三件事：编译期破坏性变更、静默的语义变更、以及 TOF 提取算法的实质改写。三者里只有第一类会被编译器拦住。

分级结论：

| 维度 | 结论 |
|---|---|
| 1.6 源码自身 | **可编译**。net8.0 / Release 零警告零错误，前提是带上 `Directory.Build.props` 防护文件（见 §7.1） |
| 与我方代码集成 | **必然编译失败**。至少 20 项破坏性变更，其中 4 项直接打断 `src/BoltPreload.Fx/FxSdkConnector.cs` |
| 运行 | 现行 TCP Client 链路可用；但心跳保活补丁回退、发送失败重新被吞、新增一条自动断线路径 |
| 数值 | **取决于 `channels.meas_mode`**。阈值法（=2）实质不变；自动法（=0）算法被整支替换 |
| 标定 | 现场若跑自动法，`channels.ref_time_ns` 与 `k_l` **全部需要重做**；跑阈值法则只需同源验证 |
| 数据模型 | 缺 `RefEnvTime` / `RefPeriodNs` 两个字段，缺 `sdk_version` 追溯字段 |
| 验证能力 | **没有黄金样本**，当前无法做新旧同源比对。这是升级前的第一优先补齐项 |

**升级前必须先查清一件事**：现场 `channels.meas_mode` 的真实取值分布。它决定上面“数值”和“标定”两行落在哪一档，也决定整个升级是“两天的适配工作”还是“一轮完整的重新标定”。查法见 §8。

---

## 1. 变更规模

仅统计 `.cs`，排除 `bin/`、`obj/`、`__MACOSX/`：

| 项目 | 数量 |
|---|---|
| 两版共有文件 | 32 |
| 其中内容有改动 | 21 |
| 1.6 新增文件 | 20 |
| 我方对 1.4 打过补丁的文件 | 9（**1.6 全部动过，无一可无损保留**） |

改动量最大的几支，全部落在信号处理与协议解码链上：

| 文件 | 变更行数 |
|---|---|
| `Utility/SignalProcessor.cs` | +550 / −189 |
| `Utility/CalculateExtensions.cs` | +495 / −154 |
| `Codec/MultiChIotProtocolHandler.cs` | +378 / −157 |
| `Codec/GeneralProtocolHandler.cs` | +372 / −300 |
| `Utility/AgcContext.cs` | +305 / −50 |
| `Core/IoSession.cs` | +291 / −66 |
| `Codec/FxProtocolHandler.cs` | +280 / −21 |
| `Utility/SavitzkyGolayFilter.cs` | +274 / −100 |

1.6 新增的能力（我方当前用不到，但决定了架构走向）：

- `Server/` 整个命名空间：`FxServer` + `IFxDeviceKeyResolver` 四种实现，支持设备主动连入
- `Codec/UltraHighRate28ProtocolHandler.cs`：Model=5，28 通道高频版
- `Utility/TemperatureStabilizer.cs`：温度范围保护 + 一阶平滑
- `Models/EchoAssist.cs`、`EnvelopeTrackingContext.cs`、`MeasStatusCode.cs`：自动法的算法上下文与状态码枚举

---

## 2. 编译期破坏性变更

以下每一项都会让 `dotnet build` 失败，属于“换目录后立刻可见”的一类，风险可控。

### 2.1 直接打断 `FxSdkConnector.cs` 的四项

| 变更 | 1.4 | 1.6 | 受影响位置 |
|---|---|---|---|
| `ChannelResult.StatusCode` 类型 | `short` | `MeasStatusCode` 枚举（底层仍 `short`） | `FxSdkConnector.cs:524,538`（`>= 0` 比较）、`:772`（赋值） |
| `ChannelResult.MidResults` | `decimal[]` | **删除**，语义移到 `Assist`（`EchoAssist?`） | `FxSdkConnector.cs:782` |
| `FxClient.RunAgcControlAsync` | `(..., decimal gainStep, int maxIterations = 20)` | 中间插入必填 `CancellationToken token`；返回 `AgcContext?` → `AgcResult?` | AGC 调用处 |
| `IoSession` 三个 public 事件 | `SessionConnected` / `SessionDisconnected` / `SessionDecResultsReceived` | 全部删除，改 `internal SetLifecycleHandler` / `SetDecodedResultHandler` 回调 | `src/BoltPreload.Tests/Fx/MultiChIotDecodeResyncTests.cs:21-32` 的 `FakeSession` |

`MeasStatusCode` 的数值与 1.4 的注释语义**逐一对应**（0 Normal、−1 SignalTooSmall、−2 ThresholdNotReached、−3 SignalOverflow、−4 SimilarityLow、−5 UnknownSignalError、−6 SnrLow），底层类型显式声明为 `short`。**所以数据库存量数据不需要迁移**，边界上加一次 `(short)` 转型即可。1.6 另新增 11 `BelowLowerLimit` / 12 `AboveUpperLimit`，语义是“测量值有效但业务超限”——注意 `IsSignalValid()` 对它们返回 `true`，我方 `PostgresMeasurementSink.cs:190` 的 `StatusCode < 0` 判断恰好也能正确处理，不需改。

### 2.2 其他破坏性变更

- `CalculateExtensions` 五个 `CalcZeroIndexBy*` 的 `out decimal[] midResults` 全部改成 `out EchoAssist assist`；`CalcZeroIndexByEnv` 还在中间插了 `int consecutive = 20`，位置参数会错位
- `CalcZeroIndexAuto`、`GetSmoothingData`、`SignalProcessor.DenoiseSignal`、`LightGaussianSmooth`、`SavitzkyGolayFilter.Differentiate` 整支删除
- `SavitzkyGolayFilter.Smooth` 参数名 `y`/`order` 改为 `data`/`polynomialOrder`（具名传参会断）
- `AgcContext` 构造函数第 4 位插入必填 `CancellationToken`；`Cts` 属性删除；`WaitNextGainAsync` 返回 `Task` → `Task<decimal>`
- `FxProtocolHandler.ClearContext()` 从 `protected` 收紧为 `internal`

**好消息**：`WorkMode` 九个成员的顺序与隐含数值**完全没变**（`Standby=0` … `TWaveRead=8`）。`IFxConnector.cs:200-211` 定义的 `FxWorkMode` 与 `FxSdkConnector.cs:195` 的 `(WorkMode)(int)mode` 序数强转**继续成立**。这是本次升级唯一一处“刻意依赖枚举顺序”的设计，它躲过了一劫。

`CalculateExtensions.CalcUltraResult` 签名与实现逐字未变。

---

## 3. 静默语义变更（本次升级最危险的一类）

这些**不会有任何编译错误或警告**，换目录后直接带病上线。

### 3.1 `SpTemp` 从原始温度变成平滑后温度 （高风险）

| | 1.4 | 1.6 |
|---|---|---|
| `SpTemp` 语义 | 设备原始温度，过 window=10 在线中值滤波 | **范围保护（−40~120°C）+ 一阶指数平滑** |
| 原始值 | 无处可取 | 新字段 `RawTemp` |
| 默认时间常数 | 不适用 | **τ = 180 秒**（`TempTauProfile.Balanced`） |

证据：`bhls1.6/sdk16/FxSDK/Models/MeasResult.cs:60,64`、`Utility/TemperatureStabilizer.cs:20-37`、`Utility/FxUtility.cs:52-62`。

我方 `FxSdkConnector.cs:775` 直接把 `cr.SpTemp` 存成 `SpTempC`，`:597` 又拿它当重标定的 `RefTemp` 基准。字段名和类型都没变，编译器不会吭声，但：

1. **历史数据与新数据不可比**——同一列 `sp_temp_c` 前后语义不同，且无字段可区分
2. **温补参考点系统性偏移**——置零时刻若温度正在变化，1.4 记的是中值、1.6 记的是 EMA 值，两者不同，且这个差异**不会被重新置零抵消**（`RefTemp` 只出现在温补公式分母里）
3. **变温期间产生动态偏差**——一阶滤波追踪斜坡的稳态滞后正好等于 τ。以 20 μs 声时、`tc = 0.00010269` 估算，温补灵敏度约 **−2.05 ns/°C**；5 °C 阶跃 → 初始约 10 ns 偏差，按 τ=180s 衰减

另外 1.6 **移除了温度中值滤波**，单点温度突跳不再被挡掉。采样间隔 30 s 时 α ≈ 0.154，一次 10 °C 的 PT1000 接触不良突波会立刻造成约 1.5 °C 跳变、约 3 ns 的 DeltaTof 错误，且要几分钟才衰减。

**处置方向（二选一）**：改取 `RawTemp` 保持语义连续；或显式设 `FxUtility.TempTauProfile = TempTauProfile.Direct` 关掉平滑。无论哪种都要在 `measurements` 上加字段区分新旧语义。

### 3.2 `CommunicationType = 2` 直接抛异常 （高风险）

`bhls1.6/sdk16/FxSDK/FxCommunication.cs:183-186`：

```csharp
case 2: //TCP服务器
    throw new NotSupportedException(
        "TCP Server mode has moved to FxServer in FxSDK 2.0. " +
        "Use FxSDK.Server.FxServer.StartAsync(FxServerListenerOptions) for devices that actively connect to the SDK.");
```

1.4 有完整的 TCP Server 实现（`FxCommunication.cs:169-172,246-268`），1.6 整段移除。

**当前不致命**：`appsettings.json:40` 与 `devices.comm_type` 现场都是 `1`（TCP Client）。但 `devices.comm_type` 是**可配置字段**，`0001_initial_schema.sql:55` 的注释明写“2 TCPServer”，运维只要在设备管理页把它改成 2，1.6 下就是连接时抛异常，而 1.4 下是能跑的。升级时应同步在 `DeviceEndpoints.cs` 的校验里把 2 拒掉，或补上 `FxServer` 支持。

（顺带：厂家这条异常文案写的是“FxSDK 2.0”，而包版本号是 1.6.0，文案与版本不一致，建议向厂家求证。）

### 3.3 心跳保活补丁回退 （中风险）

| 版本 | `HeartbeatSeconds` | 位置 |
|---|---|---|
| 原厂 1.4 | 20 | — |
| 我方 1.4 | **2** | `external/FxSDK/Core/IoSession.cs:41` |
| 1.6 | 20（可配） | `bhls1.6/.../Core/IoSession.cs:69`、`Models/Config/TcpSessionOptions.cs:21` |

`sdk-changelog.md:7` 记录了改成 2s 的原因：**现场串口服务器约 5 秒无流量就掐 TCP，20s 心跳保不住连接**。这是有真机故障背书的补丁。

1.6 把它做成了 `DeviceConfig.TcpSessionOptions.HeartbeatSeconds`，**可以设回 2**——但必须有人记得设，否则 DEV_LAB_001 那条链路的断线问题会原样复发。

### 3.4 发送失败重新被吞 （中风险）

`sdk-changelog.md:16` 记录我方把 `IoSession.SendMsgAsync` / `TcpSession.SendDataAsync` 改成失败上抛，让心跳 missed 计数真正生效。1.6 回到只记 `Logger.Warn` 不抛。调用方会以为命令已下发，实际没送出去，然后白等设备回包到超时。

### 3.5 其他静默变更

| 变更 | 影响 |
|---|---|
| 解码异常从“复位续跑”改为 `RequestClose(ProtocolError)` **关闭整个会话** | 坏数据的后果从丢一段缓冲升级成断线 |
| 新增“接收活动超时”主动断线（`TcpSession.ResolveReceiveActivityTimeout`） | 1.4 完全没有的新断线来源；可用 `TcpSessionOptions.EnableReceiveActivityTimeout = false` 关掉 |
| `SerialSession` 心跳循环整段删除 | 串口链路失去断线检测能力（我方走 TCP，暂不受影响） |
| `FxClient.StopAllAsync` 不再停监听器 | `FxSdkConnector.cs:806` 的注释“停所有监听器 + 关所有会话”不再成立 |
| `ConfigureChannelAsync` 在 `Gain` 等字段变动时**自动清空包络追踪锚点** | AGC 每轮调增益都会触发，等于把自动法的锚点清掉 |
| AGC 目标峰值 `SignalAuto` **600 → 400**、`SignalMax` 700 → 600 | 稳态增益必然不同，详见 §4.4 |
| `DeltaTof` 前置条件放宽：1.4 要求 `RefTime` 与 `RefTemp` 都非空，1.6 只要 `RefTime` | `RefTemp` 为空时 1.6 **静默假设 22.22°C 并输出看似正常的 DeltaTof**。现场温度 60°C 时约 78 ns 假偏移 |
| `OnResultReceived` 改由独立派发线程触发（unbounded `Channel`） | 顺序仍保证；但事件不再跑在解码线程上，积压超 1024 批会告警 |
| `ChannelConfig.EchoWidth` 默认值 40 → 15 | 我方 `FxConnectorFactory.cs:35` 显式写死 40，实际行为不受影响，但该处注释“刻意对齐 SDK 默认值”不再成立 |

---

## 4. TOF 数值行为变化与标定影响

这是决定“要不要重新标定”的核心部分。

### 4.1 三种测量模式的实现变化

`ChannelConfig.MeasMode` 的编号与文档语义两版**完全相同**（0 自动 / 1 动态特征 / 2 阈值），但 0 和 1 的实现都被换掉了：

| MeasMode | 1.4 实现 | 1.6 实现 | 同一段原始字节的 TOF 差异 |
|---|---|---|---|
| 0 自动法 | `CalcZeroIndexAuto`：首个超 5σ 回波 → 包络最大梯度点 → 其后第一个过零 | `CalcZeroIndexByEnvelopeGuidedZero`：加权选主回波 → 30% 包络前沿 Fx → F0/Z0 相位追踪 | **整载波周期级（2 MHz 下约 500 ns）到 μs 级** |
| 1 动态特征法 | 全局包络 45% 正向 → 反向找 30%；因果 5 点移动平均平滑 | 主回波区域内 30% 正向；零相位 SG 平滑 | **系统性提前约 50 ns**，主回波选错时可达数百 ns |
| 2 阈值法 | `CalcZeroIndexByThrSignal` | 同名同流程，仅修了过零插值的 off-by-one | **次奈秒级，实质不变** |

50 ns 的来源：1.4 用 `MovingStatistics(5)` 这种 trailing 平均，群延迟 = (5−1)/2 = 2 个采样点；算法工作在 25 ns 网格上，2 × 25 = 50 ns。1.6 换成零相位 SG，这个延迟消失。

阈值法“实质不变”的依据：协议层字节换算（温度 `Int16 × 0.01`、`EchoStart = UInt16 × 1000`、原始点间隔 125 ns、算法插值 25 ns、`EchoWin` 按 1000 ns 量化）**逐字未变**；`BandPassFilter` 逐字未变；2 μs 窗内插到 1 ns 再找正向过零的流程未变。唯一的 off-by-one 修正作用在 1 ns 网格上，对 500 ns 周期而言是 ps 量级。

### 4.2 自动法从无状态变成有状态 （高风险）

这是升级中**唯一一个“安全关键且完全静默”**的点。

- 1.4 的 `MeasMode=0` 是**无状态**的：`external/FxSDK/Codec/MultiChIotProtocolHandler.cs:841-843` 只看当前波形，不读 `RefTime`、不读参考波形
- 1.6 的 `MeasMode=0` 是**有状态**的：`bhls1.6/.../MultiChIotProtocolHandler.cs:976-990` 构造 `EnvelopeTrackingContext`，读 `channelConfig.RefEnvTime` 与 `RefPeriodNs`；`:1015` 明确用 `RefTime.HasValue && RefEnvTime.HasValue` 判断有无持久锚点

**我方数据库里没有 `RefEnvTime` 和 `RefPeriodNs` 这两个字段**（全库 grep 只在 `bhls1.6/` 命中）。后果是：每次进程重启，自动法都会走“无锚点”分支重新捕获，过零点可能落在相邻载波周期上。

按 `GbSingleWaveCalculator.cs:55` 的 `F = Δt / K_L`（`K_L` 单位 ns/N），以测试夹具里的 `K_L = 0.001` 换算，**1 ns 的 TOF 偏移 ≈ 1 kN 假预紧力**，一个 500 ns 的周期跳变 ≈ 500 kN。而系统会照常计算、照常入库、照常不告警（比值可能仍落在 0.70~1.10 区间内）。现场真实 `K_L` 需从 `channels.k_l` 读取后重算这个换算系数。

厂家自己的参考实现已经示范了正确做法，见 `bhls1.6/FxReader/DeviceRunner.cs:322-327` 与 `bhls1.6/FxReader/ZeroStore.cs:55-95`。

### 4.3 预处理链路的参数变动

| 项目 | 1.4 | 1.6 | 是否影响过零位置 |
|---|---|---|---|
| `SavitzkyGolayFilter.Smooth` 默认值 | `windowSize=9, order=3` | `windowSize=5, polynomialOrder=2` | 有，几 ns 级、与波形形状相关 |
| 三次样条重采样 | natural spline，无延拓 | natural spline + **镜像延拓** | 仅记录边界约 1.25 μs 内 |
| `SignalProcessor.CalcSnrAuto` 阈值因子 | 3.0 | 5.0 | 该函数两版均无调用者，不进实际路径 |
| Hilbert 包络、`BandPassFilter`、`WaveletTransform` | — | **逐字未变** | 无 |
| `CalcDeltaTof` 公式、`DefaultTempCoeff = 0.00010269`、`StandardTemperature = 22.22` | — | **逐字未变** | 无 |

### 4.4 AGC 变化与“跳周期”风险

1.4 是“比例反推 + 从最小增益起爬坡”，1.6 改成“从当前增益夹逼 + 二分搜索 + 饱和平台检测”，且目标峰值 `SignalAuto` 从 600 降到 400。**稳态增益必然不同。**

会不会因此改变过零点，分两种情况：

- **`Threshold == 0`（`0012_multichannel_model.sql:135` 自动建档的默认值）→ 不会**。阈值法此时用 `signal.Max() * 0.6` 的相对阈值，随幅值等比缩放，线性系统中过零点与幅值无关
- **`Threshold != 0`（如 dev 种子的 −490）→ 会，且是整周期跳变风险**。幅值降 33% 后，固定阈值在回波上升包络上的交越点后移，一旦跨过一个载波周期边界，后续选中的过零点整体晚一个周期

### 4.5 既有标定系数还能不能用

先把数学讲清楚。系统自己算 Δt（**不用 SDK 的 `DeltaTof`**，见 §5）：

```
Δt = EchoTime / (1 + tc × (T − T_ref)) − RefTime
F  = Δt / K_L
```

**会抵消的情况**：设 1.6 相对 1.4 有固定偏移 δ。若在同一松弛状态下用 1.6 **重新置零**，则 `RefTime` 也偏移 δ，相减后完全抵消，`K_L` / `c_0` 可以沿用。（严格说温补分母会留下约 `δ × tc × ΔT` 的残差，δ=500 ns、ΔT=30°C 时约 0.15 ns，可忽略。）

**不会抵消、必须重新全量标定的四种情况**：

1. **偏移与波形形状相关**（SG 参数变更、主回波选择规则变更）。置零时刻的偏移是 δ₀，但螺栓拧紧、温度变化、耦合老化后波形形状会变，偏移变成 δ(t) ≠ δ₀，差值直接进 Δt 并被 `K_L` 放大。这类偏移在整个量程上不是常数，**`K_L` 的斜率本身可能失真**
2. **发生整周期跳变**（自动法选错周期、阈值法幅值下降跨周期）。这是分段不连续，任何线性标定都无法补偿
3. **温度路径改变**（§3.1）。`RefTemp` 的语义变了，且该误差随温度变化而变，是纯动态误差，标定系数无从补偿
4. **`RefTemp` 为空的通道**（§3.5）。1.4 不出 DeltaTof，1.6 用 22.22°C 顶上

**分模式处置建议**：

| 通道配置 | 处置 |
|---|---|
| `MeasMode=2` 且 `Threshold=0` | TOF 实质不变。做同源比对验证即可，理论上连重新置零都不必 |
| `MeasMode=2` 且 `Threshold≠0` | 确认升级后不重跑 AGC；若重跑，需核对峰值仍远高于该阈值 |
| `MeasMode=0` 或 `1` | **必须重新置零 + 重新全量标定**。只重新置零不够（偏移不是常数） |

---

## 5. 现行系统的耦合面

**类型层面零泄漏**，这是本次升级最有利的一点。只有 `src/BoltPreload.Fx` 引用 `external/FxSDK/FxSDK.csproj`（`BoltPreload.Fx.csproj:20`）；Application / Api / Infrastructure / Algorithms / Domain 全部只通过 `FxDecodedEnvelope`、`FxChannelResultDto`、`FxEchoWaveDto`、`FxDeviceParameterDto` 这组 record 通信。编译期爆炸半径约 1200 行，集中在三个文件。

但有四类**非类型**耦合会跟着外溢：

1. **枚举序数强转**：`FxSdkConnector.cs:195` 的 `(WorkMode)(int)mode`。本次 `WorkMode` 没变，逃过一劫，但这个设计在下次升级仍是雷
2. **SDK 属性名字符串已经变成数据库内容**：`device_parameter_history.param_name` 存的就是 `ProbeFreq` / `Gain` / `RefTime` 这些 `nameof(ChannelConfig.X)` 的复制品，而启动时又反过来从这张表覆盖通道配置（`FxInitialChannelConfigLoader.cs:112-159`）。SDK 改属性名 = 历史数据失效。本次没改，但要记在案
3. **状态码语义硬编码**：`PostgresMeasurementSink.cs:902-911` 把 −1..−6 逐一映射成中文原因。1.6 未新增负值状态码，本次安全
4. **`AlarmInput.SdkStatusCode` 是死字段**：传进去了但 `StaticThresholdAlarmEvaluator` 从未读取

**关键事实：系统自己算 Δt，不用 SDK 的 `DeltaTof`。** `PostgresMeasurementSink.cs:338,374` 都取 `cr.EchoTimeNs` 原始值送进预紧力链。SDK 的 `DeltaTof` 只写进 `measurements.delta_tof_ns` 留痕，且**该列语义是混的**——已标定通道写系统自算值，未标定通道写 SDK 值（`:488-489`）。这意味着 §3.1 的温度语义变更会污染未标定通道的这一列，而没有任何字段能区分。

温补也因此**没有重复计算**，但只是侥幸：SDK 补出的是 `DeltaTof`，系统补的是 `EchoTime`，预紧力链只吃后者。哪天有人把 `cr.DeltaTofNs` 接进 `PreloadInput`，立刻变成补两次。

---

## 6. 我方本地补丁的去向

三方比对结果：我方改过 9 个 `.cs`，1.6 **全部动过**，无一可以无损保留。逐条核对如下：

| 我方补丁（`sdk-changelog.md`） | 1.6 状态 | 处置 |
|---|---|---|
| CRC 坏帧重同步（`MultiChIotProtocolHandler`） | **已进上游**：`ResynchronizeAfterCorruptFrame()` `:907`、`ShouldLogCrcMismatch` `:922` | 补丁可弃，保留 `MultiChIotDecodeResyncTests` 钉住行为 |
| 心跳 20s → 2s | **回退**到 20，但改为可配 | 必须在 `DeviceConfig.TcpSessionOptions.HeartbeatSeconds` 设回 2 |
| 解码超时/异常不杀循环 | **策略不同**：1.6 改为关闭会话 + 依赖重连 | 需重新评估 1.6 的机制能否接受 |
| 发送失败不吞异常 | **回退**，重新静默 | 需重新打补丁，或在适配层加超时检测 |
| `ByteUtility` 删除 CRC 循环里的废弃 `BitArray` | **回退**，1.6 又有了 | 纯性能，热路径每字节一次多余分配 |
| `ChannelConfig.EchoWidth` 注释对齐 | 1.6 把代码改成 15 | 无需处理 |

`external/FxSDK/Directory.Build.props` 是**我方加的防护文件，1.6 包里没有**。它做三件事，缺一不可：

1. 截断根 `Directory.Build.props` 的继承，避免厂家代码受 `TreatWarningsAsErrors=true` + `AnalysisMode=Recommended` 约束，也避免把 BoltPreload 的 Company/Product/Version 元数据盖到厂商 DLL 上
2. 排除 macOS AppleDouble `._*.cs`
3. 提升 `System.Net.Http` / `System.Text.RegularExpressions` 到修复版，消除 `MathNet.Symbolics → FParsec` 带进来的高危 CVE

**升级时必须把这个文件一起带过去**，并复核第 3 条：1.6 的 csproj 已经显式引用 `FParsec 1.1.1`，CVE 提升是否仍必要需重新验证。

---

## 7. 依赖与构建环境

`FxSDK.csproj` 的变化：

| 项 | 我方 1.4 | 1.6 | 说明 |
|---|---|---|---|
| 目标框架 | `net8.0` | `net6.0;net8.0` | 多目标；根 `Directory.Build.props` 的单数 `TargetFramework=net8.0` 先生效，实测只构建 net8.0（见 §7.1） |
| NLog | 6.1.3 | **5.3.4** | 降级。`src/BoltPreload.Fx.csproj:12` 自己也引 6.1.3，NuGet 取高版本，但会把 `sdk-changelog.md:34` 刚消除的版本分叉带回来 |
| System.IO.Ports | 10.0.9 | 8.0.0 | 降级 |
| K4os.Compression.LZ4.Streams | 1.3.8 | **移除** | — |
| FParsec | 传递引入 | **显式 1.1.1** | 与 CVE 提升策略相关，需复核 |
| System.IO.Pipelines | 无 | 新增（条件式 6.0.3 / 8.0.0） | — |

**包内有 macOS 残留**：`__MACOSX/` 目录（含 37 个 `._*.cs`）、`FxSDK/.DS_Store`、`FxSDK.csproj.user`、`Properties/PublishProfiles/*.pubxml.user`。`__MACOSX/` 是 `FxSDK/` 的**同级目录**，不在 csproj 的 glob 锥体内，因此不会被当源码编译；但整理入库时应一并清掉。

**目录名含中文会破坏构建**：`bhls1.6/sdk16/` 在非 UTF-8 代码页的 PowerShell 下会让 MSBuild 报 `error MSB3202: 未找到项目文件 ...sdk1.6鍖匼FxSDK\FxSDK.csproj`。入库时目录名应改为纯 ASCII。

### 7.1 构建验证结果（已实测）

评估环境无法访问 `api.nuget.org`。绕过方式：用一份 `<packageSources><clear /></packageSources>` + `<auditSources><clear /></auditSources>` 的临时 `nuget.config` 走 `--configfile` 还原——1.6 的全部依赖（NLog 5.3.4、FParsec 1.1.1、System.IO.Ports 8.0.0 等）已在本机 NuGet 全局缓存中，离线还原可成功。

（附带结论：根 `Directory.Build.props:9` 的 `WarningsNotAsErrors` 只豁免了 `NU1903` 没豁免 `NU1900`，所以一旦构建机拿不到 NuGet 漏洞数据，构建就会直接失败。这是与本次升级无关、但现网 CI/工控机离线时会踩到的独立问题，建议把 `NU1900` 一并加进豁免。）

实测两组结论：

**一、1.6 源码本身编译干净。** 施加 `external/FxSDK/Directory.Build.props` 的等效设置（`TreatWarningsAsErrors=false` + `EnableNETAnalyzers=false` + `EnforceCodeStyleInBuild=false`）后：

```
FxSDK -> bhls1.6\sdk16\FxSDK\bin\Release\net8.0\FxSDK.dll
0 Warning(s)
```

零警告、零 CS 错误。**1.6 在 net8.0 下可用。**

**二、缺了防护文件就编不过，共 83 个错误，全部是 CA 分析器规则升级而来，没有一个是真正的 C# 编译错误。** 这直接印证了 §6 里“防护文件必须一起带过去”的判断。错误分布：

| 规则 | 数量级 | 内容 |
|---|---|---|
| CA1309 | 约 40 处 | 未用 ordinal 字符串比较（协议属性名 switch 里大量出现） |
| CA2208 | 6 处 | `ArgumentException` 的 paramName 传的是中文描述或属性名 |
| CA1861 | 4 处 | 常量数组实参应改 `static readonly` |
| CA1510/1512/1513 | 4 处 | 应改用 `ArgumentNullException.ThrowIfNull` 等新 API |
| CA1816 | 2 处 | `Dispose()` 未调 `GC.SuppressFinalize` |
| CA2201 | 2 处 | 抛了过宽的 `System.Exception`（`WaveletTransform`） |

**三、多目标框架不会带来麻烦。** 1.6 的 csproj 声明 `TargetFrameworks=net6.0;net8.0`，但根 `Directory.Build.props:4` 的单数 `TargetFramework=net8.0` 会先生效，实测构建产物只有 `bin/Release/net8.0/`，net6.0 分支不参与构建。§7 表格里“行为需实测”一项**已确认无风险**，也不会触发 net6.0 的 EOL 告警。

---

## 8. 升级前必须查清的现场事实

这些无法从源码得出，必须查库或实测。按优先级排列：

1. **`channels.meas_mode` 的真实分布**（决定整个评估落在哪一档）

   ```sql
   SELECT meas_mode, threshold, count(*), count(*) FILTER (WHERE ref_temp_c IS NULL) AS ref_temp_null
   FROM channels WHERE sampling_enabled GROUP BY meas_mode, threshold ORDER BY 1, 2;
   ```

   已知的配置来源互相矛盾，必须以现场库为准：

   | 来源 | `meas_mode` | 说明 |
   |---|---|---|
   | `0001_initial_schema.sql:89` | 2 | 表默认值 |
   | `0012_multichannel_model.sql:135` | 2 | 自动建档的物理通道 |
   | `deploy/sql/dev/0003_dev_seed_real_probes.sql:25` | **0** | dev 实探头种子，生产不跑 `dev/` |
   | `src/BoltPreload.Api/appsettings.json:52` | **0** | 单设备兜底配置 |

   注意一个陷阱：`FxInitialChannelConfigLoader.cs:187` 的 `if (target.MeasMode == 2 && fallback.MeasMode != 2) target.MeasMode = fallback.MeasMode;` ——**库里是默认值 2 的通道，会被 appsettings 的 0 静默覆盖**。这条路径只在 `devices` 表查不到启用设备时才走（`DeviceConnectorManager.cs:277-278,350`），多设备 DB 驱动路径传的是 `includeAppSettingsFallback: false`。但 DB 查询失败时会退到这条路径，需要确认现场不会命中

2. **是否有 `ref_temp_c IS NULL` 但 `ref_time_ns` 有值的通道**（决定 §3.5 的静默假读数是否会发生）
3. **`channels.k_l` 的真实量级**（把 §4.2 的“1 ns ≈ 1 kN”换算成现场实际数字）
4. **多通道监测的实际上报间隔**（决定 1.6 的 τ=180s 平滑比 1.4 的中值-10 滞后更多还是更少，临界值约 40 秒）
5. **探头实际中心频率**（本评估按 `ProbeFreq = 2.0 MHz`、载波周期 500 ns 估算；5 MHz 探头需按比例缩到 200 ns）

---

## 9. 建议的升级路径

**第 0 步必须先做，且必须用 1.4 在现场做**——否则升级后没有基准可比：

### 阶段 A：先补验证能力（升级前，用 1.4）

1. **录一批真机原始回波帧**。`bhls1.6/FxReader/RawWriter.cs` 已有现成的录制能力；或用 `Fx:DeveloperMode=true`（`FxConnectorFactory.cs:54-58`）落 `logs/sdk-*.log`
2. **搭离线重放 harness**。`src/BoltPreload.Tests/Fx/MultiChIotDecodeResyncTests.cs:21-32` 的 `FakeSession` + `Enqueue`/`TryDecode` 已经是现成写法，推广成“喂字节流、收 `DecodedResult`”即可
3. 覆盖面要包含现场实际用到的每一种 `(meas_mode, threshold, probe_freq)` 组合

当时的 `tools/FxDeviceSimulator` 是空壳（`Program.cs:1-15` 只打印三行字），进程内 `SimulatedFxConnector` 完全绕过 SDK 直接产 DTO，两者都**不能**用于同源比对——后来它们已随“移除模拟功能”一并删除。

### 阶段 B：适配与静态验证

4. 把 1.6 源码整理入库（ASCII 目录名、清 `__MACOSX`/`.DS_Store`/`*.user`、带上 `Directory.Build.props`）
5. 修 §2 的编译错误
6. 逐条处置 §3 的静默变更，重点是 `SpTemp` 取值策略与 `TcpSessionOptions.HeartbeatSeconds = 2`
7. 补数据模型缺口：`channels` 加 `ref_env_time_ns` / `ref_period_ns`，`calibration_records` 与 `channels` 加 `sdk_version`（或 `tof_extractor_signature`），并在 `TryComputePreload` 开头比对，不符走既有的降级路径（`PostgresMeasurementSink.cs:319-327` 已有现成模式）
8. `LEchoWave` 的持久化与开机回灌当前完全缺失（`FxConnectorFactory.cs:86-100` 只映射 12 个字段），自动法通道必须补上

### 阶段 C：数值验收

9. **同源比对**：同一批录制帧分别喂 1.4 与 1.6，逐帧比 `EchoTime`。这是判定“阈值法实质不变”这个结论是否成立的唯一手段
10. 按厂家文档 §4.5 的四项做真机验证：静态重复性、加载/卸载、温度影响、断电恢复
11. 自动法通道额外验证：进程重启后 TOF 是否连续（这是 §4.2 那个静默风险的直接检验）

### 阶段 D：标定

12. 按 §4.5 的分模式结论决定重新置零 / 重新全量标定的范围
13. 新标定记录必须带上 `sdk_version`，避免新旧系数混用

---

## 10. 需要向厂家求证的问题

1. 文档说自动法首次扫波“只接受 Fx 之后 **0.8** 个周期内的第一个合格过零点”，但代码是 **1.05T**（`CalculateExtensions.cs:21` `InitialSearchMaxPeriodRatio = 1.05d`，注释称从 0.8T 放宽而来）。**文档与实现不符**，且该参数直接决定首次扫波会不会选错周期——以哪个为准？
2. `TryLocateEnvelopeFront` 对自动法用 `finThreshold: 0.09`、对动态特征法用 `0.08`（`CalculateExtensions.cs:211,524`），差 0.01 无注释无文档。是刻意调参还是笔误？
3. `MeasStatusCode.SimilarityLow`（−4）在 1.6 自动法里实际表示的是“周期估计失败 / 找不到合格过零点”，与名称完全不符。我方告警规则若按名称理解会误判根因
4. `CommunicationType = 2` 的异常文案写“FxSDK 2.0”，包版本号是 1.6.0，文案与版本不一致
5. 自动法标注为 **Beta**。用于商用安全关键场景（风电塔筒、桥梁、压力容器法兰）时，厂家的稳定性承诺与已知限制是什么？
6. `ByteUtility.cs:251` CRC 循环里 `new BitArray(t).Reverse();` 构造后即丢弃，结果未使用。我方 1.4 曾删掉它，1.6 又有了——确认是无作用语句还是遗漏的逻辑？

---

## 附：本次评估未能覆盖的部分

- 未逐行核对 `GeneralProtocolHandler`（Model=0/1）与 `IotR24ProtocolHandler`（Model=2）的改动，我方只用 Model=3
- 未评估 `UltraHighRate28ProtocolHandler`（Model=5）
- 未设计 `FxServer` 的接入方案，只在耦合面盘点里标出了受影响位置（`FxSdkConnector` 的单会话模型、`DeviceConnectorManager`、`IngestionService` 的对账逻辑、`IFxConnector` 接口契约、以及 `devices` 表的设备识别键映射）
