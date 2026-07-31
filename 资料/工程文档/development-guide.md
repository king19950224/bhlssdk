# BoltPreload 螺栓预紧力在线监测系统 开发文档

文档版本：V1.0
编写日期：2026年06月10日

## 目录

1. 概述
2. 开发环境
3. 供应商资料
4. 系统架构与通讯方式
5. 接口说明
6. 快速上手
7. 工程结构说明
8. 开发注意事项
9. 常见问题

---

## 1. 概述

这是一套螺栓预紧力在线监测系统，基于超声波螺栓预紧力测量设备及配套开发，通过测量超声回波渡越时间（TOF）反算螺栓预紧力，提供 7×24 小时实时监测、报警管理、历史趋势、在线标定、报告生成等核心能力，适用于风电塔筒、桥梁、压力容器法兰等关键设施的螺栓预紧力监测场景。

系统采用 B/S 架构，核心设计要点：

- 后端基于 .NET 8 构建，厂家 SDK 以源码方式编入工程，便于调试与维护；
- 设备接入层与业务层解耦，通过统一接口（IFxConnector）屏蔽 SDK 细节。进程内模拟器（`Fx:Mode=Simulated`）只服务于无真机的开发与自动化测试：Production 环境配 Simulated 会拒绝启动（见 Program.cs 开头的防线），独立模拟器工具 `tools/FxDeviceSimulator` 也不随交付包发布；
- 测量数据采用时序数据库存储（PostgreSQL + TimescaleDB），支持高频写入与历史查询；
- 前端通过 REST API 与 SignalR（WebSocket）获取数据，实现实时刷新。

本系统属于商用关键基础设施监测系统，算法、阈值、报警规则的设计与修改均须可追溯至国家标准（GB/T 43232-2023、GB/T 41261-2022 等）、SDK 源码或现场标定数据，详见 plan.md §0 商用底线原则。

## 2. 开发环境

### 2.1 后端

| 组件 | 技术选型 | 说明 |
|---|---|---|
| 语言/运行时 | C# 12 / .NET 8.0（LTS） | 全部后端代码 |
| Web 框架 | ASP.NET Core Minimal API | REST 接口，开发环境提供 Swagger 文档 |
| 实时推送 | SignalR | 测量数据、报警的实时推送 |
| 数据访问 | EF Core + Npgsql | 业务表使用 EF Core；测量数据高频写入使用原生 Npgsql |
| 日志 | Serilog | 控制台 + 按天滚动文件（logs/，保留 90 天） |
| 数值计算 | MathNet.Numerics / MathNet.Filtering | 滤波、曲线拟合 |
| 设备 SDK | FxSDK C# 1.4.0 | 源码位于 external/FxSDK/，随工程编译 |

### 2.2 前端

| 组件 | 技术选型 | 版本 |
|---|---|---|
| 框架 | Vue 3（Composition API）+ TypeScript | 3.5.x |
| 构建工具 | Vite | 8.x |
| UI 组件库 | Element Plus | 2.14.x |
| 图表 | ECharts | 6.x |
| 状态管理 | Pinia | 3.x |
| 路由 | vue-router | 4.x |
| 实时通讯 | @microsoft/signalr | 10.x |

### 2.3 数据库

| 项目 | 说明 |
|---|---|
| 数据库 | PostgreSQL 16 + TimescaleDB 2.17（时序扩展） |
| 开发环境 | Docker 容器运行（deploy/docker-compose.yml），仅供开发 |
| 生产环境 | Windows 工控机原生安装（Inno Setup 打包），不使用 Docker|
| 结构变更 | SQL 迁移脚本 deploy/sql/0001~00NN_*.sql，按文件名序号顺序执行，不使用 EF 迁移 |

### 2.4 开发工具要求

| 工具 | 是否必须 | 说明 |
|---|---|---|
| .NET 8 SDK | 必须 | <https://dotnet.microsoft.com/en-us/download/dotnet/8.0> |
| Docker Desktop | 必须 | 本地运行 PostgreSQL + TimescaleDB |
| Git | 必须 | — |
| Node.js 20+ | 前端开发必须 | — |
| IDE | 推荐 | Rider / Visual Studio / VS Code + C# Dev Kit |

开发机操作系统支持 macOS / Linux / Windows。

## 3. 供应商资料

### 3.1 供应商信息

