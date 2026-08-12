# FxSDK 1.6 替换 1.4 实施方案

配对文档：`sdk-1.6-upgrade-assessment.md`（影响评估，2026-08-11）。评估讲"换了会发生什么"，本文讲"怎么换、按什么顺序、每一步动哪些文件"。

方案日期：2026-08-12。写本方案时把评估里的断点在当前代码上重新核对了一遍，有几处收窄（见附录），执行时以本文为准。

> **执行状态（2026-08-12）：代码侧已全部完成**，构建 0 警告 0 错误、测试 397/397 通过，明细见文末"执行记录"。
> 现场侧四件事仍然欠着，投产前必须补：§1.1 的 meas_mode 分布查询、§1.2 金样录制（要在**部署 1.6 之前**用在跑的 1.4 录）、
> §6.1 同源比对、§6.3 实验室观察期。0031 迁移要随部署执行。

---

## 0. 总策略

**换源一次 commit 完成，回滚 = revert。** `external/FxSDK/` 原地替换，csproj 引用路径、`Directory.Build.props` 防护文件全部不动位置。DB 迁移只加列不删列，回滚代码不用回滚库。

**两档路径，由现场 `channels.meas_mode` 分布决定**（评估 §8 第 1 条，动手前必查）：

| 路径 | 现场条件 | 内容 | 代码量级 |
|---|---|---|---|
| A（阈值法档） | 所有启用通道 `meas_mode=2` | 步骤 1~3 + 5~6，跳过步骤 4 | 2~3 人日 + 现场同源验证半天 |
| B（自动法档） | 存在 `meas_mode=0/1` 通道 | 全部步骤 + 重新置零/全量标定 | 再加 3~5 人日 + 现场标定窗口 |

路径 B 的通道升级后**必须重新置零 + 重新全量标定**，只重新置零不够（评估 §4.5：偏移与波形形状相关，`K_L` 斜率本身可能失真）。

---

## 1. 动手前的门槛（缺一不开工）

### 1.1 查现场库（评估 §8，5 分钟）

```sql
-- 定 A/B 路径 + 顺带查静默假读数风险
SELECT meas_mode, threshold, count(*),
       count(*) FILTER (WHERE ref_temp_c IS NULL AND ref_time_ns IS NOT NULL) AS silent_2222_risk
FROM channels WHERE sampling_enabled
GROUP BY meas_mode, threshold ORDER BY 1, 2;

-- k_l 量级：把"1 ns 偏移 ≈ 多少 kN"换算成现场实际数字，写进验收判据
SELECT min(k_l), max(k_l), avg(k_l) FROM channels WHERE k_l IS NOT NULL;
```

`silent_2222_risk > 0` 的通道要在换源前补 `ref_temp_c`——1.6 下 RefTemp 为空会静默按 22.22°C 算 DeltaTof（评估 §3.5），虽然预紧力链不吃 SDK 的 DeltaTof，但 `measurements.delta_tof_ns` 会留下看似正常的假值。

### 1.2 金样录制（用 1.4 录，换完就没机会了）

- 开 `Fx:DeveloperMode=true` 落 `logs/sdk-*.log` 原始帧，或参考 `bhls1.6/FxReader/RawWriter.cs` 的录制写法
- 覆盖现场实际用到的每一种 `(meas_mode, threshold, probe_freq)` 组合，每组合至少几百帧、含温度变化时段
- 这批帧是步骤 5 同源比对的唯一基准。`tools/FxDeviceSimulator` 是空壳、`SimulatedFxConnector` 绕过 SDK，都不能替代

---

## 2. 步骤 1：源码入库（不接线，纯整理）

**源**：`bhls1.6/sdk1.6包/FxSDK/`（厂家原包）。注意仓库里还有一份 `bhls1.6/sdk16/`——评估 §7.1 编译验证时做的 ASCII 副本，入库前先 diff 两者 `.cs` 一致，一致就用哪份都行。

**目标**：`external/FxSDK/` 原地覆盖。操作清单：

