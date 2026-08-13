# 真机通道标定 / 重标定现场指南

> 目的：把"真机已接入、在测原始声时"变成"算得出**可信预紧力**"。
> 适用：已接入的现场设备（设备接入只有真机一条路）。所有标定写操作要求 **reviewer+** 权限（登录账号角色为 `reviewer` 或 `admin`）。

## 1. 原理：每个测量通道需要两样东西

预紧力按声弹性换算：

```
F = (Δt − b) / k_L          Δt = 当前温补声时 − RefTime(t₀)
```

| 量 | 含义 | 怎么来 |
|---|---|---|
| **RefTime / RefTemp（t₀）** | 声时基线（零/已知载荷时的参考声时与温度） | **重标定** `/recalibrate` |
| **k_L（+ 截距 b）** | 载荷因子，把 ΔTOF 换成力 | **标定**（quick / 扭矩 / 载荷） |

> k_L 量纲约定：本系统 `k_L = Δt/F`，单位 **ns/N**，量级约 **1e-4**。
> 切勿把标定证书 / 方案附录 C 的 `1/N`（约 1e-11）直接填入——相差一个 t₀，接口有量级护栏会拒绝。

## 2. 两套 ID 不要混

| 接口族 | 路径用的 ID |
|---|---|
| 重标定 `/api/devices/{deviceId}/channels/{channelNo}/recalibrate` | **物理通道号** channel_no |
| 标定 `/api/channels/{channelId}/...` | **channels.id**（数据库主键） |

查映射：

```bash
docker exec -i boltpreload-pg psql -U boltpreload -d boltpreload -tAc \
 "SELECT c.id AS channel_id, c.channel_no, b.code AS bolt
  FROM channels c LEFT JOIN bolts b ON b.channel_id=c.id AND b.deleted_at IS NULL
  WHERE c.device_id=1 AND c.deleted_at IS NULL ORDER BY c.channel_no;"
```

## 3. 标定方法与可信级（calibration-center 口径）

| 方法 | 接口 | 可信级 | 适用 / 约束 |
|---|---|---|---|
| 重标定基线 | `POST /api/devices/{dev}/channels/{ch}/recalibrate` | —（设 t₀，非 k_L） | 螺栓在已知/零载、回波稳定；扫描中禁用 |
| 快速标定 | `POST /api/channels/{cid}/quick-calibrate` | temporary | 已知 k_L 直接写；默认 30 天过期 |
| 扭矩临时标定 | `POST /api/channels/{cid}/torque-calibrate` | temporary | 单点扭矩 T=K·F·d 反推 k_L；需 ≥20 近期有效 EchoTime；默认 7 天过期 |
| 扭矩阶梯拟合 | 采点 `POST /api/channels/{cid}/torque-test-points` → `POST /api/channels/{cid}/apply-torque-fit` | engineering_estimate | 多扭矩点线性拟合；R²<0.95 拒绝，≥0.98 记 passed；试运行/趋势用 |
| **正式载荷标定** | `POST /api/channels/{cid}/load-calibrate` → `POST /api/channels/{cid}/calibrations/{calibId}/countersign` | **formal** | **≥4 已知载荷点**，R²≥0.98 + JJF 不确定度；**两人会签**（提交人≠会签人）；唯一可用于正式报告/告警阈值依据 |

可信级策略（`GET /api/calibration-center/summary`）：

- **formal** — 可用于正式报告和告警阈值依据
- **engineering_estimate** — 可用于试运行和趋势验证，报告须标明非载荷标定
- **temporary** — 仅调试/临时演示，不应用于安全结论

## 4. 推荐现场流程（正式可信路径，逐通道）

设基址：`BASE=http://127.0.0.1:5080`（现场按实际地址）。

### 步骤 0｜登录拿 token（reviewer+）

```bash
TOKEN=$(curl -s -XPOST $BASE/api/auth/login -H 'Content-Type: application/json' \
  -d '{"username":"<reviewer账号>","password":"<密码>"}' \
  | sed -n 's/.*"accessToken":"\([^"]*\)".*/\1/p')
```

### 步骤 1｜螺栓置零载 → 重设基线 t₀（用 channel_no）

```bash
curl -s -XPOST $BASE/api/devices/1/channels/<channelNo>/recalibrate \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{}'
```

