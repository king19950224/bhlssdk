-- ============================================================================
-- 0012_multichannel_model.sql
-- 多通道模型修复：物理通道目录 / 业务绑定历史 / 未绑定波形 / 多通道测量键
-- ============================================================================

-- 一台多通道设备的物理通道数。法柯多通道设备现场按 40 通道使用；
-- SDK 上限仍以厂家协议为准，本字段是业务目录初始化依据。
ALTER TABLE devices
    ADD COLUMN IF NOT EXISTS channel_count smallint NOT NULL DEFAULT 40;

COMMENT ON COLUMN devices.channel_count IS
    '设备物理通道数。多通道监测设备现场默认 40；用于初始化/展示通道目录。';

-- 物理通道、采集、业务状态分层。
ALTER TABLE channels
    ADD COLUMN IF NOT EXISTS sampling_enabled boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS hardware_state varchar(32) NOT NULL DEFAULT 'unknown',
    ADD COLUMN IF NOT EXISTS business_state varchar(32) NOT NULL DEFAULT 'unbound',
    ADD COLUMN IF NOT EXISTS config_source varchar(32) NOT NULL DEFAULT 'database',
    ADD COLUMN IF NOT EXISTS config_synced_at timestamptz;

COMMENT ON COLUMN channels.sampling_enabled IS
    '是否应加载到 SDK Session / 参与采集诊断。不同于是否绑定业务螺栓。';
COMMENT ON COLUMN channels.hardware_state IS
    '物理/链路状态：unknown/healthy/no_data/noisy/fault/suspect 等。';
COMMENT ON COLUMN channels.business_state IS
    '业务绑定状态：unbound/bound/moved/disabled 等。';
COMMENT ON COLUMN channels.config_source IS
    '当前 DB 配置来源：database/device_readback/appsettings/manual/recalibrated。';
COMMENT ON COLUMN channels.config_synced_at IS
    '最近一次把设备读回参数同步到 channels 的时间。';

-- 现有数据补齐业务状态。
UPDATE channels c
   SET business_state = CASE
        WHEN EXISTS (SELECT 1 FROM bolts b WHERE b.channel_id = c.id AND b.deleted_at IS NULL) THEN 'bound'
        ELSE 'unbound'
       END
 WHERE c.deleted_at IS NULL;

-- 一个有效通道同一时刻最多绑定一个有效 bolt。
CREATE UNIQUE INDEX IF NOT EXISTS ux_bolts_active_channel
    ON bolts(channel_id)
    WHERE deleted_at IS NULL;

-- 业务绑定历史：以后移动/解绑 bolt 不再靠手工 SQL，必须记录前后通道和标定处理策略。
CREATE TABLE IF NOT EXISTS channel_binding_history (
    id                          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    bolt_id                     bigint NOT NULL REFERENCES bolts(id),
    from_channel_id             bigint REFERENCES channels(id),
    to_channel_id               bigint REFERENCES channels(id),
    action                      varchar(32) NOT NULL,
    calibration_action          varchar(32) NOT NULL DEFAULT 'invalidate',
    moved_by                    bigint REFERENCES users(id),
    moved_at                    timestamptz NOT NULL DEFAULT now(),
    before_jsonb                jsonb,
    after_jsonb                 jsonb,
    note                        text,
    CHECK (action IN ('bind','unbind','move')),
    CHECK (calibration_action IN ('invalidate','transfer_with_validation','keep_uncomputed'))
);

CREATE INDEX IF NOT EXISTS idx_channel_binding_history_bolt
    ON channel_binding_history(bolt_id, moved_at DESC);

CREATE INDEX IF NOT EXISTS idx_channel_binding_history_channel
    ON channel_binding_history(to_channel_id, moved_at DESC);

COMMENT ON TABLE channel_binding_history IS
    'bolt 与物理 channel 的业务绑定历史。所有绑定/解绑/换接口必须经 API 写入并审计。';

-- 未绑定通道也要保留波形路径，便于 1-40 通道诊断和后续 backfill。
ALTER TABLE unbound_measurements
    ADD COLUMN IF NOT EXISTS wave_blob_url varchar(512);

COMMENT ON COLUMN unbound_measurements.wave_blob_url IS
    '未绑定通道的原始波形文件路径。绑定 bolt 后可用于回放/回填。';

-- 多通道唯一键：以 channel_id + ts 去重，避免同一 bolt 换接口/多源回放时被 (bolt_id, ts) 静默丢数。
-- Timescale hypertable 若启用了 compression，必须先暂时关闭 compression 再调整 key。
DO $$
DECLARE
    compression_was_enabled boolean := false;
BEGIN
    SELECT COALESCE(compression_enabled, false)
      INTO compression_was_enabled
      FROM timescaledb_information.hypertables
     WHERE hypertable_schema = 'public'
       AND hypertable_name = 'measurements';

    IF compression_was_enabled THEN
        PERFORM remove_compression_policy('measurements', if_exists => true);
        -- 存在已压缩 chunk 时不能直接关压缩。0001 的压缩策略 30 天起压，
        -- 装机超过 30 天才升级到本迁移的库会踩中，先逐个解压再关。
        PERFORM decompress_chunk(c, if_compressed => true)
           FROM show_chunks('measurements') AS c;
        ALTER TABLE measurements SET (timescaledb.compress = false);
    END IF;

    IF EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'measurements'::regclass
          AND conname = 'measurements_pkey'
    ) THEN
        ALTER TABLE measurements DROP CONSTRAINT measurements_pkey;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'measurements'::regclass
          AND conname = 'measurements_channel_ts_key'
    ) THEN
        ALTER TABLE measurements
            ADD CONSTRAINT measurements_channel_ts_key PRIMARY KEY (channel_id, ts);
    END IF;

    IF compression_was_enabled THEN
        ALTER TABLE measurements SET (
            timescaledb.compress,
            timescaledb.compress_segmentby = 'channel_id',
            timescaledb.compress_orderby = 'ts DESC'
        );
        PERFORM add_compression_policy('measurements', INTERVAL '30 days', if_not_exists => true);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_meas_bolt_ts
    ON measurements(bolt_id, ts DESC);

-- 为每台设备补齐 1..channel_count 的物理通道目录。只创建 channel，不自动绑定 bolt。
INSERT INTO channels (
    device_id, channel_no, probe_freq_mhz, gain, threshold, meas_mode,
    sampling_enabled, state, hardware_state, business_state, config_source
)
SELECT d.id, gs.ch, 2.0, 10, 0, 2,
       true, 'manual', 'unknown', 'unbound', 'database'
FROM devices d
CROSS JOIN LATERAL generate_series(1, GREATEST(d.channel_count, 1)) AS gs(ch)
WHERE d.deleted_at IS NULL
ON CONFLICT DO NOTHING;

DO $$ BEGIN RAISE NOTICE '0012_multichannel_model.sql 完成'; END $$;
