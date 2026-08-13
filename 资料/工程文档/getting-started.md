# 开发上手指引

## 前置工具

| 工具 | 必须 | 下载 |
|---|---|---|
| .NET 8 SDK | 是 | <https://dotnet.microsoft.com/en-us/download/dotnet/8.0> |
| Docker Desktop | 是 | <https://www.docker.com/products/docker-desktop/> |
| Git | 是 | macOS 自带 / Linux apt / Windows git-scm.com |
| IDE | 推荐 | Rider / Visual Studio / VS Code + C# Dev Kit |

不需要装 Homebrew、PostgreSQL、Node（除非做前端）。

## 一键初始化

```bash
bash setup.sh
```

跑完会有：

- `.docker-data/postgres/`：本地 PG 数据
- `src/**/bin`, `src/**/obj`：编译产物
- 数据库 `boltpreload` 已创建，schema 已执行，种子数据已插入（项目 / 监测组 / 设备 / 48 通道目录；不预插螺栓，螺栓由探头在位自动建档产生）

## 启动 API

```bash
cd src/BoltPreload.Api
dotnet run
```

浏览器开 <http://localhost:5000/swagger> 看 API 文档。

设备接入只有真机一条路。曾经有过一个进程内模拟器（`Fx:Mode=Simulated`），已整体移除：
它出的假预紧力物理上自洽、界面上跟真数据一模一样，对安全监测系统来说这条路径本身就是风险。
配置里若还残留 `Fx:Mode=Simulated`，进程会直接拒绝启动并告诉你删掉它。

**本机没有真机时**：`devices` 表照常建档、界面照常可点，但不会有实时数据流。
要看有数据的界面，先跑 `deploy/sql/dev/` 下的种子脚本铺一批静态样本。
要验证 SDK 解码链路本身，用 `src/BoltPreload.Tests/Fx/RawFrameReplay.cs` 重放真机录波帧。

接真机把 `appsettings.Development.json` 的 `Fx:Real:CommParams` 改成设备地址（如 `192.168.0.7:23`），
或直接在 `devices` 表建行——表里有启用设备时以表为准，`Fx:Real:*` 只是空表时的单设备兜底。

## 验证数据链路

有真机或已铺种子数据后，跑：

```bash
# 看入库总数（应该有几百条）
curl http://localhost:5000/api/measurements/count

# 看最新 20 条
curl http://localhost:5000/api/measurements/latest | jq

# 按螺栓查（螺栓是自动建档出来的，boltId 从上一条 latest 的返回里拿）
curl http://localhost:5000/api/measurements/by-bolt/<boltId>/latest?limit=10 | jq
```

直接连数据库看：

```bash
docker compose -f deploy/docker-compose.yml exec postgres \
  psql -U boltpreload -d boltpreload -c \
  "SELECT bolt_id, ts, echo_time_ns, sp_temp_c FROM measurements ORDER BY ts DESC LIMIT 10;"
```

## 常用命令

```bash
bash setup.sh up      # 只启动数据库
bash setup.sh down    # 停数据库
bash setup.sh reset   # 重置数据库（清空）
bash setup.sh test    # 跑单元测试
dotnet test BoltPreload.sln  # 直接跑测试
```

## 排错

### dotnet restore 失败

可能是 NuGet 源问题。`dotnet nuget list source` 看下，必要时改国内镜像（清华、华为云）。

### Docker 启动后 PG 端口冲突

可能 macOS 本机已经装了 PG 占了 5432。要么停掉本机的，要么改 `deploy/docker-compose.yml` 的端口（如改 5433）。

### "无法解析通道，丢弃数据" 日志

测量上来了但落不进库，通常是设备/通道目录对不上：数据库里得有对应的 device 行与通道目录
（种子建 48 通道，螺栓不预插、由自动建档产生），`devices.id` 要与实际采集的设备一致。
检查种子数据是否成功插入：

```bash
docker compose -f deploy/docker-compose.yml exec postgres \
  psql -U boltpreload -d boltpreload -c \
  "SELECT id, code, model FROM devices;"
```

应该看到 `(1, DEV_LAB_001, 3)`。

如果没有，跑 `bash setup.sh reset` 重新初始化。
