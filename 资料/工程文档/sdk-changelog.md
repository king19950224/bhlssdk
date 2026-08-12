# 厂家 FxSDK 版本与依赖变更记录

对应项目内拷贝：`external/FxSDK/`（来源见该目录 README）。我方对 SDK 源码本体的修改也记在这里，厂家升级时按此对差异。

## 2026-08-12 版本升级：C# 1.4.0 → 1.6.0

评估与实施方案见 `sdk-1.6-upgrade-assessment.md` / `sdk-1.6-upgrade-plan.md`。厂家原包留档在 `bhls1.6/sdk16/`（目录原名 `sdk1.6包`，中文名会破坏 MSBuild/PowerShell，已改 ASCII）。要点：

- **源码整树替换**：51 个 `.cs` 全部换成厂家 1.6.0；1.4 时代的 `._*` macOS 垃圾文件一并清掉。厂家 1.6 已吸收我方的 CRC 坏帧重同步补丁（`ResynchronizeAfterCorruptFrame`），`MultiChIotDecodeResyncTests` 保留钉行为。
- **重打的补丁（仅一组）**：`IoSession.SendMsgAsync` / `TcpSession.SendDataAsync` 发送失败上抛——1.6 又回到吞异常只记 Warn，0cb0d86 的补丁原样重打（TcpSession 侧含"已关闭时抛 OperationCanceledException 而非静默 return"）。
- **不再改源码的补丁**：心跳 20s→2s 不用再改 SDK 了，1.6 提供 `DeviceConfig.TcpSessionOptions`，在 `FxConnectorFactory.CreateReal` 里设 `HeartbeatSeconds=2`，同时 `EnableReceiveActivityTimeout=false`（1.6 新增的断线来源，与我方看门狗职责重叠，先关）。
- **温度语义**：1.6 默认给 `SpTemp` 上 τ=180s 平滑（原始值挪到新字段 `RawTemp`）。`FxConnectorFactory` 静态构造里全局设 `TempTauProfile.Direct` 关掉，保 `sp_temp_c` 语义连续。注意 1.4 的 window=10 温度中值滤波在 1.6 没有了，PT1000 单点突跳（−40~120 范围内）会直接进库。
- **csproj**：`TargetFrameworks=net6.0;net8.0` 改单数 `net8.0`（与 1.4 处理一致）；NLog 5.3.4→6.1.3、System.IO.Ports 8.0.0→10.0.9（沿用 2026-07-08 的统一版本）；System.IO.Pipelines 是 1.6 新依赖，取 8.0.0。
- **CVE 提升删除**：`Directory.Build.props` 里的 System.Net.Http 4.3.4 / System.Text.RegularExpressions 4.3.1 提升不再需要——1.6 csproj 显式钉 FParsec 1.1.1，System.Net.Http 4.3.0 的传递链已断。实测 `--vulnerable --include-transitive` 干净。厂家下次升级若拿掉 FParsec 显式引用，记得重扫。
- **我方适配面**（编译断点实际 3 处，比评估收窄）：`FxSdkConnector` 的 StatusCode 枚举转 short（`ToCode()`）与 `MidResults` 删除（DTO 字段保留传 null）；测试桩补 1.6 新抽象成员 `RequestClose`。`ByteUtility` 的 BitArray 死代码 1.6 又带回来了，这次不动（纯性能，等厂家回复求证清单再说）。
- **自动法有状态化**（1.6 最大的行为变化）：置零锚点 `RefEnvTime`/`RefPeriodNs` + 参考波形持久化到 `channels`（0031 迁移），重启回灌；`/recalibrate` 自动法通道会从"同一帧"的 `Assist.Fx` 算锚点落库。标定记录与通道带 `sdk_version` 追溯，防跨算法版本混用系数。
- **comm_type=2 (TCPServer) 被 1.6 移除**：入口校验拒绝、运行时装载跳过并记 ERROR。
- 验证：`dotnet build` 0 警告 0 错误；`dotnet test` 397/397 通过（173 个 Postgres 集成测试无库跳过）。**数值验收（同源比对、真机四项、重启连续性）待现场金样，见 plan §6。**

## 2026-08-05 之前的源码修改（补记，按提交追溯）

- 2026-07-31（7daeea7）：心跳间隔 20s → 2s——现场串口服务器约 5 秒无流量就掐 TCP，20s 心跳保不住连接；RefTime 存储读取波形命令修复；参数探测。
- 2026-08-04（d6b0fc6）：两处注释与实现不符修正；断电损坏波形帧不再显示成"暂无波形"。