| 项目 | 内容 |
|---|---|
| 供应商 | 北京 iFast Technology Co., Ltd.（艾法斯特） |
| 产品 | 超声波螺栓预紧力测量设备 + FxSDK 开发工具包 |
| SDK 版本 | C# 1.4.0（本系统使用）；Java 1.0（仅参考，字段定义与 C# 1.4 存在差异） |

### 3.2 SDK 交付物

厂家原始交付物位于 `资料/FxSDK C#+Java源码+使用示例/`：

| 交付物 | 说明 |
|---|---|
| c#/1.4.0 源码 | 本系统实际使用的 SDK 版本 |
| FxSDK V1.3.3 使用说明文档.docx | 厂家官方使用说明。注意该文档对应 1.3.3 版本，部分内容已过时（见 4.2 节说明） |
| java/1.0 源码 + 使用文档 | 仅作参考，不作为开发依据 |
| 业务层面重连部分.txt | 厂家提供的重连参考实现，本系统重连逻辑依此落地 |

### 3.3 SDK 集成方式

实际编译使用的 SDK 位于 `external/FxSDK/`，为厂家原版源码的工程内拷贝。集成原则：

- `.cs` 源码文件保持原样，未做任何修改；
- 仅调整 `FxSDK.csproj` 三处（TargetFramework 由 net6.0 改为 net8.0 等），明细见 `external/FxSDK/README.md`；
- 未采用 NuGet 引用（厂家未发布包）；以源码方式编入便于断点调试；
- 升级 SDK 版本时须同步更新 `资料/工程文档/sdk-api-facts.md`，否则不允许合并。

### 3.4 设备型号

设备型号由 `DeviceConfig.Model` 指定，共四种：

| Model | 名称 | 关键参数 |
|---|---|---|
| 0 | 标准版 | 非主力型号 |
| 1 | 专家版 | 非主力型号 |
| 2 | 实时 24 通道版 | 采样率 100 MHz（间隔 10ns），固定 24 通道，增益范围 0~80（步长 0.1） |
| 3 | 多通道监测版（本系统现场使用） | 采样率 8 MHz（间隔 125ns，SDK 内部插值至 25ns），单板最多 48 通道，增益范围 1~47（步长 1） |

注意：model=2 与 model=3 的采样率、增益范围、协议帧头均不相同，开发时不可混用，参数对照见 sdk-api-facts.md §6、§7。

### 3.5 供应商风险条款

按 plan.md §9.16 要求：合同须包含协议文档交付与 SDK 源码 escrow 条款；关键协议常量已在 sdk-api-facts.md 及 plan.md §8.1 留档，保证供应商失联时具备自主维护能力。

## 4. 系统架构与通讯方式

### 4.1 总体架构

```
浏览器（Vue 3 前端）
   │  REST /api/*    +    SignalR /hubs/measurements（WebSocket）
   ▼
ASP.NET Core 8 后端（BoltPreload.Api 及下属各层）
   │                                │
   │ FxSDK（TCP / 串口）             │ SQL（Npgsql / EF Core）
   ▼                                ▼
超声波测量设备                  PostgreSQL 16 + TimescaleDB
（最多 48 通道）                （测量数据为时序表）
```

### 4.2 设备通讯（FxSDK 层）

#### 4.2.1 通信方式

设备支持三种通信方式，由 `DeviceConfig.CommunicationType` 指定：

| 值 | 通信方式 | CommunicationParameters 格式 | 说明 |
|---|---|---|---|
| 0 | 串口 | `"COM3:115200,0,8,1"` | COM号:波特率,校验位,数据位,停止位 |
| 1 | TCP Client（默认，本系统使用） | `"192.168.0.7:23"` | SDK 作为客户端连接设备 IP:端口 |
| 2 | TCP Server（beta） | `"IP:Port,监听端口"` | SDK 作为服务端，设备主动连接 |

传输模式由 `TransferMode` 指定：0 为 LoraMesh 无线中继，1 为直连。实验室真机为直连 TCP，地址 `192.168.0.7:23`。

#### 4.2.2 协议帧结构（model=3）

底层协议为厂家私有二进制帧，编解码由 SDK 完成（`external/FxSDK/Codec/MultiChIotProtocolHandler.cs`），开发时无需手工处理，以下常量供抓包排障时参考：

| 项目 | 值 |
|---|---|
| 帧头 | `0xDE 0xDE 0xDE 0xDE` |
| 帧尾 | `0x00 0x00 0x00 0x00` |
| LoRa 唤醒头 | `0xF0 × 5` |
| 写参数命令 | `0x21` |
| 读参数命令 | `0x22` |
| 写/读设备本地存储 | `0x41` / `0x42` |

