# 力-声时同步标定（试验模式）设计

> 状态：设计稿（2026-08-18，基于 SDK 1.6 全源码与厂家软件 iFastApp 逆向对标）。
> 目标：把标定从"逐档手动采点"升级为厂家级"试验模式"——连续加载、力与声时同步记录、
> 平台期自动分档、自动拟合 K_L、一键报告。消除扭矩折算 ±30% 误差的正路。
> 前置事实见 `vendor-benchmark-ifastapp.md`；手动力值直标（measuredForceN）已于 08-18 实现，是本设计的 C 级兜底。

## 0. 硬件与 SDK 事实（已核实源码，不是猜测）

| 事实 | 出处 |
|---|---|
| SDK `ChannelResult` 自带 `LowAi`/`HighAi`（模拟量 AD 回读）与 `Angle`（编码器计数），且解码时做中值滤波 | `sdk16/FxSDK/Models/MeasResult.cs`、`GeneralProtocolHandler.cs:1045,1324,1389` |
| **帧内 AI 只有 General 协议族解码**：model 0（Standard）/ 1（Expert）继承 GeneralProtocolHandler | `FxProtocolHandlerFactory.cs`：0/1→General 派生；2→IotR24（仅增益参数，无 AI 解码）；3→MultiChIot（无 AI）；5→UltraHighRate28 |
| **我们的 48 通道设备是 model 3 → 帧内 AI 不可用** | 同上；这是"能不能做到厂家同款"的硬边界 |
| 我们的适配层 DTO **已带全字段**：`FxChannelResultDto.LowAi/HighAi/Angle` 一路流到 Ingestion | `BoltPreload.Fx/Models/FxDecodedEnvelope.cs` |
| 现状入库：AI/Angle 只随 unbound_measurements 的原始 JSON 快照存档，绑定测量直接丢弃 | `PostgresMeasurementSink.cs:969-971` |
| 厂家的 AI→力换算模型：AdZero/AdSpan/FullScale/CalValue/MultiCalValues（多点校准）/ExcVolt | iFastApp `AiConfigs`（sys.db 设备 extra_model JSON） |
| 厂家标定表上限 128 点（MaxCalCount），报告模板 F/T/TA/TF 四种 | iFastApp config.json、Tpl/Test |

## 1. 力值来源三级（接口化，向下兼容）

```
IForceSource（试验期间提供带时间戳的力值流）
├── A 帧内 AI（DeviceAiForceSource）     同步误差 = 0（同帧）      仅 model 0/1 单通道仪器
├── B 旁路测力仪（SerialForceSource）    同步误差 ≤ 力值采样半周期   任意拉力机/数显测力计，PC 直连
└── C 人工录入（ManualForceSource）      人工对表                  已实现（measuredForceN）
```

**为什么 B 够用**：拧紧/拉伸过程的力值变化率低（手动加载秒级、液压拉伸器更慢），
且拟合只用**平台期**（稳态）的点——平台期内力值本身不变，配对误差 ±0.5s 对 F 的影响可忽略。
这让"没有 model 0/1 标定台"的现场用任何带串口/网口的测力仪都能做力值直标。

**A 的适用场景**：实验室标定台（单通道 model 0/1 仪器 + 传感器接设备 AI 口）。
AI→力换算按厂家模型：`F = (Ad − AdZero) / AdSpan × FullScaleN`，支持多点校准表（MultiCalValues 等价物）。
换算配置存设备级 jsonb（`devices.ai_calib`），换算在采集侧完成、落库即工程量。

## 2. 数据模型（新增 2 张表 + 1 列）