1. 删旧 `.cs`/`.csproj`，拷入 1.6 源码。**保留我方两个文件**：`Directory.Build.props`（防护文件，1.6 包里没有，缺了直接 83 个 CA 分析器错误）、`README.md`（改内容不删文件）
2. 不入库的垃圾：`__MACOSX/`（在 FxSDK 同级）、`.DS_Store`、`FxSDK.csproj.user`、`Properties/PublishProfiles/`（FolderProfile.pubxml 及 .user）、`bin/`、`obj/`
3. csproj 按我方惯例修（都记 `sdk-changelog.md`，参照 2026-07-08 条目）：
   - NLog `5.3.4` → `6.1.3`（`BoltPreload.Fx.csproj:12` 已引 6.1.3，不改就把刚消掉的版本分叉带回来）
   - System.IO.Ports `8.0.0` → `10.0.9`
   - `TargetFrameworks=net6.0;net8.0` 不动：根 `Directory.Build.props` 的单数 `TargetFramework=net8.0` 先生效，实测只构建 net8.0（评估 §7.1 已验证）
4. 入库后跑 `dotnet list external/FxSDK/FxSDK.csproj package --vulnerable --include-transitive`，复核防护文件里 System.Net.Http / System.Text.RegularExpressions 的 CVE 提升是否还需要（1.6 已显式钉 FParsec 1.1.1，可能已经不用提升，能删就删并记录）
5. `bhls1.6/` 整目录定位成厂家交付资料归档：FxReader 参考实现留着对照（置零/锚点持久化的厂家示范），不编入解决方案；`~$SDK1.6...docx` Word 锁文件删掉；`sdk16/` 副本在入库完成后删除，避免三份源码并存

---

## 3. 步骤 2：编译适配

今天在当前代码上重新核对，实际断点比评估 §2.1 少：**AGC 那条不存在**（`RunAgcControlAsync` 全 src 无调用点），`StatusCode >= 0` 比较也**不是断点**（字面量 0 可隐转枚举，能编译，且 1.6 新增的 11/12 是正值，缓存判断语义恰好正确）。真正要改的就三处：

### 3.1 `FxSdkConnector.cs:772` —— StatusCode 枚举转 short

```csharp
// DTO 的 StatusCode 保持 short（库里存量数据、WAL、前端全按数值走），边界上转一次
StatusCode: cr.StatusCode.ToCode(),   // 1.6 自带扩展方法，等价 (short)cr.StatusCode
```

顺手项（非断点）：`:524`/`:538` 的 `cr.StatusCode >= 0` 建议改成 `cr.StatusCode.IsSignalValid()`，跟 SDK 的语义定义绑死，下次它再加状态码不用回来对数值。

### 3.2 `FxSdkConnector.cs:782` —— MidResults 没了

1.6 删了 `ChannelResult.MidResults`，语义移到 `Assist`（`EchoAssist?`，结构化的选点信息：Mx/Zx/Fx/PeriodNs 等）。

处置：**DTO 字段保留，来源置 null**。`FxChannelResultDto.MidResults` 在 WAL 序列化和 7 个测试文件里都有引用（`LocalFileMeasurementWalTests:157` 明确断言往返），删字段动静太大没收益。`Assist` 里真正有用的两个值（Fx/PeriodNs）在路径 B 的重标定链路里单独取用（见步骤 4），不整包进 DTO。

```csharp
MidResults: null);  // 1.6 删了 MidResults（语义移到 Assist）；字段留给 WAL 兼容，自动法锚点走重标定链路取 Assist
```

### 3.3 测试 `FakeSession` —— 补新抽象成员

1.6 `IoSession` 新增 `protected abstract void RequestClose(SessionCloseReason reason, Exception? exception)`（`Core/IoSession.cs:54`）；`SessionToken`、`SendDataAsync` 签名没变。`MultiChIotDecodeResyncTests` 的 FakeSession 补一行：

```csharp
// 解码测试不碰连接生命周期，收到关闭请求不用做事
protected override void RequestClose(SessionCloseReason reason, Exception? exception) { }
```

