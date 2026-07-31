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
- 数据库 `boltpreload` 已创建，schema 已执行，种子数据已插入（项目 / 监测组 / 设备 / 3 颗螺栓）

## 启动 API

```bash
cd src/BoltPreload.Api
dotnet run
```

浏览器开 <http://localhost:5000/swagger> 看 API 文档。

要用模拟器产数据（无真机时），需在 `appsettings.Development.json` 里显式设 `Fx:Mode=Simulated` ——
主配置 `appsettings.json` 的默认值是 `Real`，且缺该键时也按 `Real` 兜底（避免现场配置丢失时静默跑仿真、
让值班人员盯着假数据）。设为 Simulated 后 `SimulatedFxConnector` 启动即产数据，5Hz × 12 通道
（通道数见 `Fx:Simulator:ChannelCount`）。

## 验证 Phase 1 里程碑

等 30 秒（让模拟器产数据），跑：

```bash
# 看入库总数（应该有几百条）
curl http://localhost:5000/api/measurements/count

# 看最新 20 条
curl http://localhost:5000/api/measurements/latest | jq

# 按螺栓查（M16-001 的 ID=1）
curl http://localhost:5000/api/measurements/by-bolt/1/latest?limit=10 | jq
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

`SimulatedFxConnector` 用的 `DeviceId=1`，需要数据库里有对应的 device + 3 个 channels + 3 个 bolts。
检查种子数据是否成功插入：

```bash
docker compose -f deploy/docker-compose.yml exec postgres \
  psql -U boltpreload -d boltpreload -c \
  "SELECT id, code, model FROM devices;"
```

应该看到 `(1, DEV_LAB_001, 3)`。

如果没有，跑 `bash setup.sh reset` 重新初始化。