```sql
-- 一次标定试验（对标厂家 u_test）
CREATE TABLE calibration_trials (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    channel_id    bigint NOT NULL REFERENCES channels(id),
    bolt_id       bigint NOT NULL REFERENCES bolts(id),
    force_source  varchar(16) NOT NULL,          -- device_ai | serial | manual
    status        varchar(16) NOT NULL DEFAULT 'running',  -- running | done | aborted
    started_at    timestamptz NOT NULL DEFAULT now(),
    ended_at      timestamptz,
    operator_user_id bigint REFERENCES users(id),
    note          text
);

-- 试验期间的原始配对样本流（高频，试验结束后保留为证据链）
CREATE TABLE calibration_trial_samples (
    trial_id      bigint NOT NULL REFERENCES calibration_trials(id),
    ts            timestamptz NOT NULL,
    force_n       numeric(12,2) NOT NULL,        -- 换算后的工程力值
    echo_time_ns  numeric(12,4),                 -- 配对的声时（配对失败为 NULL）
    delta_tof_ns  numeric(12,4),
    sp_temp_c     numeric(6,2),
    PRIMARY KEY (trial_id, ts)
);

-- 聚合档位点复用现有 torque_test_points（拟合/会签/报告链路全部现成），补来源标记：
ALTER TABLE torque_test_points ADD COLUMN trial_id bigint REFERENCES calibration_trials(id);
-- （force 模式点的 note 已带 [力值直标] 标记，trial_id 非空 = 试验模式自动生成）
```

## 3. 平台期自动分档（替代"拧到位点按钮"）

滑窗检测力值平台：**窗口 W 秒内 force 极差 ≤ max(绝对带宽, 满量程×0.5%) 且样本 ≥ N → 平台期**。
平台期内聚合：force 取均值、声时走现有 EchoStabilityGuard 判稳（复用 stddev+半窗趋势双判据），
两者都稳才生成一个标定点（写入 torque_test_points，estimated_preload_n = 平台均值力）。
相邻平台去重：新平台的力均值与上一档差 < 满量程 2% 时不重复出点。
加载方向守卫沿用现有约定：只认单调加载（卸载有螺纹摩擦滞后）。

## 4. 拟合与生效（全部复用现有链路）

试验结束 → 点集就是 torque_test_points（trial_id 过滤）→ 现有 `summary` 拟合
（regr_slope/intercept/r2、当前基线隔离、坏点剔除）→ 现有 `apply-torque-fit` 生效
（R² 门槛 0.98/0.95、会签、审计）。**试验模式不新建任何拟合/生效逻辑**。

## 5. API 与前端

- `POST /api/channels/{id}/calibration-trials`（开始：force_source、串口参数或 AI 配置引用）
- `POST .../calibration-trials/{tid}/force-sample`（Source B/C 的力值进样口：串口守护进程或手动界面调用）
- `GET .../calibration-trials/{tid}/live`（实时曲线：F-t、Δt-t、已识别平台、已生成点）
- `POST .../calibration-trials/{tid}/finish|abort`
- 前端：标定向导新增"试验模式（连续加载）"场景卡——实时 F-Δt 曲线 + 平台自动打点动画 +
  结束即拟合页（复用现有拟合页）。
- 串口测力仪接入做成独立小守护（`tools/force-feeder`）：读串口 → POST force-sample。
  协议先支持 Modbus RTU 保持寄存器 + 常见数显表连续输出两种，插件式扩展。

## 6. 分期

| 期 | 内容 | 依赖 |
|---|---|---|
| P1 | 表结构 + 试验模式 API + 平台检测 + 向导试验页 + Source C（把手动逐档升级为平台自动打点） | 无（纯软件） |
| P2 | Source B 串口测力仪守护 + 配对 | 一台带串口/网口的测力仪 |
| P3 | Source A 帧内 AI：绑定测量的 LowAi/HighAi 入库（measurements 补 ai 列或 feature_vector）、AI 换算配置、试验模式直读 | **需 model 0/1 单通道仪器**（现有 48 通道 model 3 协议无 AI 帧） |

## 7. 要向厂家确认的问题（P3 前置）

1. 48 通道（model 3）固件是否有任何模式/帧携带 AI 读数？（源码看是没有，求证是否有固件更新计划）
2. model 0/1 单通道仪器的 AI 口电气规格（激励电压、AD 位数、可接传感器类型）——采购标定台仪器用
3. `Angle` 通道在 model 0/1 上的接线与 CountsPerR（若做扭矩-转角试验）
