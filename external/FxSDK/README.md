# 厂家 FxSDK C# 1.4 — 项目内拷贝版

## 来源

厂家原版位置：`/Users/kong/bhlssdk/FxSDK C#+Java源码+使用示例/c#/1.4.0 源码/`

由北京 iFast Technology Co., Ltd. 提供。

## 为什么放在 external/ 里

- 厂家 SDK 是项目核心依赖，必须随仓库版本化（避免本机环境差异）
- 不用 NuGet 包是因为厂家没发包，且源码方式编入可以直接 step into 调试
- 不用 git submodule 是因为厂家没有公开 git 仓库

## 对原版做的改动（最小化原则）

| 项目 | 原版 | 改动后 | 原因 |
|---|---|---|---|
| `FxSDK.csproj` 的 `TargetFramework` | `net6.0` | `net8.0` | .NET 6 已 EOL（2024-11），全项目统一 .NET 8 LTS |
| `FxSDK.csproj` 的 `GeneratePackageOnBuild` | `true` | `false` | 我们不分发 SDK 包，每次 build 不需要生成 .nupkg |
| 新增 `Directory.Build.props` | 无 | 局部覆盖严格警告 | 不强迫修改厂家代码消除警告 |

`.cs` 源代码文件**一行未改**。

## 升级 SDK 版本的流程

1. 备份当前 `external/FxSDK/`
2. 把厂家新版的 `.cs` 文件全量替换到 `external/FxSDK/`（路径保持一致）
3. 重新应用上表中的三处改动（TargetFramework / GeneratePackageOnBuild / Directory.Build.props）
4. `dotnet build` 验证编译
5. 跑回归测试集（包括算法回归数据、协议解码用例）
6. 记录版本变更到 `docs/sdk-changelog.md`

## 商用合规提醒

按方案 §9.16 SDK 供应商风险：

- 合同必须要求厂家提供完整协议文档
- 合同必须要求 SDK 源码 escrow（厂家失联时我们能拿到源码自维护）
- 关键协议常量已经在 `docs/plan.md §8.1` 记录留底（即使没 SDK 也能重写编解码）
