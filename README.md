# BoltPreload — 螺栓预紧力在线监测系统

商用关键基础设施监测系统。基于厂家 FxSDK（C# 1.4）对接超声波螺栓预紧力测量设备，提供 7×24 实时监测、报警、历史趋势、报告等能力。

## 文档

- 开发文档：`docs/development-guide.md`，新人先看这份。用什么做的、厂家资料、怎么通讯、接口有哪些、环境一步步怎么搭，都在里面
- 完整设计方案：`docs/plan.md`（从 `.cursor/plans/` 拷贝过来，含商用底线、算法、合规、附录 A~H）

## 开发环境要求

- macOS（开发机）或 Linux/Windows
- .NET 8 SDK（[下载](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)）
- Docker Desktop（[下载](https://www.docker.com/products/docker-desktop/)），用于本地跑 PostgreSQL + TimescaleDB
- Node 20+（前端开发）
- Git

部署环境是 Windows 工控机，**不能用 Docker**，PG 用 Inno Setup 打的原生二进制（见方案附录 H）。开发机用 Docker 只是为了不污染原生 macOS。

## 快速开始

### 启动本地依赖

```bash
docker compose up -d postgres
```

第一次启动会自动创建数据库 + 执行 `deploy/sql/0001_initial_schema.sql`。

### 跑后端

```bash
cd src/BoltPreload.Api
dotnet run
```

默认端口 5000，UI 在 https://localhost:5000

### 跑测试

```bash
dotnet test
```

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
│   ├── sql/                            数据库迁移脚本
│   └── installer/                      Windows Inno Setup
├── tools/
│   └── FxDeviceSimulator/              设备模拟器
└── docs/                               架构 / 标定 SOP / 验收文档
```

## 业务约束

**这是商用关键基础设施监测系统**，监测对象（风电塔筒、桥梁、压力容器法兰等）失效后果可能是人员伤亡。所有设计、开发、部署严守 6 条商用底线（见方案 §0），违反任意一条都是给未来的灾难性事故埋雷。

任何对算法、阈值、报警规则的修改都要能追溯到国标、SDK 源码或现场标定数据。"我觉得" 不能进系统。

## License

待定（视客户合同）。