#### 4.2.3 工作模式

`WorkMode` 枚举定义设备工作模式，下发后设备按模式运行：

| 值 | 模式 | 说明 |
|---|---|---|
| 0 | Standby | 待机，停止采集 |
| 1 | SingleMeas | 单通道测量 |
| 2 | MultiMeas | 多通道轮询测量（7×24 监测主模式） |
| 3 | DualMeas | 双波测量 |
| 4 | ReadParam | 读取参数 |
| 5 | FullWaveRead | 读取全波 |
| 6 | Reboot | 重启设备 |
| 7 | LWaveRead | 纵波扫波 |
| 8 | TWaveRead | 横波扫波 |

#### 4.2.4 事件模型

SDK 采用异步非阻塞通信、事件驱动回调。核心流程为“连接设备 → 配置参数 → 设置工作模式 → 接收数据”：

```csharp
var client = new FxClient();
client.OnConnected      += OnConnected;       // 会话连接成功
client.OnDisconnected   += OnDisconnected;    // 会话断开
client.OnResultReceived += OnResultReceived;  // 接收解码结果
await client.ConnectAsync(deviceConfig);
```

接收数据的解析方式：

```csharp
private void OnResultReceived(object? sender, DecodedResultsReceivedEventArgs args)
{
    foreach (var result in args.Results)   // 注意：属性名为 Results
    {
        switch (result)
        {
            case ChannelResult chResult:        // 单通道测量结果
                // chResult.EchoTime / DeltaTof / SpTemp / EchoWave
                break;
            case MultiChannelResult mcResult:   // 多通道测量结果
                break;
            case DeviceParameter param:         // 参数回读结果
                break;
        }
    }
}
```

注意：厂家《FxSDK V1.3.3 使用说明文档》示例代码中使用的 `args.Data` 属性在 1.4.0 版本中已不存在，1.4.0 的事件参数属性为 `args.Results`（`DecodedResult[]` 类型），以 SDK 源码为准。

#### 4.2.5 EchoTime 计算前置条件（重要）

SDK 收到上行波形后，仅当其本地 `SessionContext` 中存在对应 `(address, channel)` 的 `ChannelConfig` 且 `ProbeFreq > 0` 时，才会执行 EchoTime 计算。因此接入真实设备时必须按以下顺序操作：

1. 调用 `ConfigureDeviceAsync(..., sendCommand:false)` 将 `DeviceConfig` 写入 SDK 本地会话；
2. 调用 `ConfigureChannelAsync(..., sendCommand:false)` 将各监测通道的 `ChannelConfig` 写入 SDK 本地会话（`sendCommand:false` 仅写本地，不下发设备）;
3. 再调用 `SetWorkModeAsync(MultiMeas)` 下发工作模式。

若缺少步骤 1、2，设备仍会持续上报波形与温度，但 `EchoTime` 恒为 null，且 `StatusCode` 仍为正常值 0，故障现象不易识别。详见 sdk-api-facts.md §1.1。

### 4.3 后端内部数据流

```
FxClient（SDK 收包、协议解码）
   │ event OnResultReceived
   ▼
FxSdkConnector（src/BoltPreload.Fx/）
   │ 事件转为 Channel<FxDecodedEnvelope>（有界 8192，满时丢弃最旧）
   ▼
IngestionService（BackgroundService 常驻）
   │ 通道号 → 螺栓路由（带缓存）
   ├──> measurements 时序表落库，波形文件写 data/waves/
   ├──> 报警引擎判定 → alarms 表
   └──> SignalR 推送前端
```

设计要点：

- 业务层不直接依赖厂家 SDK。所有设备操作经由 `BoltPreload.Fx.IFxConnector` 接口：真机实现为 `FxSdkConnector`，开发/测试实现为 `SimulatedFxConnector`（进程内模拟器），通过配置项 `Fx:Mode`（`Simulated` / `Real`）切换，业务代码无需改动；
- 入库兜底：数据库不可用时测量数据先写本地 WAL（`data/wal/`），数据库恢复后由 WalReplayService 自动重放补写；
- 断线看门狗：默认 60 秒未收到数据自动重连，并重新下发工作模式。

#### 4.3.1 探头热插拔与检测即螺栓（Fx:HotPlug）