这支测试锁定的 CRC 坏帧重同步行为 1.6 已进上游（`ResynchronizeAfterCorruptFrame`），**测试保留**，钉住厂家别在下个版本又退回去。

### 3.4 注释修正（编译不报错，但已经是错话）

- `FxSdkConnector.cs:777`：`Gain: 0f` 的注释"SDK 1.4 的 ChannelResult 没有 Gain 字段"——1.6 一样没有，去掉版本号表述
- `FxSdkConnector.cs:805`：`StopAllAsync` 注释"停所有监听器 + 关所有会话"——1.6 只关会话，且调用后 FxClient 实例不可复用（我们只在 DisposeAsync 里调，语义没问题，改注释就行）
- `FxConnectorFactory.cs:23`：FxChannelInit 注释"EchoWidth=40 刻意对齐 SDK 默认值"——1.6 默认改 15 了，我方 40 是跟设备/历史行为对齐的业务选择，注释改成这个口径

---

## 4. 步骤 3：行为回齐（静默变更逐条处置）

这一步是本次升级的核心，每一条都是"编译器不拦、上线才炸"的：

### 4.1 心跳 2s + 关掉新增的接收超时断线（必做）

现场串口服务器约 5 秒无流量掐 TCP，2s 心跳是有真机故障背书的补丁（`sdk-changelog.md` 7daeea7）。1.6 回退到 20s 但做成了可配，在 `FxConnectorFactory.CreateReal` 的 DeviceConfig 上设回来：

```csharp
TcpSessionOptions = new TcpSessionOptions
{
    // 现场串口服务器 ~5s 无流量就掐 TCP，20s 默认值保不住连接（sdk-changelog 7daeea7 有真机记录）
    HeartbeatSeconds = 2,
    // 1.6 新增的"接收活动超时主动断线"先关掉：无数据重连已由 Ingestion 看门狗负责
    //（NoDataTimeoutSeconds=60），SDK 内部再来一套会抢在看门狗前制造断线，日志归因会乱
    EnableReceiveActivityTimeout = false
},
```

要不要开 appsettings 覆盖键（如 `Fx:Real:HeartbeatSeconds`）看需要——现场只有一种串口服务器行为，常量够用，别提前造配置面。

### 4.2 温度语义（必做，决策点见 §7）

1.6 把 `SpTemp` 从"原始温度过中值滤波"改成"范围保护 + τ=180s 指数平滑"，原始值挪到新字段 `RawTemp`。我方 `sp_temp_c` 列、重标定的 RefTemp 基准全吃 `SpTemp`，语义静默漂移（评估 §3.1）。

处置：**全局关掉平滑，保语义连续**。`FxConnectorFactory` 里加静态设置（与 DeveloperMode 同样的封装思路，上层不 using FxSDK.Utility）：

```csharp
// 1.6 默认给 SpTemp 上 τ=180s 指数平滑，历史数据与重标定基准全按"接近原始"的口径建的，
// 这里全局切 Direct：只保留范围保护(-40~120)，不平滑。要吃厂家平滑请先解决 sp_temp_c 新旧语义区分
FxUtility.TempTauProfile = TempTauProfile.Direct;
```

Direct 档下与 1.4 仍有两处差异，记录进 `troubleshooting.md`，不改代码：

1. **1.4 的 window=10 中值滤波没了**——PT1000 单点突跳（在 −40~120 范围内的）会直接进 `sp_temp_c`。若现场有接触不良史，后续在我方 Sink 侧补中值，不动 SDK
2. **超范围值的兜底变了**——1.4 靠中值滤波挡，1.6 用"会话内其他通道的代表温度"顶（`FxProtocolHandler.cs:307-312`）。比 1.4 出 800°C 强，接受

### 4.3 发送失败重新被吞（必做，重打补丁)

