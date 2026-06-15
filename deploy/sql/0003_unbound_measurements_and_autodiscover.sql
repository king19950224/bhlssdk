-- ============================================================================
-- 通道自动发现 + 未绑定测量留存
-- ----------------------------------------------------------------------------
-- 设计目标：
-- 1. 设备上报未知 channel_no 时，自动写一行到 channels（state='auto_discovered'），
--    保证下次能解析、不丢；
-- 2. 已发现通道但还未绑定 bolt 的测量，落到 unbound_measurements，
--    等业主在前台完成 bolt 登记后，由数据迁移脚本把 unbound 数据 backfill 到 measurements。
-- ============================================================================

-- 1. channels 状态字段：用于区分人工登记 vs 自动发现
ALTER TABLE channels ADD COLUMN IF NOT EXISTS state varchar(32) NOT NULL DEFAULT 'manual';
COMMENT ON COLUMN channels.state IS '通道状态：manual=人工登记 | auto_discovered=自动发现待人工确认 | retired=停用';

ALTER TABLE channels ADD COLUMN IF NOT EXISTS first_seen_at timestamptz;
ALTER TABLE channels ADD COLUMN IF NOT EXISTS last_seen_at timestamptz;

-- 2. 未绑定测量（channel 已知/已自动发现，但还没绑定 bolt）
--    保留 raw_payload 让后续 bolt 绑定后可以重放计算
CREATE TABLE IF NOT EXISTS unbound_measurements (
    id              bigint GENERATED ALWAYS AS IDENTITY,
    device_id       bigint NOT NULL,
    channel_id      bigint NOT NULL,                  -- 必须先有 channel（即使是 auto_discovered）
    channel_no      smallint NOT NULL,                -- 冗余存设备原始通道号，方便人看
    ts              timestamptz NOT NULL,
    ingested_at     timestamptz NOT NULL DEFAULT now(),
    echo_time_ns    numeric(12,2),
    delta_tof_ns    numeric(12,2),
    sp_temp_c       numeric(6,2),
    gain            real,
    status_code     smallint NOT NULL,
    wave_blob_url   varchar(512),
    raw_payload     jsonb,                            -- 完整 ChannelResult JSON 序列化，方便 backfill
    PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS idx_um_channel_ts ON unbound_measurements(channel_id, ts DESC);
CREATE INDEX IF NOT EXISTS idx_um_device_chno_ts ON unbound_measurements(device_id, channel_no, ts DESC);

COMMENT ON TABLE unbound_measurements IS
    '通道已发现但 bolt 未绑定时的测量暂存。完成 bolt 登记后由 backfill 脚本迁移到 measurements。';