设备只测量 MCR 轮询集内的通道（probe-scan 设计事实 F1），因此热插拔识别的物理前提是**常态轮询全部物理通道**（1..`devices.channel_count`），由 `ChannelPresenceService` 在设备连接就绪后下发（会话 MCR 快照已是全通道则跳过；probe-scan / 验收会话期间让位，结束后收敛恢复）。

- **插入识别**：探头插入空闲口 → 下个轮询周期起该通道有回波 → `ChannelPresenceTracker`（采集热路径纯内存状态机）要求连续 `PlugConsecutiveEchoes`（默认 3）条**合理且稳定**的回波（声时 ≥ `MinPlausibleEchoNs`，序列抖动 ≤ `PlugStabilityBandNs`）才判"插入"→ 更新 `channels.hardware_state=healthy` / `last_present_at`、SignalR 推 `OnChannelPresence`；
- **检测即螺栓（`Fx:HotPlug:AutoEnroll`，默认 true）**：在位且通道无活跃螺栓时，`BoltAutoEnroller` 自动建档（编号中文“通道X-Y号螺栓”——每 10 个物理口为一“通道”束、束内从 1 数到 10，第 13 口即“通道2-3号螺栓”，见 `ChannelNaming`；缺省规格 M20/8.8，目标预紧力空，待现场补全）。归属组：`devices.default_group_id` → 设备同项目唯一活跃组兜底 → 否则跳过并提示去设备页设置。设置默认组的 PATCH 会对当前在位未建档通道立即补建档；
- **删除抑制（`channels.auto_enroll_hold`）**：人工解绑/删除且探头仍在位时置 hold，阻止立刻重建；真正离位（Present=false）后清 hold，再插入才重建。**进程重启不清 hold**（避免“删了重启又回来”）；
- **拔出识别**：在位通道静默超自适应阈值（`max(UnplugSilenceSeconds, 通道数×PerChannelMeasureMs×UnplugSilenceCycles)`）→ `hardware_state=no_data`。假静默（离线 / 扫描中 / 整机无上报 / 通道不在 MCR 集）挂起判定；
- **识别页统计口径**：通道数 → 在位 → 螺栓（在测 / 失联=曾在位现无信号 / 待接入=从未在位 / 待补规格=目标预紧力空）。孔位（`expected_bolt_count`）是结构配置，监测数是已建档探头，二者不自动同步；
- **数据卫生**：空口或低于下限门的杂散未绑定数据不入库（probe-scan 期间放行）；有合理回波的未绑定数据落 `unbound_measurements`，建档后回填。保留期由 `UnboundRetention` 后台清理；
- **分层**：在位/断开是 UI 秒级显示；安全告警 `signal_lost`（120s）由 SignalHealthService 独立判定；
- **代价与开关**：全通道轮询稀释单通道刷新率（预紧力缓变量可接受）。`Fx:HotPlug:Enabled=false` 关热插拔；`AutoEnroll=false` 关自动建档（纯点位表流程）；
- **License 口径**：授权通道数按 SDK 会话已配置通道统计，全通道轮询不改变该口径。

### 4.4 前后端通讯

| 通道 | 协议 | 地址 | 说明 |
|---|---|---|---|
| REST API | HTTP + JSON | `/api/*` | 增删改查与操作命令；JWT 认证，`POST /api/auth/login` 获取 token 后以 Bearer 头携带 |
| 实时推送 | SignalR（WebSocket） | `/hubs/measurements` | 服务器到客户端单向推送 |

匿名（无需登录）的端点仅限：`/api/health`、`POST /api/auth/login`、以及整个 `/api/license/*`（机器码 `GET /api/license/machine-code` 与授权状态）。其余所有接口均要求登录（JWT），敏感写操作另按角色（operator/reviewer/admin）授权。

SignalR 推送事件：

| 事件名 | 内容 |
|---|---|
| OnMeasurement | 单条测量结果（设备、通道、EchoTime、温度、预紧力等） |
| OnAlarm | 报警的产生 / 确认 / 清除 |
| OnDeviceParameter | 设备参数回读结果 |
| OnChannelPresence | 探头热插拔在位变化（deviceId、channelNo、present、绑定螺栓编号；`initial=true` 为进程启动后的首次确认；自动建档成功时带新 boltId/code） |

开发环境下前端 Vite 端口 5173，`/api` 与 `/hubs` 均代理至后端 5085 端口（`web/vite.config.ts`），无跨域问题。

