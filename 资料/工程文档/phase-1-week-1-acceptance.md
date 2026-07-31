# Phase 1 第 1 周 — 自验收清单

按方案 §11 阶段节奏，第 1 周关键里程碑：**模拟器产生假 ChannelResult → 通过 IFxConnector → 落到 PostgreSQL，能用 SQL 查到**。

这份清单帮你跑完一遍验证里程碑达成。

## 跑通前置

```bash
cd /Users/kong/bhlssdk

# 1. 你的 git 身份
git config user.name "你的姓名"
git config user.email "你的邮箱"

# 2. 提交（脚手架已经 staged 了 77 个文件）
git commit -m "chore(phase-1): 初始化解决方案脚手架

- .NET 8 + ASP.NET Core 项目结构（10 个 .csproj）
- 厂家 C# SDK 1.4 引入到 external/FxSDK（升 net6→net8）
- IFxConnector 抽象层 + FxSdkConnector + SimulatedFxConnector
- IngestionService + IMeasurementSink + PostgresMeasurementSink
- IPreloadCalculator + SimplifiedCalculator（模型 A）
- Docker Compose（PG 16 + TimescaleDB 2.17）
- 数据库 schema + 种子数据（按方案附录 D）
- GitHub Actions CI + setup.sh 一键脚本
- 完整文档（plan、getting-started、sdk-api-facts、troubleshooting）

按方案 §0 商用底线 6 条 + 附录 A 接口 + 附录 D schema 实施。"
```

## 自检清单

跑 `bash setup.sh` 然后 `cd src/BoltPreload.Api && dotnet run`，等 30 秒，按下面顺序验证。

### A. 环境

- [ ] `.NET 8 SDK` 装了，`dotnet --version` 输出 `8.0.xxx`
- [ ] `Docker Desktop` 启动了，`docker ps` 能跑
- [ ] `bash setup.sh` 跑通无报错

### B. 数据库

- [ ] `docker ps` 看到 `boltpreload-pg` 容器在跑
- [ ] 跑 `docker compose -f deploy/docker-compose.yml exec postgres psql -U boltpreload -d boltpreload -c "\dt"` 看到 11 张表
- [ ] 表里有种子数据：3 个 channels、3 个 bolts

```sql
-- 应该返回：
SELECT id, code FROM devices;
-- (1, DEV_LAB_001)

SELECT id, channel_id, code FROM bolts;
-- (1, 1, M16-001)
-- (2, 2, M16-002)
-- (3, 3, M16-003)
```

### C. API

- [ ] `curl http://localhost:5000/api/health` 返回 `{"status":"ok",...}`
- [ ] Swagger 打得开 <http://localhost:5000/swagger>
- [ ] API 日志（控制台）看到：

```
模拟器开始连接 device=1, channels=3, rate=5Hz
模拟器 SetWorkMode=MultiMeas channel=0
```

### D. 入库（关键里程碑）

等 30 秒后跑：

```bash
curl http://localhost:5000/api/measurements/count
```

- [ ] **返回的 count > 0**（理想 ~450，是 5Hz × 3 通道 × 30 秒）

```bash
curl http://localhost:5000/api/measurements/latest?limit=5
```

- [ ] 返回 5 条最新数据
- [ ] 每条都有 `ts / boltId / channelId / echoTimeNs / spTempC / statusCode`
- [ ] `statusCode` 全是 `0`（模拟器只产生正常数据）
- [ ] `echoTimeNs` 在 27100~27300 之间（模拟器基线值 + 噪声）
- [ ] `spTempC` 在 21~23 之间（模拟器温度 22 ± 1°C）

直接查库验证：

```bash
docker compose -f deploy/docker-compose.yml exec postgres psql -U boltpreload -d boltpreload -c "
  SELECT bolt_id, COUNT(*) AS cnt, MIN(ts) AS min_ts, MAX(ts) AS max_ts
  FROM measurements
  GROUP BY bolt_id
  ORDER BY bolt_id;"
```

- [ ] 看到 3 个 bolt 各有 ~150 条数据，最新时间在 30 秒内

### E. 7×24 稳定性（非硬要求）

让 API 持续跑 1 小时，看：

- [ ] 内存占用稳定（不超 100 MB）
- [ ] 日志没有 ERROR
- [ ] count 持续增长（5Hz × 3 通道 × 3600 秒 ≈ 54000 条）
- [ ] PG 容器没 OOM 或重启

### F. CI（自动跑）

```bash
git push  # 触发 GitHub Actions
```

- [ ] CI 跑通（编译 + 单测）

## 全部打勾 = Phase 1 第 1 周里程碑达成

可以进 Phase 1 第 2 周：接实验室真机 `192.168.0.7:23`。

Phase 1 第 2 周清单见 `docs/phase-1-week-2-checklist.md`（Phase 2 启动时写）。

## 没跑通

按 `资料/工程文档/troubleshooting.md` 排查。如果都不解决，把这个清单和具体卡在第几条贴给我。