1.6 的 `IoSession.SendMsgAsync`（`Core/IoSession.cs:208`）/ `TcpSession.SendDataAsync` 回到只 `Logger.Warn` 不上抛——调用方以为命令下发了，实际白等回包到超时，心跳 missed 计数也失效。按 0cb0d86 的做法原样重打（失败上抛），`sdk-changelog.md` 记账。这是本次唯一需要重新改 SDK 源码本体的补丁。

### 4.4 comm_type=2 变成运行时炸弹（必做）

1.6 的 `FxCommunication` 遇 `CommunicationType=2` 直接抛 `NotSupportedException`（TCP Server 挪去了 FxServer）。现场都是 1（TCP Client），但字段可配，运维在设备管理页一改就炸：

- `DeviceEndpoints.cs:529-530`：`ValidateDeviceParams` 拒掉 2，文案"CommType 必须是 0 串口 / 1 TCPClient（2 TCPServer 已随 SDK 1.6 移除，设备主动连入需另立 FxServer 方案）"
- `DeviceConnectorManager.cs:236/:386`：读库遇存量 `comm_type=2` 的启用设备，记 ERROR 跳过，不让一行脏数据把连接循环炸穿
- `appsettings.json:41` 的 `//CommType` 注释同步改

### 4.5 接受不动的（记录即可）

| 变更 | 为什么接受 |
|---|---|
| 解码异常从"复位续跑"改为关会话 | 看门狗已有重连闭环；断线原因在 `SessionDisconnectedEventArgs.Reason=ProtocolError` 里可见，比 1.4 静默空转好定位 |
| `OnResultReceived` 挪到独立派发线程 | 我方事件处理本来就是"转 Channel 即走"，顺序仍保证；留意 SDK 积压 1024 批的告警日志 |
| `ConfigureChannelAsync` 改 Gain/ProbeFreq 自动清包络锚点 | 阈值法无感。自动法通道**调增益后必须重新置零**——写进标定操作规程（`calibration-field-guide.md`），路径 B 时在诊断直发端点的响应里加提醒 |
| AGC 目标峰值 600→400 | 我方没有 AGC 调用点；现场若用厂家工具跑过 AGC，注意 `Threshold≠0` 的通道有跨周期风险（评估 §4.4） |

---

## 5. 步骤 4：数据模型与锚点链路（仅路径 B）

自动法从无状态变有状态：1.6 靠 `ChannelConfig.RefEnvTime` + `RefPeriodNs` 锁定载波周期，缺了它每次重启都重新捕获，过零点可能跳到相邻周期——500 ns ≈ 数百 kN 假预紧力，系统照常入库不告警（评估 §4.2）。厂家参考实现的正确姿势就是存盘 + 回灌（`FxReader/ZeroStore.cs`）。

### 5.1 迁移 `deploy/sql/0031_sdk16_auto_anchor.sql`

```sql
-- SDK 1.6 自动法(meas_mode=0)锚点：置零时记录，重启回灌，缺了会跨载波周期漂移
ALTER TABLE channels ADD COLUMN IF NOT EXISTS ref_env_time_ns numeric;
ALTER TABLE channels ADD COLUMN IF NOT EXISTS ref_period_ns   numeric;
-- 标定系数与 TOF 提取算法版本绑定，禁止新旧混用（TryComputePreload 前置比对）
ALTER TABLE channels            ADD COLUMN IF NOT EXISTS sdk_version text;
ALTER TABLE calibration_records ADD COLUMN IF NOT EXISTS sdk_version text;
```

（`sdk_version` 两列路径 A 也建议一起加上——列是死的，比对逻辑可以后接，先把追溯字段占住。）

### 5.2 加载链（重启回灌）

现有链路已经通到底（`_initialChannelConfigs` → `OnSdkConnectedAsync` 逐通道 `ConfigureChannelAsync(sendCommand:false)`），只需把两个字段穿过去，共 3 处：

1. `FxInitialChannelConfigLoader.cs:78-80` SELECT 加 `ref_env_time_ns, ref_period_ns`
2. `FxChannelInit` 加 `RefEnvTime` / `RefPeriodNs` 两个可空字段
3. `FxConnectorFactory.ToChannelConfig` 映射进 1.6 的 `ChannelConfig.RefEnvTime/RefPeriodNs`

