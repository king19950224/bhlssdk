-- ============================================================================
-- 0013_channel_probe_scan.sql
-- 通道自动检测（Probe Scan）：扫描运行记录 + 每通道判定结果
-- 设计文档：资料/工程文档/probe-scan-design.md
-- ----------------------------------------------------------------------------
-- 设计意图：
-- 1. 扫描期间逐通道 SetWorkMode SingleMeas，复用 measurements/unbound_measurements/
--    wave_features 三张表的数据做窗口统计，本表只保存扫描元数据 + 判定结果。
-- 2. 扫描结果是一次性诊断快照（不是时序数据），需永久保留供溯源；不放进 hypertable。
-- 3. 不自动绑定 bolt；扫描完成时只更新 channels.hardware_state / last_seen_at。
-- ============================================================================

CREATE TABLE IF NOT EXISTS channel_probe_scan_runs (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    device_id           bigint NOT NULL REFERENCES devices(id),
    requested_by        bigint REFERENCES users(id),
    requested_at        timestamptz NOT NULL DEFAULT now(),
    started_at          timestamptz,
    finished_at         timestamptz,
    status              varchar(16) NOT NULL DEFAULT 'pending',
    strategy            varchar(32) NOT NULL DEFAULT 'single_meas',
    requested_channels  smallint[] NOT NULL,
    settle_seconds      smallint NOT NULL,
    sample_seconds      smallint NOT NULL,
    thresholds_jsonb    jsonb NOT NULL,
    summary_jsonb       jsonb,
    error_message       text,
    CHECK (status IN ('pending','running','completed','failed','aborted')),
    CHECK (strategy IN ('single_meas','mcr_single')),
    CHECK (settle_seconds BETWEEN 0 AND 30),
    CHECK (sample_seconds BETWEEN 1 AND 60)
);

CREATE INDEX IF NOT EXISTS idx_probe_scan_runs_device
    ON channel_probe_scan_runs(device_id, requested_at DESC);

CREATE INDEX IF NOT EXISTS idx_probe_scan_runs_open
    ON channel_probe_scan_runs(device_id)
    WHERE status IN ('pending','running');

COMMENT ON TABLE channel_probe_scan_runs IS
    '通道自动检测运行记录。一次扫描一行，保留参数、阈值、汇总，便于现场追溯。';
COMMENT ON COLUMN channel_probe_scan_runs.status IS
    'pending=入库待执行 | running=执行中 | completed=正常完成 | failed=异常退出 | aborted=用户中止';
COMMENT ON COLUMN channel_probe_scan_runs.strategy IS
    'single_meas=SetWorkMode(SingleMeas, ch) | mcr_single=MCRConfig.EnabledChannels=[ch] 退路';
COMMENT ON COLUMN channel_probe_scan_runs.thresholds_jsonb IS
    '本次扫描使用的判定阈值快照（minSamples/snrGoodDb/echoStdGoodNs/peakToPeakMin）';

CREATE TABLE IF NOT EXISTS channel_probe_scan_results (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    scan_run_id         bigint NOT NULL REFERENCES channel_probe_scan_runs(id) ON DELETE CASCADE,
    channel_no          smallint NOT NULL,
    channel_id          bigint REFERENCES channels(id),
    verdict             varchar(24) NOT NULL,
    samples             int NOT NULL DEFAULT 0,
    samples_with_echo   int NOT NULL DEFAULT 0,
    echo_rate_pct       numeric(5,2),
    echo_avg_ns         numeric(12,2),
    echo_std_ns         numeric(12,3),
    peak_to_peak_avg    numeric(10,1),
    snr_avg_db          numeric(6,2),
    bound_bolt_code     varchar(64),
    started_at          timestamptz NOT NULL,
    finished_at         timestamptz NOT NULL,
    CHECK (verdict IN ('active_good','active_weak','active_no_echo','not_reporting','inconclusive'))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_probe_scan_results_run_channel
    ON channel_probe_scan_results(scan_run_id, channel_no);

CREATE INDEX IF NOT EXISTS idx_probe_scan_results_channel
    ON channel_probe_scan_results(channel_id, finished_at DESC)
    WHERE channel_id IS NOT NULL;

COMMENT ON TABLE channel_probe_scan_results IS
    '每个被扫通道在本次扫描中的统计与判定结果。verdict 由 ProbeScanResultJudge 计算。';

-- channels.last_seen_at 已经在 0003 加过，复用即可，扫描结束时更新。
-- channels.hardware_state 已经在 0012 加过，扫描结束时按 verdict 映射写入：
--   active_good   -> healthy
--   active_weak   -> noisy
--   active_no_echo-> suspect
--   not_reporting -> no_data
--   inconclusive  -> 不更新

DO $$ BEGIN RAISE NOTICE '0013_channel_probe_scan.sql 完成'; END $$;