## 5. 接口说明

### 5.1 REST API

完整接口文档以运行时 Swagger 为准：<http://localhost:5085/swagger>（dev.sh 启动）或 <http://localhost:5000/swagger>（dotnet run 默认）。接口分组如下，源码位于 `src/BoltPreload.Api/Endpoints/`：

| 分组 | 路由前缀 | 功能 |
|---|---|---|
| 认证 | `/api/auth` | 登录、当前用户、用户管理 |
| 测量数据 | `/api/measurements` | 最新数据、按螺栓查询、计数 |
| 波形 | `/api/waves` | 最近波形、波形文件、特征、参考波形比对 |
| 通道 | `/api/channels` | 通道列表、未绑定通道、通道矩阵、绑定/解绑螺栓 |
| 螺栓 | `/api/bolts` | 螺栓列表、预紧力趋势、趋势预测、参数设置 |
| 报警 | `/api/alarms` | 查询、确认、清除、搁置、汇总 |
| 标定 | `/api/channels/{id}/...` | 快速标定、力矩标定、力矩台阶测点、拟合应用、载荷标定、复核会签 |
| 设备诊断 | `/api/devices/{id}/diagnostic` | 设备/通道参数读写、轮询配置、工作模式切换、真机健康检查 |
| 通道自动发现 | `/api/devices/{id}/channels/probe-scan` | 发起扫描、查询结果、中止 |
| 配置变更审批 | `/api/config-changes` | 审批、执行、验证、查询（两人四眼）；变更【申请】入口为 `POST /api/devices/{id}/diagnostic/configure-channel/{ch}` |
| 报告 | `/api/reports` | 力矩台阶报告（`/torque-step/{channelId}`）、法兰验收报告、下载、签字 |
| 资产 | `/api/projects` 等 | 项目→风机→监测组→螺栓 四级资产树 |
| 审计日志 | `/api/audit-log` | 查询、CSV 导出 |
| 授权许可 | `/api/license` | 机器码、license 状态（匿名可访问） |
| 仪表盘 | `/api/dashboard/preload-health` | 全场预紧力健康度 |
| 运行时 | `/api/runtime/devices` | 设备运行时状态、WAL 积压、授权容量 |
| 健康检查 | `/api/health` | 匿名可访问 |

> 注：上表为分组概览，`recalibrate`（重标定）实际路由在 `POST /api/devices/{id}/channels/{ch}/recalibrate`（设备诊断组）。完整、权威的端点清单以运行时 Swagger 为准。

> **标定信任分级与两人四眼（商用底线 §0）**：标定分两类，靠 `calibration_records.review_state` 区分是否真有第二人签字——
> - `load-calibrate`（正式多点载荷标定）：提交后置 `pending`，**必须由另一名 reviewer** 调 `countersign` 才激活，`review_state='approved'`，是真正的两人会签，可作为正式验收依据；
> - `quick-calibrate` / `torque-calibrate` / `apply-torque-fit`：现场自签的临时/工程估算标定，立即生效但 `review_state='self_signed'`（单人，非会签），有效期短，**不得作为安全验收结论**（trust 等级 temporary/engineering_estimate，见 `/api/calibration-center/summary`）；
> - `recalibrate`（写设备 RefTime/RefTemp 基线）：需 reviewer+ 权限，全程写审计。
>
> 故 `review_state='approved'` 严格等价于“已通过两人会签”，报告与审计可直接信任该字段。

### 5.2 设备适配层接口（IFxConnector）

后端与设备交互的唯一入口为 `src/BoltPreload.Fx/IFxConnector.cs`，核心成员：

| 成员 | 功能说明 |
|---|---|
| `Results` | 解码结果流（`ChannelReader<FxDecodedEnvelope>`），下游 `await foreach` 消费 |
| `ConnectAsync` / `DisconnectAsync` / `ReconnectAsync` | 连接管理；重连保证 Results 通道不中断 |
| `SetWorkModeAsync` | 切换设备工作模式 |
| `ReadDeviceParamAsync` / `ReadChannelParamAsync` | 读取设备/通道参数，结果经 Results 流异步返回 |
| `ConfigureChannelAsync` | 写通道参数（增益、阈值、RefTime 等；null 字段不更新） |
| `ConfigureMultiChannelPollingAsync` | 配置多通道轮询启用通道 |
| `RecalibrateChannelAsync` | 在线重标定，将当前 TOF/温度写为新基线 RefTime/RefTemp |
| `State` / `DeviceId` | 连接器当前状态（`FxConnectorState`）/ 所属设备 id（多设备路由用） |
| `GetSessionChannelConfigs()` | 读取 SDK 本地会话已注入的通道配置（诊断/运行时展示用） |