### 5.3 回写链（置零时落库）

`RecalibrateChannelAsync` 里锚点的算法照抄厂家（`FxReader/DeviceRunner.cs:322-327`）：

```csharp
// 自动法通道要连锚点一起写：Assist.Fx 是波形内索引，配同一帧的 EchoStart/ClockInt 换算成绝对 ns。
// 【必须同帧配对】Fx 相对的是产生它那帧的波形；_latestEchoWave 可能被 RefTime 存储读刷成参考波形，
// 拿它配最近测量帧的 Fx 会算出错误锚点——错锚点比没锚点更糟，直接把跨周期漂移做实
if (existingCfg.MeasMode == 0 && latestCr.Assist?.Fx is not null && latestCr.EchoWave is { } sameFrameWave)
{
    cfg.RefEnvTime = sameFrameWave.EchoStart + latestCr.Assist.Fx.Value * sameFrameWave.ClockInt;
    cfg.RefPeriodNs = latestCr.Assist.PeriodNs;
}
```

`RecalibrateResult` 加 `NewRefEnvTimeNs` / `NewRefPeriodNs`，`DiagnosticEndpoints.cs:1075` 的 `UPDATE channels` 跟着加两列。注意 `_latestChannelResult` 缓存的筛选条件（`:524`）不用动——Assist 挂在 ChannelResult 上，缓存里天然有；但自动法通道置零时若 `latestCr.EchoWave` 为空（波形没随帧上来），宁可不写锚点也不要拿别的波形凑对。

### 5.4 LEchoWave 持久化（路径 B 工作量主体）

当前参考波形完全没有持久化（`ToChannelConfig` 只映射 12 个标量字段），进程重启后 SDK 端没有参考波形，自动法的锚点分支直接废掉。补法：

- `channels` 加 `ref_echo_wave bytea`（EchoStart/ClockInt/WaveData 打包，格式参考 `ZeroRecord`），置零时随锚点一起写
- 加载链同 §5.2 穿透，回灌成 `ChannelConfig.LEchoWave`
- 不建议复用 WaveStore 文件引用：置零波形一通道一份、随标定基线走，跟测量波形的生命周期完全不同，放列里原子性好

---

## 6. 步骤 5 & 6：验证与上线

### 6.1 离线同源比对（判定"阈值法实质不变"是否成立的唯一手段）

新建 `tools/FxReplay`：喂 §1.2 录制的原始帧 → 逐帧输出 CSV（channel, frame#, StatusCode, EchoTime, SpTemp）。测试侧 `FakeSession + Enqueue/TryDecode` 的写法已经验证可行，推广即可。

跑法：换源 commit 的前后各构建一次，同一批帧各跑一遍，diff CSV。**验收判据：`meas_mode=2` 通道全帧 |ΔEchoTime| ≤ 1 ns**（评估 §4.1 说次奈秒级，超了就是评估结论不成立，停下来查）。SpTemp 列在 Direct 档下应与 1.4 的差异 ≤ 中值滤波量级。

### 6.2 构建与测试

- `dotnet build BoltPreload.sln -c Release` 零警告零错误（防护文件在位的前提下）
- `dotnet test` 全绿，重点看 `MultiChIotDecodeResyncTests`（重同步行为）和 WAL 往返测试（MidResults 置 null 后序列化兼容）

### 6.3 真机验收（实验室 DEV_LAB_001，观察 ≥1 周再上现场）

按厂家文档四项：静态重复性、加载/卸载、温度影响、断电恢复。额外盯四个指标：

1. 断线率——看门狗重连日志频次不高于 1.4 基线（验证心跳 2s 生效、没被新超时机制偷断）
2. `sp_temp_c` 曲线——不出现 τ=180s 特征的"慢爬"（验证 Direct 生效）
3. 自动法通道（若有）**重启进程后 EchoTime 连续**——这是 §5 整条链路的直接检验
4. 背压丢弃计数 `DroppedCount`——1.6 事件派发线程变了，确认消费侧没出现新瓶颈

