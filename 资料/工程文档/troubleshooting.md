# 调试指南

按"跑通三步"的顺序列出常见问题。每条都有"症状 → 原因 → 修复"。

如果这里没覆盖你遇到的问题，把完整错误信息 + `dotnet --info` + `docker --version` 输出给我。

---

## 授权过期 / 写操作全部 403

**症状**：页面能看、数据在采，但建档/标定/告警确认等一切写操作报 403；
侧栏"授权异常"红灯；`GET /api/license/status` 返回 `valid: false`。

**原因**：授权到期、license.json 缺失/被改、或换过网卡导致机器码不匹配。
这是设计内行为——**监测与读取永不因授权停**（安全数据不作人质），只拦写操作。

**修复**：
1. `GET /api/license/machine-code`（匿名可访问）取当前机器码；
2. 把机器码发给供应商，用 `tools/LicenseSigner sign` 签发新 license.json；
3. 替换安装目录下的 license.json，`Restart-Service BoltPreload.Api`；
4. `GET /api/license/status` 确认 `valid: true`。
授权流程详见 deployment-guide §4；到期前 30 天开始联系续签，别等 403 了才动。

---

## 真实设备问题

### 有温度和波形，但 `EchoTime` 一直是 NULL

**症状**：

- API 能连接 `192.168.0.7:23`
- `measurements` 持续入库
- `sp_temp_c` 有真实温度
- `LEchoWave` 能读到
- 但 `echo_time_ns` 全是 NULL，`status_code` 仍可能是 0

**根因**：

C# SDK 1.4 的 `EchoTime` 是 SDK 根据波形在上位机计算出来的，不是设备直接给的最终结果。计算入口在：

- `external/FxSDK/Codec/MultiChIotProtocolHandler.cs`
- `ProcessResult(session, chResult, rawData)`

它必须满足：

```csharp
channelConfig is { ProbeFreq: > 0 }
```

也就是说，SDK 的 `SessionContext` 里必须有对应 `(address, channel)` 的 `ChannelConfig`。如果只存了 `DeviceConfig`，没有存 `ChannelConfig`，SDK 不会计算 `EchoTime`。

**修复**：

连接成功后必须按顺序：

1. `ConfigureDeviceAsync(..., sendCommand:false)`
2. `ConfigureChannelAsync(..., sendCommand:false)`，把通道配置写到 SDK 本地 `SessionContext`
3. `SetWorkModeAsync(MultiMeas)`

注意：`sendCommand:false` 只写 SDK 本地缓存，不会改设备参数。

**验证**：

```bash
curl http://localhost:5000/api/devices/1/diagnostic/real-health?channel=9
```

重点看：

- `measurement.echoRatePct` 应接近 `100`
- `sdkSession.hasChannelConfig` 应为 `true`
- `sdkSession.sessionConfig.probeFreqMhz` 应大于 0

---

## 第 1 步：环境准备问题

### `dotnet: command not found`

**原因**：.NET 8 SDK 没装，或装了但 PATH 没刷新。

**修复**：

```bash
# 1. 装：https://dotnet.microsoft.com/en-us/download/dotnet/8.0
#    选 macOS Arm64（M 芯片）或 macOS x64（Intel）的 .pkg
# 2. 装完关闭 Terminal 重新开（PATH 才会刷新）
dotnet --version
# 期望：8.0.xxx
```

如果 `dotnet --version` 还是找不到，手动加 PATH：

```bash
echo 'export PATH=$PATH:/usr/local/share/dotnet' >> ~/.zshrc
source ~/.zshrc
```

### `docker: command not found`

**原因**：Docker Desktop 没装，或者没启动。

**修复**：

1. 装：<https://www.docker.com/products/docker-desktop/>
2. 启动 Docker.app，等任务栏小鲸鱼图标变绿（约 30 秒）
3. 验证：`docker --version`

### Docker Desktop 启动后 `docker ps` 报错 "Cannot connect to the Docker daemon"

**原因**：Docker Desktop GUI 在启动中，daemon 还没就绪。

**修复**：等 10~30 秒，再试一次。

---

## 第 2 步：数据库启动问题

### `bash setup.sh up` 失败，PG 容器起不来

**症状**：`docker compose -f deploy/docker-compose.yml up -d postgres` 后跑健康检查失败。

**排查**：