### 5.3 测量结果（ChannelResult）

| 字段 | 类型 | 说明 |
|---|---|---|
| Address / Channel | ushort | 设备地址 / 通道号 |
| StatusCode | short | 状态码，见下表 |
| EchoTime | decimal? | 纵波 TOF（ns），可空，为 null 的原因见 4.2.5 节 |
| SpTemp | decimal? | 温度（°C） |
| DeltaTof | decimal? | 声时差（ns），SDK 已做温度补偿 |
| EchoWave | EchoWave? | 波形数据 |

状态码定义（C# SDK 1.4）：

| StatusCode | 含义 | 处理建议 |
|---|---|---|
| 0 | 正常 | 正常入库 |
| -1 | 信号过小 | 检查耦合或增益 |
| -2 | 阈值异常 | 调整阈值或执行 AGC |
| -3 | 信号溢出 | 降低增益 |
| -4 | 相似度低 | 检查耦合状态或螺栓状态变化 |
| -5 | 未知异常 | 进一步排查 |
| -6 | 信噪比低 | 重涂耦合剂 |

注意：-3、-4 的含义与 Java 1.0 文档不一致，以 C# SDK 1.4 为准。完整字段表及可空性说明见 sdk-api-facts.md。

## 6. 快速上手

### 6.1 环境准备

- 依赖项：.NET 8 SDK、Docker Desktop（已启动）、Git；前端开发另需 Node.js 20+；
- 验证：`dotnet --version` 输出 8.0.x；`docker info` 无报错。

### 6.2 获取代码

```bash
git clone <仓库地址> bhlssdk
cd bhlssdk
```

厂家 SDK 已以源码形式包含在 `external/FxSDK/`，无需单独安装任何厂家软件。

### 6.3 初始化

```bash
bash setup.sh
```

该脚本依次执行：前置工具检查 → Docker 启动 PostgreSQL+TimescaleDB（自动建库、按序执行 `deploy/sql/*.sql`、写入开发种子数据）→ NuGet 还原 → 编译 → 单元测试。全部通过即环境就绪。

### 6.4 启动系统

```bash
./dev.sh
```

固定端口：数据库 5432、后端 5085、前端 5173。启动完成后：

- 前端：<http://localhost:5173>，登录账号 `admin / admin123`（仅开发环境自动创建）
- 后端 Swagger：<http://localhost:5085/swagger>
- 常用命令：`./dev.sh logs` 查看日志、`./dev.sh down` 停止、`./dev.sh restart` 重启、`./dev.sh status` 查看状态

### 6.5 验证数据链路

默认配置 `Fx:Mode = Simulated`（`src/BoltPreload.Api/appsettings.Development.json`，首次启动自动从 .example 生成），由进程内模拟器产生 12 通道 5Hz 测量数据，无需真机。等待约 30 秒后验证：

```bash
curl http://localhost:5085/api/measurements/count     # 应返回数百条
curl http://localhost:5085/api/measurements/latest | jq
```

打开前端 Dashboard 应能看到实时刷新的预紧力数据与趋势图。

### 6.6 日常开发流程

| 操作 | 方式 |
|---|---|
| 修改后端 | 修改后 `./dev.sh restart`，或在 IDE 中直接 Debug 运行 BoltPreload.Api |
| 修改前端 | Vite 热更新，保存即生效 |
| 修改表结构 | `deploy/sql/` 新增序号脚本 + `deploy/docker-compose.yml` 挂载列表加一行，执行 `bash setup.sh reset` 重建（会清空数据） |
| 运行测试 | `dotnet test BoltPreload.sln` |

### 6.7 接入真实设备

1. 确认网络连通：实验室设备地址 `192.168.0.7:23`，ping 与端口连通后再继续；
2. 修改 `appsettings.Development.json`：

```json
{
  "Fx": {
    "Mode": "Real",
    "Real": {
      "DeviceId": 1,
      "Model": 3,
      "CommType": 1,
      "CommParams": "192.168.0.7:23",
      "Address": 1,
      "TransferMode": 1,
      "Channels": [
        { "Address": 1, "Channel": 9, "ProbeFreqMhz": 2, "Gain": 16,
          "MeasMode": 0, "Threshold": -490, "RefTimeNs": 32142,
          "RefTempC": 16.71, "TcMode": 1 }
      ]
    }
  }
}
```

