# 现场部署手册（Windows 工控机）

> 适用版本：v1.1.0。目标环境：风场/桥梁现场 Windows 工控机，**不使用 Docker**，PostgreSQL 走原生安装。
> 本手册中的每一步都在 macOS + Linux 容器上做过等价演练（空库首装、Production 冷启动、授权签发、鉴权与深链验证）；
> Windows 特有部分（NSSM 服务注册、防火墙、PG 原生安装）标注了“需在 Windows 上验证”。

---

## 0. 动手前先看这五件事

这五件事全做到才算装完：**建库并跑完全部迁移**、**注入 4 个必需配置**、**设好首个管理员密码**、
**装入签好名的 license.json**、**填对设备通讯地址**。
第 3 项漏了，现场没人登得进去；第 4 项漏了，系统只能看不能动。

---

## 1. 前置条件

| 项 | 要求 | 备注 |
|---|---|---|
| 操作系统 | Windows 10/11 或 Server 2019+ x64 | |
| .NET 运行时 | ASP.NET Core Runtime 8.0 | 也可用自包含发布免装运行时，见 §3 |
| PostgreSQL | 16 + TimescaleDB 扩展 | 迁移脚本里用到 Timescale 超表 |
| NSSM | `nssm.exe` 放到 `C:\BoltPreload\tools\` | 注册 Windows 服务用 |
| 磁盘 | 系统盘外另留数据分区 | 波形/WAL/日志会持续增长，见 §7 |

---

## 2. 构建交付包（在开发机上做）

```bash
# 1) 前端产物（必须先构建：csproj 里有硬校验，dist 为空会直接编译期报错）
cd web && npm ci && npm run build && cd ..