```bash
docker compose -f deploy/docker-compose.yml logs postgres
```

**常见原因 1**：端口冲突。本机已经有别的 PG 占了 5432。

```bash
# 看谁占了 5432
lsof -i :5432
# 杀掉占用进程，或者改 deploy/docker-compose.yml 的端口（如改成 5433）
```

如果改了端口，对应改 `src/BoltPreload.Api/appsettings.json` 里的 `Port=5432` 也要改。

**常见原因 2**：旧的 `.docker-data/postgres` 数据残留，跟新版本不兼容。

```bash
bash setup.sh reset   # 完全清空数据
bash setup.sh up
```

**常见原因 3**：volumes 权限问题（macOS 罕见，Linux 常见）。

```bash
# 检查 .docker-data/postgres 权限
ls -la .docker-data/postgres
# 如果属主不是当前用户，重置
sudo rm -rf .docker-data/postgres
bash setup.sh up
```

### `psql` 连上后报 "relation 'measurements' does not exist"

**原因**：初始化 SQL 没执行（一般是因为 volumes 残留，PG 觉得"数据库已经初始化过了"就跳过 `docker-entrypoint-initdb.d/`）。

**修复**：

```bash
bash setup.sh reset
bash setup.sh up
```

完全清空再重建。

### TimescaleDB 扩展没启用

**症状**：跑 `\d measurements` 看到表存在但不是 hypertable，或者 `CREATE EXTENSION timescaledb` 报错。

**排查**：

```bash
docker compose -f deploy/docker-compose.yml exec postgres \
  psql -U boltpreload -d boltpreload -c "\dx"
# 应该看到 timescaledb 已装
```

如果没有，检查 `deploy/docker-compose.yml` 的 `command` 里 `shared_preload_libraries=timescaledb` 是不是被改掉了。

---

## 第 3 步：编译 / 启动 API 问题

### `dotnet restore` 报错 NU1101 "Unable to find package..."

**原因**：NuGet 源访问问题（国内网络）。

**修复**：

```bash
# 看当前源
dotnet nuget list source

# 加国内镜像（华为云示例）
dotnet nuget add source https://repo.huaweicloud.com/repository/nuget/v3/index.json \
  --name HuaweiCloud

# 或清华
dotnet nuget add source https://nuget.cdn.azure.cn/v3/index.json \
  --name AzureCN

dotnet restore BoltPreload.sln
```

### `dotnet build` 编译错误：external/FxSDK 的警告被当错误

**原因**：根 `Directory.Build.props` 设了 `TreatWarningsAsErrors=true`，应该被 `external/FxSDK/Directory.Build.props` 覆盖成 false，但有时被忽略。

**修复**：检查 `external/FxSDK/Directory.Build.props` 内容是否完整。如果还是失败，临时改根的 `TreatWarningsAsErrors=false` 跑过去，然后定位具体警告。

### `dotnet run` 启动 API 报 "could not connect to server"

**症状**：API 启动时 Npgsql 连接失败。

**排查**：

```bash
# 1. 数据库容器在跑吗
docker ps | grep boltpreload-pg
# 2. 本机能连吗
docker compose -f deploy/docker-compose.yml exec postgres \
  pg_isready -U boltpreload -d boltpreload
# 3. 用 psql 直连验证
docker compose -f deploy/docker-compose.yml exec postgres \
  psql -U boltpreload -d boltpreload -c "SELECT version();"
```

如果上面都 OK，但 API 还是连不上，看 `src/BoltPreload.Api/appsettings.json` 里的 `ConnectionStrings:Postgres` 是不是对的。

### API 启动后日志一直刷 "无法解析通道 device=1 channel=X，丢弃数据"

**原因**：种子数据没插入成功，或者 SimulatorOptions 的 DeviceId 跟数据库里的 device.id 对不上。

**排查**：

```bash
docker compose -f deploy/docker-compose.yml exec postgres \
  psql -U boltpreload -d boltpreload -c "SELECT id, code, model FROM devices;"
```

应该看到 `(1, DEV_LAB_001, 3)`。

```bash
docker compose -f deploy/docker-compose.yml exec postgres \
  psql -U boltpreload -d boltpreload -c "SELECT id, channel_id, code FROM bolts;"
```