其中 `Channels` 为注入 SDK 本地会话的初始通道配置（对应 4.2.5 节前置条件），缺失时 EchoTime 恒为 null，必须配置。

3. 重启后端，通过日志确认连接成功、MultiMeas 模式下发、数据开始入库。现场联调问题参见 troubleshooting.md 与 site-debug-2026-05-21.md。

## 7. 工程结构说明

```
bhlssdk/
├── BoltPreload.sln                  # 解决方案入口
├── setup.sh / dev.sh                # 一键初始化 / 一键启动脚本
├── src/
│   ├── BoltPreload.Domain/          # 领域模型
│   ├── BoltPreload.Application/     # 用例层：采集、报警、标定、许可
│   ├── BoltPreload.Infrastructure/  # EF Core + Npgsql 数据访问
│   ├── BoltPreload.Fx/              # SDK 适配层：IFxConnector、真机实现、模拟器
│   ├── BoltPreload.Algorithms/      # TOF、应力换算、温度补偿算法
│   ├── BoltPreload.Calibration/     # 标定算法（K_L 拟合等）
│   ├── BoltPreload.Api/             # ASP.NET Core 入口：Endpoints、Hubs、Auth
│   └── BoltPreload.Tests/           # 单元测试 + 集成测试
├── web/                             # Vue 3 前端，页面位于 src/views/
├── external/FxSDK/                  # 厂家 SDK 源码拷贝（禁止修改 .cs）
├── deploy/
│   ├── docker-compose.yml           # 开发数据库
│   ├── sql/                         # 数据库迁移脚本，按序号执行
│   └── windows/ installer/          # 工控机部署、Inno Setup
├── tools/FxDeviceSimulator/         # 独立 TCP 层设备模拟器
├── docs/                            # 客户交付文档（部署 / 标定 SOP / 排障）
└── 资料/
    ├── 工程文档/                    # 内部工程文档（本文件所在目录，不随客户交付）
    └── …                            # 厂家原始包、软著/会议材料等（不进 git）
```

## 8. 开发注意事项

1. 算法、阈值、报警规则的修改必须可追溯至国家标准、SDK 源码或现场标定数据，不接受无依据的经验值；
2. SDK 字段、状态码、常量问题以 `资料/工程文档/sdk-api-facts.md` 为准，不以 Java 1.0 文档为依据；
3. 禁止修改 `external/FxSDK/` 下的 `.cs` 文件；需要扩展行为时在 `BoltPreload.Fx` 适配层实现；
4. K_L 量纲约定：全系统 `K_L = Δt/F`，单位 ns/N，量级约 1e-4。标定证书上 1/N 量纲（约 1e-11）的数值不可直接录入，接口已设量级护栏，详见 sdk-api-facts.md §12；
5. 工程开启编译警告即错误（TreatWarningsAsErrors），提交前须保证 `dotnet build` 无警告、`dotnet test` 全部通过；
6. 连接串、JWT 密钥、license 公钥等敏感配置不得提交仓库。开发环境置于 `appsettings.Development.json`（已 gitignore），生产环境通过环境变量（如 `ConnectionStrings__Postgres`）或 user-secrets 注入；
7. 提交/接手前跑 `python3 tools/code-audit.py`（零依赖）复核代码卫生：后端 public 类型注释覆盖、前端文件头注释、活文档引用有效性、TODO/调试残留、文风统一（状态用文字不用表情符号、引号用中文弯引号）等六类门禁须全部归零。豁免清单与理由内联在脚本头部，新增豁免必须写明原因。

## 9. 常见问题

| 现象 | 处理方法 |
|---|---|
| dotnet restore 失败 | 检查 NuGet 源（`dotnet nuget list source`），必要时切换国内镜像 |
| 5432 端口冲突 | 本机已安装 PostgreSQL，停止本机实例或修改 compose 端口 |
| 日志出现"无法解析通道，丢弃数据" | 种子数据未写入，执行 `bash setup.sh reset` 重建 |
| 真机已连接但 EchoTime 全为 null | `Fx:Real:Channels` 未配置或配置错误，见 4.2.5 节 |
| 其他问题 | 参见 `docs/troubleshooting.md` |