### 6.4 提交切分与记账

| commit | 内容 | 回滚方式 |
|---|---|---|
| 1 | `external/FxSDK` 换源 + csproj 依赖修正 + 重打发送失败补丁 | revert |
| 2 | 编译适配 + 行为回齐（§3、§4，含 comm_type 拒 2） | revert |
| 3 | 0031 迁移 + 锚点链路（仅路径 B） | revert 代码，列留着无害 |
| 4 | tools/FxReplay + 文档 | revert |

记账三件套：`sdk-changelog.md` 新条目（保留补丁：发送失败上抛；废弃补丁：CRC 重同步已进上游、心跳改常量为配置；回退确认：BitArray 死代码 1.6 又回来了，顺手再删一次）、`external/FxSDK/README.md` 版本来源、评估文档头部标注"已按 sdk-1.6-upgrade-plan.md 执行"。

### 6.5 标定（路径 B，或路径 A 里 `Threshold≠0` 的通道）

按评估 §4.5 的分模式结论执行：

| 通道 | 动作 |
|---|---|
| `meas_mode=2, threshold=0` | 不动。同源比对通过即视为连续 |
| `meas_mode=2, threshold≠0` | 升级后不重跑 AGC；若厂家工具跑过，核对峰值仍远高于阈值 |
| `meas_mode=0/1` | 重新置零 + 全量标定，新 `calibration_records` 带 `sdk_version='1.6.0'` |

---

## 7. 决策点（拍板后再开工）

| # | 事项 | 建议 | 说明 |
|---|---|---|---|
| 1 | 走路径 A 还是 B | 跑 §1.1 SQL 定 | 这条定了工作量才定 |
| 2 | 温度策略 | **Direct**（关平滑） | 选 Balanced=180s 就得先解决 `sp_temp_c` 历史不可比 + 温补参考点偏移两个问题，收益配不上代价 |
| 3 | 接收活动超时 | **关**（false） | 与看门狗职责重叠；观察期后想换成 SDK 内建再说 |
| 4 | `measurements` 加不加语义标记列 | **不加** | 评估 §3.1 建议加；但选 Direct 后温度语义基本连续，用 `channels.sdk_version` + 部署时间点足够追溯，省一列每行成本。若拍板选 Balanced，此条推翻、必须加列 |
| 5 | 发送失败处置 | **重打 SDK 补丁** | 做法在 1.4 验证过两个月；适配层超时检测方案改动面更大且检测滞后 |

---

## 执行记录（2026-08-12）

代码侧按本方案执行完毕，几处与方案文本的偏差如实记下：