# 2) 后端发布（前端产物由 CopyWebDistOnPublish 目标自动打进 wwwroot）
dotnet publish src/BoltPreload.Api/BoltPreload.Api.csproj -c Release -r win-x64 --self-contained false -o out/
```

自包含（目标机不装 .NET 运行时）改用：

```bash
dotnet publish src/BoltPreload.Api/BoltPreload.Api.csproj -c Release -r win-x64 --self-contained true -o out/
```

交付包内容自检：`out/BoltPreload.Api.exe`、`out/wwwroot/index.html`、`out/wwwroot/assets/`、`out/NLog.config` 都应存在。
**`wwwroot/index.html` 缺失就是前端没打进去**，装上去打开是空白页。

反向自检同样重要：`out/appsettings.Development.json` **必须不存在**（csproj 已设 `CopyToPublishDirectory=Never`）。
该文件含开发库口令、开发 JWT 密钥，且 `AllowMissingLicenseInDevelopment=true`——一旦随包发出，现场把
`ASPNETCORE_ENVIRONMENT` 改成 `Development` 就能绕过授权校验并跑起仿真模式。发现它出现在 `out/` 就别发货。

另需一并带到现场：`deploy/sql/0*.sql`（全部迁移）、`deploy/windows/*.ps1`、签好的 `license.json`。

---

## 3. 数据库

```powershell
# 建库与角色（密码自行设定，不要用示例值）
psql -U postgres -c "CREATE ROLE boltpreload LOGIN PASSWORD '<强密码>';"
psql -U postgres -c "CREATE DATABASE boltpreload OWNER boltpreload;"

# 执行全部迁移（账本式：已应用的自动跳过，装机与升级共用同一脚本）
.\deploy\windows\apply-migrations.ps1
```

要点：

- psql 非交互执行要先设 `$env:PGPASSWORD` 或配好 `%APPDATA%\postgresql\pgpass.conf`，
  否则脚本会卡在密码提示上（备份脚本 backup-postgres.ps1 同理）。
- **只跑 `deploy/sql/0*.sql`，不要跑 `deploy/sql/dev/`** —— 那是开发种子数据，会往生产库塞演示风场和螺栓
  （`apply-migrations.ps1` 只认顶层 `0*.sql`，不会碰 `dev/`）。
- 脚本用 `schema_migrations` 账本记录已执行的迁移：每个文件整体一个事务，执行与记账同一事务提交，
  中途失败自动整体回滚，修复后重跑即从断点续跑。
- **存量库注意**（本脚本引入之前装的机器，库里有表但没有账本）：第一次跑必须带
  `-Baseline <序号>` 显式声明装机时执行到了哪个迁移（例：`-Baseline 0024`），
  脚本会把 ≤ 该序号的文件记账但不执行。不带参数时脚本会拒绝执行——早期迁移并不幂等，
  对存量库从 0001 重放会直接失败。

---

## 4. 授权（license）

系统对 `license.json` 做 RSA-SHA256 验签。**授权无效时监测照常运行、读接口正常，但所有写操作被拦（403）** ——
建档、标定、告警确认、参数下发全都不能用。所以这一步不是可选项。

### 4.1 供应商侧：生成密钥对（一次性，长期复用）

```bash
dotnet run --project tools/LicenseSigner -- keygen --out-dir ./keys
```

产出 `license-private.pem`（**供应商保管，绝不进仓库和交付包**）与 `license-public.pem`（随现场配置下发）。
私钥丢失或被覆盖会让此前签发的所有授权立即失效，工具对覆盖已有私钥的行为会直接报错拒绝。

### 4.2 取目标机机器码

在工控机上先把服务跑起来（此时授权还没装，属正常），然后任一方式取机器码：

```powershell
curl http://localhost:5000/api/license/machine-code    # 匿名可访问
# 或看启动日志里的：授权无效或缺失：...（机器码 XXXXXXXXXXXXXXXX）
```

机器码由机器名、CPU 核数、以及本机物理网卡 MAC 推出，**不随网线插拔或虚拟网卡上下线变化**
（旧版本会变，导致授权自己失效，v1.1.0 已修）。更换主板/网卡后需重新签发。

### 4.3 签发并装入

```bash
dotnet run --project tools/LicenseSigner -- sign \
  --private-key ./keys/license-private.pem \
  --machine-code <上一步拿到的机器码> \
  --licensed-to  "客户单位全称" \
  --license-id   BH-2026-0001 \
  --expires      2027-12-31 \
  --max-devices  2 \
  --max-channels 40 \
  --out license.json

# 下发前本地预检，别等到现场才发现签错
dotnet run --project tools/LicenseSigner -- verify \
  --public-key ./keys/license-public.pem --license license.json
```

把 `license.json` 放到安装目录（与 `BoltPreload.Api.exe` 同级），公钥内容填进 `License__PublicKeyPem`。

装好后自检：启动日志应出现 `授权有效 LicensedTo=... MaxDevices=... MaxChannels=...`，
且 `GET /api/license/status` 返回 `"valid": true`。

---

## 5. 必需配置清单

配置可用环境变量（推荐，服务级设置见 §6）或 `appsettings.Production.json`。
`__`（双下划线）对应配置层级里的 `:`。

| 环境变量 | 必需 | 缺失时的表现 |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | 建议显式设 `Production` | 不设时框架默认即 Production（已实测）。**绝不能设成 `Development`**：那会启用硬编码的 dev JWT 密钥并自动创建 `admin/admin123` |
| `ConnectionStrings__Postgres` | **是** | 启动即失败并提示（fail-fast，已实测） |
| `Jwt__SigningKey` | **是**，≥32 字节随机，建议 64 | 非 Development 下启动即失败并提示（已实测） |
| `BoltPreload__BootstrapAdminPassword` | **首装必需** | 空库里没有任何用户，而建账号接口要求 admin 鉴权 → 401，**没人能登录，系统装完打不开**。首个管理员建好后即可移除该变量 |
| `License__PublicKeyPem` | **是** | 授权判无效 → 全部写操作 403（见 §4） |
| `Fx__Mode` | 建议显式设 `Real` | 未设时按 `Real` 兜底并告警；若在 Production 环境显式配成 `Simulated`，服务直接拒绝启动（仿真数据不允许出现在现场，这是硬防线不是告警） |
| `ASPNETCORE_URLS` | 建议 | 默认监听 5000。只给本机用则设 `http://127.0.0.1:5000`，给局域网用再放开并配防火墙 |
| `Ingestion__WaveSampling__Enabled` | 视需要 | 默认 `false`。真机 MultiMeas 模式不随测量带波形，**不开启则波形诊断页和 A-Scan 没有数据源** |

设备通讯地址二选一：

- 推荐：往 `devices` 表写记录，`comm_params` 填实际地址（如 `192.168.1.50:23`），支持多设备。
- 兜底：`devices` 表为空时回退到 `Fx:Real:CommParams`，其默认值是**实验室地址 `192.168.0.7:23`**，
  现场必须改掉，否则会连到不存在的设备（表现为反复重连、无数据）。

### 关于 HTTPS

当前进程只提供 HTTP，未启用 HTTPS 重定向/HSTS。JWT 以 Bearer 头传输、存在浏览器 localStorage，
**在局域网上是明文可截获的**。交付时必须二选一并写进验收文档：

1. 前置反向代理（IIS / nginx）终结 TLS，工控机上的服务只监听 `127.0.0.1`；
2. 或明确接受该残余风险，前提是监测网段物理隔离、不与办公网互通。

---

## 6. 注册为 Windows 服务

> 需在 Windows 上验证。

```powershell
# 把 out/ 的内容复制到 C:\BoltPreload\，nssm.exe 放到 C:\BoltPreload\tools\
.\deploy\windows\install-service.ps1 `
    -ConnectionString "Host=localhost;Port=5432;Database=boltpreload;Username=boltpreload;Password=<强密码>" `
    -JwtSigningKey    "<64 字节随机串>" `
    -LicensePublicKeyPemPath "C:\BoltPreload\license-public.pem" `
    -BootstrapAdminPassword  "<首个管理员密码>" `
    -Urls "http://127.0.0.1:5000"
```

该脚本会把上述配置写成服务级环境变量（含固定 `ASPNETCORE_ENVIRONMENT=Production`）、
配置日志轮转与失败重启、开放防火墙端口，并在启动后探测健康检查。

首个管理员建好后，建议重新执行一次不带 `-BootstrapAdminPassword` 的注册，把该变量从服务配置里移除。

---

## 7. 数据目录与保留策略

| 内容 | 默认位置 | 增长与清理 |
|---|---|---|
| 波形文件 | `data/waves`（相对安装目录） | 默认保留 30 天，`WaveRetentionService` 每 24h 清理 |
| 测量 WAL | `data/wal` | 入库失败时落盘兜底，上限 50000 条，超出丢最旧并告警 |
| 应用日志 | `logs/boltpreload-*.log` | 按天滚动，保留 90 天 |
| SDK 日志 | `logs/sdk-*.log` | 10MB 滚动，保留 10 个 |
| 数据库 | PG 数据目录 | 测量表为 Timescale 超表，按合同约定保留期另行配置 |

建议把 `WaveStore:RootPath`、`MeasurementWal:RootPath` 指到独立数据分区，
避免系统盘写满导致静默丢数（`/api/health/ready` 会报告 `freeDiskGb`，可接入监控告警）。

---

## 8. 装完自检清单

按顺序全部通过才算装好：

```powershell
# 1) 进程活着
curl http://localhost:5000/api/health
#    期望：{"status":"ok",...,"version":"1.1.0.0"}

# 2) 依赖与后台服务都正常
curl http://localhost:5000/api/health/ready
#    期望：status=ready、db=up、deadServices=[]、backgroundServices 里
#          ingestion-watchdog / signal-health / channel-presence / wal-replay 的 stale 均为 false
#    若返回 503 且 deadServices 非空 → 对应后台服务已停摆，查日志，不要当成"能用"

# 3) 界面能打开，且刷新任意页面不白屏
#    浏览器直接访问 http://<主机>:5000/alarms 和 /board，应正常渲染（不是空白页）

# 4) 授权有效
curl http://localhost:5000/api/license/status
#    期望：{"found":true,"valid":true,...}

# 5) 首个管理员能登录，且能完成一次写操作
#    登录后新建一个 reviewer 账号 —— 成功说明授权闸门已放行写操作

# 6) 未登录访问业务接口应为 401
curl -o /dev/null -w "%{http_code}" http://localhost:5000/api/bolts     # 期望 401

# 7) Swagger 在生产不可访问
curl -o /dev/null -w "%{http_code}" http://localhost:5000/swagger/index.html   # 期望 404

# 8) 设备已连上、有数据在进
curl http://localhost:5000/api/health/ready
#    devices.connected 应等于配置的设备数；maxIdleSeconds 应持续保持在小值
```

第 3、4 项是最容易漏的：前者曾因缺 SPA 回退导致"除首页外全是白页"，后者漏了就是"能看不能用"。

---

## 9. 升级与回滚

升级：停服务 → 备份数据库（`deploy/windows/backup-postgres.ps1`）→ 替换程序目录（保留 `license.json`、`data/`、`logs/`）
→ 重跑 `deploy\windows\apply-migrations.ps1`（账本自动跳过已应用的迁移，只执行新增部分；
存量库第一次接入账本需带 `-Baseline`，见 §3）→ 起服务 → 走 §8 自检。

回滚：停服务 → 恢复上一版程序目录 → 起服务。
**注意迁移不提供向下脚本**，若新版本引入了破坏性 schema 变更，回滚需从备份恢复数据库。

诊断信息收集：`deploy/windows/collect-diagnostics.ps1`。
注意该脚本会打包 `appsettings.json`，**若把密钥写进了配置文件而非环境变量，导出的包里会含密钥**，外发前先检查。

---

## 10. 仍待完成

- **Inno Setup 一键安装包**（`deploy/installer/` 目前为空）：把 PG + TimescaleDB 二进制、.NET 运行时、
  程序目录、迁移执行、服务注册、防火墙规则打成单个 `.exe`。当前按本手册手工部署可以完成交付，
  但每台机器的步骤较多、依赖人工不出错。该安装包需要在 Windows 上构建与验证。
- **HTTPS 方案定稿**：见 §5 末尾，需按客户网络环境二选一并写入验收文档。
- **真机回归**：`资料/工程文档/delivery-master-plan.md` §1/§3 的实测项需在实验室或现场真机各走一轮。
