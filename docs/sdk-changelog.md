# 厂家 FxSDK 版本与依赖变更记录

对应项目内拷贝：`external/FxSDK/`（来源见该目录 README）。

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
