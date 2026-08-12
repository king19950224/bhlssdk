# 厂家 FxSDK C# 1.6 — 项目内拷贝版

## 来源

厂家 2026-08-10 交付的 1.6.0 包，原包留档：`bhls1.6/sdk16/`（目录原名 `sdk1.6包`，
中文目录名在非 UTF-8 代码页下会破坏 MSBuild/PowerShell，已改 ASCII）。

由北京 iFast Technology Co., Ltd. 提供。1.4 时代的源码在 git 历史里（2026-08-12 换源提交之前）。

## 为什么放在 external/ 里

- 厂家 SDK 是项目核心依赖，必须随仓库版本化（避免本机环境差异）
- 不用 NuGet 包是因为厂家没发包，且源码方式编入可以直接 step into 调试
- 不用 git submodule 是因为厂家没有公开 git 仓库

## 对原版做的改动（最小化原则）

| 项目 | 原版 | 改动后 | 原因 |
|---|---|---|---|
| `FxSDK.csproj` 的 `TargetFrameworks` | `net6.0;net8.0` | 单数 `net8.0` | .NET 6 已 EOL，全项目统一 .NET 8 LTS；本目录 props 截断根级继承，根上的单数 TFM 到不了这里 |
| `FxSDK.csproj` 的 NLog | `5.3.4` | `6.1.3` | 与 `src/BoltPreload.Fx`、`tools/DeviceSmokeTest` 统一（2026-07 依赖统一） |
| `FxSDK.csproj` 的 System.IO.Ports | `8.0.0` | `10.0.9` | 同上 |
| `FxSDK.csproj` 的 System.IO.Pipelines | 条件式 6.0.3/8.0.0 | `8.0.0` | 单 TFM 后条件无意义 |
| `Core/IoSession.cs` 的 `SendMsgAsync` | 发送失败吞异常记 Warn | 失败上抛 | 【源码补丁】调用方必须感知指令没发出去，心跳 missed 计数依赖它；1.4 同款补丁（0cb0d86）重打 |
| `Core/TcpSession.cs` 的 `SendDataAsync` | 已关闭静默 return、异常全吞 | 已关闭抛 OCE、异常上抛 | 【源码补丁】同上 |
| `Directory.Build.props` | 无 | 局部覆盖严格警告 | 不强迫修改厂家代码消除警告。1.4 时代的 CVE 提升已删（1.6 显式钉 FParsec 1.1.1，漏洞链已断，实测扫描干净） |

除上表两处源码补丁外，`.cs` 文件与厂家原包一致。1.4 时代的其余补丁去向：CRC 坏帧重同步已进厂家上游；
心跳 2s 改由 `FxConnectorFactory.CreateReal` 的 `TcpSessionOptions` 配置，不再动 SDK 源码。

## 1.6 的关键行为变化（对接层已处理，动 SDK 前先读）

- `SpTemp` 默认带 τ=180s 平滑，原始温度在新字段 `RawTemp`——我方在 `FxConnectorFactory` 静态构造里
  全局设 `TempTauProfile.Direct` 关掉了平滑，别在别处再改这个全局量
- 自动法(MeasMode=0)有状态：置零锚点 `RefEnvTime`/`RefPeriodNs` + `LEchoWave` 持久化在
  `channels` 表（0031），重启由 `FxInitialChannelConfigLoader` 回灌
- `StatusCode` 是 `MeasStatusCode` 枚举（底层 short，数值与 1.4 一致，新增 11/12 业务超限）
- `CommunicationType=2`(TCPServer) 直接抛异常，设备主动连入要用 1.6 新增的 `FxSDK.Server.FxServer`（我方未接入）
- `ConfigureChannelAsync` 时 ProbeFreq/Gain/EchoWidth/MeasMode/脉冲参数任何一项变化都会清包络追踪锚点
  （`SessionContext.StoreToSession` 整包替换 + `HasEnvelopeTrackingRelevantChange`）——自动法通道改增益后必须重新置零

## 升级 SDK 版本的流程

1. 厂家新包先留档到仓库（ASCII 目录名，清 `__MACOSX`/`.DS_Store`/`*.user`/`bin`/`obj`）
2. 把新版 `.cs` 全量替换到 `external/FxSDK/`（保留本 README 与 `Directory.Build.props`）
3. 重新应用上表改动（TFM / 依赖版本 / 源码补丁），逐条核对补丁是否已进上游
4. `dotnet build` 验证编译；`dotnet list package --vulnerable --include-transitive` 复扫
5. 跑回归测试集（重点：`MultiChIotDecodeResyncTests` 重同步、`RawFrameReplayTests` 重放台）
6. **同源比对**：拿现场录制的原始帧（`RawFrameReplay.LoadRawFramesFromCsv`）新旧两版各跑一遍，diff 基线 CSV
7. 记录版本变更到 `资料/工程文档/sdk-changelog.md`，更新 `FxConnectorFactory.SdkVersion` 常量

## 商用合规提醒

按方案 §9.16 SDK 供应商风险：

- 合同必须要求厂家提供完整协议文档
- 合同必须要求 SDK 源码 escrow（厂家失联时我们能拿到源码自维护）
- 关键协议常量已经在 `资料/工程文档/plan.md §8.1` 记录留底（即使没 SDK 也能重写编解码）