- 不带 body = 用该通道**当前实测** EchoTime/SpTemp 作新基线（推荐，确保与真机一致）。
- 也可显式：`-d '{"refTimeNs":32257,"refTempC":24.0}'`。
- 前提：该通道有近期有效测量（EchoTime+SpTemp）与 LEchoWave；设备处于 MultiMeas、未在扫描。
- 效果：整组写设备（MeasMode+Threshold+RefTime+RefTemp+LEchoWave）+ 同步库 + 失效路由缓存。

### 步骤 2｜施加已知载荷，逐点记录 (F, ΔTOF)

用拉伸机 / 测力垫圈施加 **≥4 个已知力 F**，每点力稳定后从实时测量读 `delta_tof_ns`（温补后 ΔTOF），配成 `(forceN, deltaTofNs)`。

### 步骤 3｜提交正式载荷标定（用 channels.id）

```bash
curl -s -XPOST $BASE/api/channels/<channelId>/load-calibrate \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{
    "points":[{"forceN":20000,"deltaTofNs":6.1},{"forceN":40000,"deltaTofNs":12.3},
              {"forceN":60000,"deltaTofNs":18.4},{"forceN":80000,"deltaTofNs":24.6}],
    "refTimeNs":32257, "refTempC":24.0,
    "systematicRelativeUncertainty":0.005,
    "loadingDevice":"拉伸机型号","loadingDeviceCert":"检定证书号"}'
```

- 强制 R²≥0.98；按 JJF 1059.1 评定扩展不确定度。
- 返回 `reviewState:"pending"` + `calibrationId`，**尚未生效**。

### 步骤 4｜另一名 reviewer 会签激活（提交人不能自签）

```bash
curl -s -XPOST $BASE/api/channels/<channelId>/calibrations/<calibrationId>/countersign \
  -H "Authorization: Bearer $TOKEN2"     # TOKEN2 = 不同的 reviewer 账号
```

会签通过后该标定 `approved` 并激活，旧标定自动 superseded，channels 冗余字段同步，缓存失效。

### 步骤 5｜验证预紧力开始计算

```bash
docker exec -i boltpreload-pg psql -U boltpreload -d boltpreload -tAc \
 "SELECT channel_id, preload_n, preload_ratio, algo_version FROM measurements \
  WHERE channel_id=<channelId> ORDER BY ts DESC LIMIT 3;"
```

期望：`preload_n` 非空、`algo_version` 为 `phase1:GbSingleWave`（不再是 `calibration_expired` / `out_of_gb_range` / `phase1:raw`）。

## 5. 现场只有扭矩扳手时（替代步骤 2–4）

按 50 / 100 / 150 / 200 N·m 各采一个扭矩阶梯点，再应用拟合：

```bash
# 每个扭矩档（力稳定后）：
curl -s -XPOST $BASE/api/channels/<channelId>/torque-test-points \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"torqueNm":100,"nutFactor":0.20,"nominalDiameterMm":16}'
# 看线性度：
curl -s $BASE/api/channels/<channelId>/torque-test-points/summary -H "Authorization: Bearer $TOKEN"
# R² 达标后应用为当前标定（engineering_estimate）：
curl -s -XPOST $BASE/api/channels/<channelId>/apply-torque-fit \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"minPoints":4}'
```

> 扭矩法是工程估算（受 nutFactor 离散性影响大），可信级仅 engineering_estimate；正式验收仍须载荷标定替换。

## 6. 落地前提

1. **会签需要两个不同的 reviewer 账号**：正式 `load-calibrate` 的 countersign 会拒绝"提交人=会签人"。建账号：
   ```bash
   curl -s -XPOST $BASE/api/auth/users -H "Authorization: Bearer $TOKEN" \
     -H 'Content-Type: application/json' \
     -d '{"username":"reviewer2","password":"<≥8位>","role":"reviewer","fullName":"复核员二"}'
   ```
2. **标定有效期**：到期后系统按安全策略停止输出"正式"预紧力（`algo_version=calibration_expired`），需在到期前重标定续期。
3. **重标定的前提是已知载荷状态**：基线必须在螺栓自由/零载（或已知力矩）时取，否则后续全部 ΔTOF 失真。

> 提醒：仿真时代遗留的标定（`sim_baseline` / 过期 torque）对真机无效，接真机后须按本指南对真实螺栓重新标定。
> 设备模拟器已整体移除，存量 `sim_baseline` 记录用 `deploy/scripts/20260813-purge-sim-baseline.sql` 清理。
> 某台机器某一时刻的通道标定现状不记在这份指南里——那类快照写完就失真，要看当下状态查
> `GET /api/channels` 与 `GET /api/dashboard/summary`，历史快照见 site-debug 系列排障文档。
