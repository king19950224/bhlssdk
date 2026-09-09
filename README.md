# BoltPreload — 螺栓预紧力超声测量

用超声波量螺栓的回波渡越时间，反算这颗螺栓当前被拧到多紧。设备侧接厂家 FxSDK 1.6（C# 源码编入，`bhls1.6/sdk16`）。

产品线只有两个软件（2026-08-28 定型）。此前的在线监测平台（ASP.NET API + PostgreSQL + Vue 控制台）与 Avalonia 工位软件试验线已整体废弃并从仓库移除。

## 两个软件

### 1. FxMonitor（现场桌面软件，运维/安装用）

`src/FxMonitor` — WinForms 程序：连上设备看实时波形/夹紧力/声时差，加载 `.ucf` 标定文件（exe 旁有就自动加载）；
「参数下发…」按通道段批量写可在线改的参数（探头频率/回波窗/回波宽度/增益/脉冲组），写完逐项读回核对。

**没有加载标定文件就不显示夹紧力**（只显示声时差）——软件里没有任何"默认斜率"，没有出处的数不上界面。
置零、参数下发、加载标定文件都记在 exe 旁的 `操作记录.log`。

```powershell
dotnet run --project src/FxMonitor              # 或 FxMonitor.exe [ip:port] [--auto] [--demo]
dotnet publish src/FxMonitor -c Release         # 自包含单文件 exe，拷走就能跑
```

### 2. BoltPreload.Monitor（业主网页大屏）

`src/BoltPreload.Monitor` — 自托管只读监视器：本机 SDK 直连设备，浏览器（局域网任意电脑/手机）开网页就能看，无需登录。历史数据落本地 SQLite（exe 旁 `data/history.db`），标定用 exe 旁的 `.ucf` 文件。

**基线口径**（`baseline`）——决定 `k×Δt` 这个数叫什么，必须和现场置零的方式一致：

- `"anchor"` **在役锚定**（默认）：螺栓已经拧紧在法兰上才置零。`k×Δt` 是**较锚定时刻的变化量**（`dkn`，负 = 松了）。绝对预紧力只能按"锚定时 = 额定值"估算：`kn = ratedKn + k×Δt`，页面标"估算"；没配额定值就只显示变化量、不判偏低/松动。
- `"free"` **自由状态置零**：拧紧前记基线，`kn = k×Δt + b` 就是绝对预紧力。安装期或实验室才做得到。

数据口径：

- **没有 `.ucf` 就不算预紧力**：历史里 `kn`/`dkn` 为空、状态记"未标定"，页面只显示声时差并提示放标定文件。声时差 `dt` 永远原样保存，任何时候都能用新标定重算。
- 标定文件是厂家软件做载荷标定得到的，**对应一种螺栓规格**；哪个法兰用哪份写在 `flanges[].ucf`，由 `bind.flangeIndex` 选中生效。
- 每次启动生效的标定（文件名、k、b、点数）和口径记入 `cal_log` 表，历史里每个力值都能对回当时的系数与口径。
- 采样每 5 秒一条保留 90 天；事件（状态变化/报警）保留 2 年。写入 `synchronous=FULL`，断电不丢已提交的数据。
- 落盘走独立写线程，页面查询各开连接；队列丢帧和写入失败会计数并在页面底部显示，不静默。

```powershell
dotnet run --project src/BoltPreload.Monitor    # 默认 http://0.0.0.0:5080
```

**注意：设备只允许一路 TCP 连接** —— FxMonitor 和大屏不能同时连同一台设备，现场调参先停大屏。

页面配置在 `wwwroot/farm.json`，改完刷新页面即生效（`device` / `channels` / `thresholds` 由服务端启动时读取，改这三项要重启）：

| 字段 | 含义 |
|---|---|
| `device` | 设备 `ip:port`，服务端自动连接、断线重连 |
| `baseline` | 基线口径，`"anchor"`（在役锚定，默认）或 `"free"`（自由状态置零），见下文"基线口径" |
| `thresholds.ratedKn / warnRatio / alarmRatio` | 全局额定预紧力与偏低/松动判定比例；法兰有自己的 `ratedKn` 时以法兰的为准 |
| `bind.turbine / bind.flangeIndex` | 设备装在哪台机组；`flangeIndex` 是老写法里顶层 `channels` 归属的法兰，也是三维图镜头聚焦的法兰 |
| `flanges[].channels` | **该法兰上实际接了探头、置过零的通道**——这就是接线表。一台 48 路设备可以跨多个法兰（顶段 CH1–8、中段 CH9–16……），各法兰各写各的；服务端只轮询这些通道 |
| `flanges[].plannedChannels` | 该法兰的规划探头位（含未接的），页面按它固定显示位子，没接的标"待接入" |
| `flanges[].channelBolts` | `{ "通道": 螺栓号 }`，每个探头实际装在哪颗螺栓上；没配的从 #1 起均匀占位 |
| `flanges[].ucf` | 该法兰螺栓规格对应的标定文件名（程序目录下）。不同法兰螺栓规格不同，各配各的；没写的回落到程序目录里最新的 `.ucf` |
| `flanges[].ratedKn` | 该法兰的额定预紧力 kN，覆盖全局 `thresholds.ratedKn` |
| `flanges[].boltCount` | 该法兰螺栓总数，全部画出 |
| `flanges[].kind` | `"root"` 表示叶根（画成层合板筒 + 变桨轴承），不写按塔筒钢法兰画 |
| `flanges[].plannedChannels` | 该法兰自己的规划探头位，覆盖全局 `plannedChannels`（叶根法兰预设 12 位）；写了的法兰在机组图上也会画出这些位子 |
| `flanges[].numbering` | `{ topBolt, clockwise }`：图纸上正上方那颗的编号、编号方向，用来和图纸对号 |
| `flanges[].marks` | 可选，`[{ at: 螺栓号, label }]`，在环外标基准位置 |
| `guardChannels` | 巡检位：只轮询、只判有没有探头，抓插到规划外通道的探头 |

老写法（顶层 `channels` / `plannedChannels`）仍然认，归到 `bind.flangeIndex` 那个法兰。一路通道只能属于一个法兰，重复写时按先出现的算并在启动日志里警告。

**接线怎么核对**：探头没有身份，哪颗螺栓接了几号通道只有装的人知道，`flanges[].channels` + `channelBolts` 就是接线表。装完验证：把某颗螺栓的探头脱耦或拔下，页面上对应通道的卡片、法兰图上的牌子会白框闪 20 秒，提示条写"接线核对：CHx（xx法兰）3 秒前探头信号消失"——亮的是几号，那颗螺栓就是几号。有探头信号却不在任何法兰接线表里的通道，提示条会点名"未分配"。

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
