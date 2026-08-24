-- 0033: 标定试验模式（力-声时同步采集，见 force-sync-calibration-design.md）
-- 把"逐档手动采点"升级为"连续加载、平台期自动分档"：试验期间外部力值（人工/串口测力仪/设备AI）
-- 持续进样，与声时按时间戳配对；力值平台 + 声时稳双达标自动生成标定点。
-- 档位点仍写 torque_test_points（拟合/会签/报告链路全部复用），本迁移只补试验骨架。

-- 一次标定试验（对标厂家 iFastApp 的 u_test）
CREATE TABLE IF NOT EXISTS calibration_trials (
    id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    channel_id       bigint NOT NULL REFERENCES channels(id),
    bolt_id          bigint NOT NULL REFERENCES bolts(id),
    -- manual=人工进样（前端定时上报当前表读数）；serial=串口测力仪守护进程；device_ai=设备帧内AI（P3，需 model 0/1 仪器）
    force_source     varchar(16) NOT NULL,
    status           varchar(16) NOT NULL DEFAULT 'running',   -- running | done | aborted
    started_at       timestamptz NOT NULL DEFAULT now(),
    ended_at         timestamptz,
    operator_user_id bigint REFERENCES users(id),
    note             text
);

COMMENT ON TABLE calibration_trials IS
    '标定试验（力-声时同步采集）。连续加载过程的力值平台自动分档出点，点写 torque_test_points（trial_id 回链）。';

-- 同一通道同时只允许一场进行中的试验（与 probe-scan 的占位思路一致，但用库约束兜底）
CREATE UNIQUE INDEX IF NOT EXISTS ux_calibration_trials_running
    ON calibration_trials(channel_id)
    WHERE status = 'running';

-- 试验期间的原始配对样本流（证据链：报告和事后复核都要能翻到逐条原始值）
CREATE TABLE IF NOT EXISTS calibration_trial_samples (
    trial_id      bigint NOT NULL REFERENCES calibration_trials(id) ON DELETE CASCADE,
    ts            timestamptz NOT NULL,
    force_n       numeric(12,2) NOT NULL,
    echo_time_ns  numeric(12,4),          -- 进样时刻配对到的最近声时（±配对窗内无测量则为 NULL）
    sp_temp_c     numeric(6,2),
    PRIMARY KEY (trial_id, ts)
);

-- 试验模式自动生成的档位点回链（人工采点/力值直标的点此列为 NULL）
ALTER TABLE torque_test_points
    ADD COLUMN IF NOT EXISTS trial_id bigint REFERENCES calibration_trials(id);