## 2026-08-05 源码修改：解码链韧性（提交 0cb0d86）

厂家 1.4.0 的解码链一处坏全线停，且外面看不出来（TCP 和心跳还活着）。改动配套单测 `MultiChIotDecodeResyncTests`：

- `MultiChIotProtocolHandler`：CRC 失败帧原地空转 → 改为删 1 字节破坏坏帧头、重新找下一个帧头（不按长度字段整帧跳：长度本身可能就是被打坏的字节）；新增 `OnContextCleared` 复位帧头索引与分段波形缓存；节流记 CRC 失败日志。
- `IoSession.DecodingLoop`：解码超时/异常从 `break`（解码循环永久退出）改为复位解码状态继续跑。
- `IoSession.SendMsgAsync` / `TcpSession.SendDataAsync`：不再吞异常静默返回，失败上抛让调用方感知，心跳 missed 计数从此真正生效。
- `FxProtocolHandler`：加 `ResetDecodeState()` 复位入口。

## 2026-08-05 源码修改：死代码与调试残留清理（交接前整备）

只动我们运行路径（model=3 MultiChIot / TCP / FxClient），行为等价或纯日志补强；GeneralProtocolHandler、IotR24、串口等纯厂家分支不动：

- `MultiChIotProtocolHandler`：删注释掉的 Standby switch 分支与"时间解码(暂时不用)"块注释；三处 catch 空吞（`//Console.WriteLine(ex)`）补 `Logger.Warn`；EncodeDevice 异常文案补齐 LoraConfig/NetCheckConfig。
- `IoSession`：删永不置位的 `_disposed` 字段及其死分支。
- `TcpSession`：删注释掉的旧同步收发实现两行。
- `FxClient`：Logger 名从抄错的 `TcpSession` 改为 `FxClient`（日志归因）。
- `FxCommunication`：删只清不加的 `_usedPorts` 半成品；错误日志里不存在的方法名 `OnSessionExceptionCaught` 改为实际的 `OnDecodedResultReceived`。
- `ByteUtility`：删 CRC 逐字节循环里构造后即丢弃的 `BitArray`（热路径无谓分配）。
- `ChannelConfig`：`EchoWidth` 注释的默认值 15 与代码 40 对齐（业务库建档默认才是 15）。

## 2026-07-08 依赖升级（SDK 源码版本不变：C# 1.4.0）

- 核对 `external/FxSDK` 与厂家原包 `资料/FxSDK C#+Java源码+使用示例/c#/1.4.0 源码`：`.cs` 全部一致，无厂家新版可升。
- NLog `5.3.4` → `6.1.3`（`external/FxSDK` 与 `src/BoltPreload.Fx` 同步升级，与 `tools/DeviceSmokeTest` 已用的 6.1.3 统一，消除版本分叉）。
- System.IO.Ports `8.0.0` → `10.0.9`。
- MathNet.Symbolics 保持 `0.24.0`：0.25.0 依赖 `MathNet.Numerics 6.0.0-beta1` 预发布版，与 `MathNet.Filtering 0.7.0` 冲突（NU1605），不引入 beta 数学库。
- MathNet.Numerics `5.0.0`、MathNet.Filtering `0.7.0`、K4os.Compression.LZ4.Streams `1.3.8` 已是最新稳定版，不动。
- 验证：`dotnet build BoltPreload.sln -c Release` 通过（0 错误）；`dotnet test` 132/132 通过；`tools/DeviceSmokeTest` 单独编译通过。
- 注意：NLog 6 的 File/Console target 仍在核心包内，`src/BoltPreload.Api/NLog.config` 无需改动；真机联调时建议观察一次 `logs/sdk-*.log` 确认 SDK 帧日志正常输出。
- 同日产出对外汇报交付包：`资料/deliverables/FxSDK-CSharp-V1.4.1-内部升级版-20260708.zip`（内部版本号 1.4.1 = 厂家 1.4.0 源码零改动 + 本次依赖升级；含源码、FxSDK.1.4.1.nupkg、升级说明 md/docx）。**1.4.1 是内部维护版本号，非厂家官方发布**。

## 基线

- 2025-05：接入厂家 C# SDK 1.4.0（net6.0 → net8.0，关闭 GeneratePackageOnBuild，详见 `external/FxSDK/README.md`）。
