# BoltPreload — 螺栓预紧力超声测量

用超声波量螺栓的回波渡越时间，反算这颗螺栓当前被拧到多紧。设备侧接厂家 FxSDK 1.6（C# 源码编入，`bhls1.6/sdk16`）。

产品线只有两个软件（2026-08-28 定型）。此前的在线监测平台（ASP.NET API + PostgreSQL + Vue 控制台）与 Avalonia 工位软件试验线已整体废弃并从仓库移除。

## 两个软件

### 1. FxMonitor（现场桌面软件，运维/安装用）

`src/FxMonitor` — WinForms 程序：连上设备看实时波形/夹紧力/声时差，加载 `.ucf` 标定文件；
「参数下发…」按通道段批量写可在线改的参数（探头频率/回波窗/回波宽度/增益/脉冲组），写完逐项读回核对。

```powershell
dotnet run --project src/FxMonitor              # 或 FxMonitor.exe [ip:port] [--auto] [--demo]
dotnet publish src/FxMonitor -c Release         # 自包含单文件 exe，拷走就能跑
```

### 2. BoltPreload.Monitor（业主网页大屏）

`src/BoltPreload.Monitor` — 自托管只读监视器：本机 SDK 直连设备，浏览器（局域网任意电脑/手机）开网页就能看，无需登录。历史数据落本地 SQLite（exe 旁 `data/history.db`），标定用 exe 旁的 `.ucf` 文件。

```powershell
dotnet run --project src/BoltPreload.Monitor    # 默认 http://0.0.0.0:5080
```

**注意：设备只允许一路 TCP 连接** —— FxMonitor 和大屏不能同时连同一台设备，现场调参先停大屏。

页面配置在 `wwwroot/farm.json`，改完刷新页面即生效（`device` / `channels` / `thresholds` 由服务端启动时读取，改这三项要重启）：

| 字段 | 含义 |
|---|---|
| `device` | 设备 `ip:port`，服务端自动连接、断线重连 |
| `channels` | **实际接了探头**的通道，服务端只轮询这些 |
| `plannedChannels` | 全局缺省的规划探头位（含未接的），页面按它固定显示 8 个位子，没接的标"待接入" |
| `thresholds.ratedKn / warnRatio / alarmRatio` | 额定预紧力与偏低/松动判定比例 |
| `bind.turbine / bind.flangeIndex` | 设备装在哪台机组的哪个法兰（`flanges` 下标） |
| `flanges[].boltCount` | 该法兰螺栓总数，全部画出 |
| `flanges[].kind` | `"root"` 表示叶根（画成层合板筒 + 变桨轴承），不写按塔筒钢法兰画 |
| `flanges[].plannedChannels` | 该法兰自己的规划探头位，覆盖全局 `plannedChannels`（叶根法兰预设 12 位）；写了的法兰在机组图上也会画出这些位子 |
| `flanges[].numbering` | `{ topBolt, clockwise }`：图纸上正上方那颗的编号、编号方向，用来和图纸对号 |
| `flanges[].channelBolts` | `{ "通道": 螺栓号 }`，每个探头实际装在哪颗螺栓上；没配的从 #1 起均匀占位 |
| `flanges[].marks` | 可选，`[{ at: 螺栓号, label }]`，在环外标基准位置 |

页面路由：`#/farm` 总览、`#/turbine/N` 任一机组、`#/flange/i` 当前机组任一法兰（没接入的也能看画面，只是没数据）、`#/alarms`、`#/report`。

机组结构页是 Three.js 三维场景（`wwwroot/tower3d.js`，库文件 vendored 在 `wwwroot/vendor/three`，MIT，随 exe 一起发布，不依赖外网）；浏览器没有 WebGL 时自动退回 SVG 版。

## 仓库结构

- `src/FxMonitor` — 桌面软件（监视 + 参数下发）
- `src/BoltPreload.Monitor` — 网页大屏
- `src/FxDevice` — 两者共用的设备连接引擎（协议分组、写后读回判定都在这里）
- `bhls1.6/` — 厂家 C# SDK 1.6 源码
- `bhls1.6/FxReader` — 厂家示例 CLI（抓 raw / 打零点），不进主方案、不随现场发布
- `资料/工程文档/` — 历史设计与现场记录（多针对已废弃平台，不作现行手册）
- `tmp/` — 本机联调草稿（gitignore，不入库）

## 环境

- .NET 8 SDK；目标机 Windows（FxMonitor 为 WinForms）
