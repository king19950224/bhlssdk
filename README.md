# BoltPreload — 螺栓预紧力在线监测系统

用超声波量螺栓的回波渡越时间，反算这颗螺栓当前被拧到多紧。7×24 连续盯着，松了报警，另外能看历史趋势、出报告。设备侧接厂家 FxSDK（C# 1.4）。

## 文档

全部在 `资料/工程文档/`：

- 开发文档：`资料/工程文档/development-guide.md`，新人先看这份
- 现场部署手册：`资料/工程文档/deployment-guide.md`
- 标定现场操作规程：`资料/工程文档/calibration-field-guide.md`
- 运维排障：`资料/工程文档/troubleshooting.md`
- 完整设计方案：`资料/工程文档/plan.md`

## 开发环境要求

- macOS（开发机）或 Linux/Windows
- .NET 8 SDK（[下载](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)）
- Docker Desktop（[下载](https://www.docker.com/products/docker-desktop/)），用于本地跑 PostgreSQL + TimescaleDB
- Node 20+（前端开发）
- Git

部署环境是 Windows 工控机，**不能用 Docker**，PG 走原生安装。开发机用 Docker 只是为了不污染原生 macOS。

现场部署目前按 `资料/工程文档/deployment-guide.md` 手工执行（建库跑迁移 → 注入配置 → 签发授权 → NSSM 注册服务 → 自检）。
Windows Inno Setup 脚本已入库（`deploy/installer/installer.iss` + README）；本机构建仍需安装 Inno Setup 并做工控机彩排。

## 快速开始

### 启动本地依赖

```bash
docker compose up -d postgres
```

第一次启动会自动创建数据库并按序执行 `deploy/sql/0*.sql` 全部迁移（含 dev 种子），
同时写 `schema_migrations` 账本——语义与生产升级脚本 `deploy/windows/apply-migrations.ps1` 一致。

### 跑后端

```bash
cd src/BoltPreload.Api
dotnet run
```

默认端口 5000，UI 在 http://localhost:5000（当前进程只提供 HTTP，TLS 由现场反向代理终结，见部署手册 §5）

开发环境会自动创建 `admin` / `admin123`（仅 Development）。生产环境不会自动建账号，
首装必须提供 `BoltPreload__BootstrapAdminPassword`，否则没人能登录 —— 详见部署手册 §5。

### 跑测试

```bash
dotnet test
```

**数据库必须先起来**（见上面的“启动本地依赖”）。连不上库时约 **171** 个 Postgres 集成测试会自动跳过，
`dotnet test` 照样报“已通过”，只在末尾一行写“已跳过: 171”—— 很容易被当成全测过了。
跳过的恰好是覆盖面最广的一批：权限守卫、建档绑定、报警管线、入库热路径、EF 模型与
手写 schema 的一致性。CI 已对跳过数=0 设门禁。合入前请确认那行是“已跳过: 0”。

### 关停依赖

```bash
docker compose down
```

数据保留在 `.docker-data/` 下，下次启动还能用。完全清空：`docker compose down -v`。

## 项目结构

```
BoltPreload/
├── src/
│   ├── BoltPreload.Domain/            领域模型
│   ├── BoltPreload.Application/       用例
│   ├── BoltPreload.Infrastructure/    EF Core + Timescale
│   ├── BoltPreload.Fx/                FxSDK 包装
│   ├── BoltPreload.Algorithms/        TOF / 应力 / 温度补偿
│   ├── BoltPreload.Calibration/       标定算法
│   ├── BoltPreload.Api/               ASP.NET Core
│   └── BoltPreload.Tests/             单测 + 集成测试
├── web/                                Vue 3 前端
├── deploy/
│   ├── docker-compose.yml              本地开发用
│   ├── sql/                            数据库迁移脚本（生产只跑 0*.sql，不含 dev/）
│   ├── windows/                        工控机部署脚本（NSSM 服务注册、备份、诊断收集）
│   └── installer/                      Windows Inno Setup 脚本（需本机装 Inno 构建）
├── tools/
│   ├── FxDeviceSimulator/              设备模拟器
│   ├── DeviceSmokeTest/                真机连通性烟测
│   └── LicenseSigner/                  授权签发（供应商侧，不随交付包发出）
└── 资料/工程文档/                       工程文档（开发/部署/标定/排障手册与设计方案）
```

## 业务约束

**这是商用关键基础设施监测系统**，监测对象（风电塔筒、桥梁、压力容器法兰等）失效后果可能是人员伤亡。所有设计、开发、部署严守 6 条商用底线（见方案 §0），违反任意一条都是给未来的灾难性事故埋雷。

任何对算法、阈值、报警规则的修改都要能追溯到国标、SDK 源码或现场标定数据。"我觉得" 不能进系统。

## License

待定（视客户合同）。