| 步骤 | 结果 | 与方案的偏差 |
|---|---|---|
| 1 源码入库 | `external/FxSDK` 整树换成 1.6（51 个 .cs），顺带清掉 1.4 时代的 `._*` 垃圾文件 | 原包中文目录在执行当天已被重命名为 `bhls1.6/sdk16/`，全库只此一份，§2 说的"两份 diff"不存在了；`sdk16/` 按原包留档保留，**不删** |
| 1 csproj | TFM 改单数 net8.0；NLog 6.1.3；Ports 10.0.9；Pipelines 8.0.0 | 方案说"多目标不动"，实际发现本目录 props 截断继承、根 TFM 到不了，必须在 csproj 里改单数（与 1.4 处理一致，README 表已有此项） |
| 1 CVE 提升复核 | **已删除** props 里两条提升 | 本机这天能连 NuGet，实测删了之后 `--vulnerable --include-transitive` 干净（FParsec 1.1.1 断了旧链），不必留 |
| 2 编译适配 | `ToCode()` / `MidResults: null` / `IsSignalValid()` / 注释修正，全部落地 | 无 |
| 2 测试桩 | `FakeSession` 与 `RawFrameReplay.ReplaySession` 各补 `RequestClose` 空实现 | 多出一处：重放台的会话桩方案里没点名 |
| 3 行为回齐 | 心跳 2s + 关接收超时（CreateReal）；温度 Direct（工厂静态构造）；发送失败补丁重打；comm_type=2 三道防线（端点校验/启动装载过滤/热加拒绝） | 重标定整组写多带了 ProbeFreq/Gain 等会话参数——执行中发现 `StoreToSession` 是整包替换，残缺 cfg 会把会话参数打回默认值，1.6 还会顺手清锚点（方案 §4 未列，属 1.6 新坑） |
| 4 数据模型 | `deploy/sql/0031_sdk16_auto_anchor.sql`：channels 加锚点两列 + `ref_echo_wave` bytea + `sdk_version`；calibration_records 加 `sdk_version`；4 处标定 INSERT、`/recalibrate` 落库、加载器回灌、`FxRefWaveCodec` 编解码全链路打通 | 锚点计算加了"同帧守卫"：只在 RefTime 真取自该测量帧时才算锚点，显式 RefTime 时锚点置空并顶掉旧值 |
| 5 验证 | 构建 0 警告 0 错误；`dotnet test` 397 过 0 挂（173 个 Postgres 集成测试无库自动跳过）；漏洞扫描干净 | 同源比对工具**不另建**：上个会话已在 `src/BoltPreload.Tests/Fx/RawFrameReplay.cs` 搭好重放台（含自身回归 8 例），本次把它适配到 1.6 即可，方案 §6.1 说的 tools/FxReplay 作废 |
| 6 记账 | `sdk-changelog.md` 新条目、`external/FxSDK/README.md` 重写、本节 | 提交切分（§6.4）留给人工：工作区里还有别的在途改动，不代拆 commit |

**遗留给现场/运维的清单**（按先后）：

1. 现场库跑 `deploy/scripts/sdk16-preflight.sql`（§1.1 两条 SQL 的完整版：多了 k_l 换算假预紧力、上报间隔、探头频率分布三项），定 A/B 档——**决定要不要走 §6.5 重新置零/标定**
2. 用**还在跑 1.4 的现场系统**录金样（FxReader rawHex=true 或 DeveloperMode 帧日志），拷回后按 §6.1 用 `RawFrameReplay` 出两份基线 CSV 做 diff（1.4 侧基线用换源提交之前的代码构建）
3. 部署时执行 `0031_sdk16_auto_anchor.sql`
4. 实验室 DEV_LAB_001 先上，观察 ≥1 周盯 §6.3 四个指标，再上现场
5. 自动法通道（若有）：升级后重新置零 + 全量标定，`/recalibrate` 现在会自动落锚点和 `sdk_version`

## 附：与评估文档的差异（2026-08-12 复核）

1. **AGC 断点不存在**——评估 §2.1 列的 `RunAgcControlAsync` 签名变更，全 src 无调用点（`Agc` 零命中），不构成编译断点
2. **`StatusCode >= 0` 不是编译断点**——字面量 0 可隐转枚举，`:524/:538` 能编译；语义也正确（1.6 新增的 11/12 是正值，`>= 0` 判有效恰好成立，与 `IsSignalValid()` 一致）
3. **实际编译断点收窄为 3 处**：`FxSdkConnector.cs:772`（枚举转 short）、`:782`（MidResults 删除）、测试 FakeSession（新增抽象成员 `RequestClose`）。我方用到的 FxClient 全部方法/事件（Connect/Close/ConfigureDevice/ConfigureChannel/SetDeviceMode/ReadDeviceParam/ReadChannelParam/StopAll/GetSessionId + 三个事件）1.6 签名未变；`EchoWave.Thr` 两版都是 `short?`；`FxProtocolHandler.Enqueue/TryDecode` 仍是 public
4. **`bhls1.6/sdk16/` 是评估期编译验证留下的 ASCII 副本**，与 `sdk1.6包/` 并存，入库时二选一、事后清理
5. 1.6 温度超范围的兜底行为补充确认：不是钳位，是"会话内其他通道代表温度顶上，没有就沿用该通道上次有效值"（`FxProtocolHandler.cs:299-312`）