种子不预插螺栓：模拟器跑起来约 30 秒后，自动建档会按在位通道生成螺栓（code 形如 通道位号）。
一直是空的话先确认 API 在跑、`Fx:Mode=Simulated` 已设；设备行都没有就跑 `bash setup.sh reset` 重新初始化。

### API 启动报 "PartitioningStrategy not supported"

**原因**：TimescaleDB 创建 hypertable 时跟某些 EF Core 版本不兼容。

**修复**：我们的方案不用 EF Core 操作时序表，只用业务表。如果 EF Core 启动时去扫描 measurements 表报错，检查 `BoltPreloadDbContext` 里是不是误加了 `DbSet<Measurement>`（应该没有，确认下）。

### API 跑起来了但 `/api/measurements/count` 一直是 0

**症状**：API 健康，但数据不入库。

**排查 1**：模拟器在跑吗？看 API 日志，应该有：

```
模拟器开始连接 device=1, channels=12, rate=5Hz
```

（通道数跟 `Fx:Simulator:ChannelCount` 配置走，当前默认 12。）

如果没有，IFxConnector 的注册有问题。

**排查 2**：数据进了 anomalies？

```bash
docker compose -f deploy/docker-compose.yml exec postgres \
  psql -U boltpreload -d boltpreload -c "SELECT COUNT(*) FROM measurement_anomalies;"
```

如果 anomalies 有数据但 measurements 是空的，说明 status_code < 0。检查 API 日志看具体原因。

**排查 3**：直接看 PG 日志，找 INSERT 错误：

```bash
docker compose -f deploy/docker-compose.yml logs postgres | grep -i error | tail -20
```

---

## 第 4 步：API 查询问题

### `/api/measurements/latest` 返回 500

**原因**：通常是 NpgsqlDataSource 没注入，或者 SQL 错误。

**排查**：

```bash
# API 日志会有详细堆栈
tail -f logs/boltpreload-$(date +%Y%m%d).log
```

### 数据有但 API 返回空数组

**原因**：API 用的是 `localhost:5432`，但 Docker 可能把端口映射到了别的地方。

**修复**：

```bash
docker compose -f deploy/docker-compose.yml port postgres 5432
# 期望：0.0.0.0:5432 或 127.0.0.1:5432
```

如果是别的端口，对应改 appsettings.json。

---

## 通用排查命令

```bash
# 1. 看所有服务状态
docker ps

# 2. 看数据库实时日志
docker compose -f deploy/docker-compose.yml logs -f postgres

# 3. 看 API 实时日志（启动 API 的那个终端窗口）
# 或看文件
tail -f src/BoltPreload.Api/logs/boltpreload-*.log

# 4. 进 DB 直接看数据
docker compose -f deploy/docker-compose.yml exec postgres \
  psql -U boltpreload -d boltpreload

# 在 psql 里：
\d                      -- 列所有表
\d+ measurements        -- 看 measurements 的结构和分区
SELECT COUNT(*) FROM measurements;
SELECT * FROM measurements ORDER BY ts DESC LIMIT 5;
SELECT * FROM measurement_anomalies ORDER BY ts DESC LIMIT 5;

# 5. 一键全清重来
bash setup.sh reset
bash setup.sh
cd src/BoltPreload.Api && dotnet run
```

---

## 提交 bug 时附上的信息

如果上面都不解决，把这些贴给我：

```bash
{
  echo "=== 环境 ==="
  dotnet --info
  docker --version
  echo ""
  echo "=== 容器状态 ==="
  docker ps
  echo ""
  echo "=== 数据库连通 ==="
  docker compose -f deploy/docker-compose.yml exec postgres pg_isready
  echo ""
  echo "=== 表存在性 ==="
  docker compose -f deploy/docker-compose.yml exec postgres \
    psql -U boltpreload -d boltpreload -c "\dt"
  echo ""
  echo "=== 种子数据 ==="
  docker compose -f deploy/docker-compose.yml exec postgres \
    psql -U boltpreload -d boltpreload -c "SELECT id, code FROM devices;"
  docker compose -f deploy/docker-compose.yml exec postgres \
    psql -U boltpreload -d boltpreload -c "SELECT id, code FROM bolts;"
  echo ""
  echo "=== 最近 API 日志 ==="
  tail -50 src/BoltPreload.Api/logs/boltpreload-*.log 2>/dev/null || echo "无日志文件"
} > /tmp/boltpreload-debug.txt
cat /tmp/boltpreload-debug.txt
```
