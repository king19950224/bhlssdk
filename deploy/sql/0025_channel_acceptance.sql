-- ============================================================================
-- 0025_channel_acceptance.sql
-- 通道点位验收：验收记录表 + channels 接线指纹列
-- ----------------------------------------------------------------------------
-- 背景：通道↔螺栓对应关系的建立不再依赖"敲击识别"（扰动探头耦合层、在役设备不合规、
-- 结论不可追溯），改为工程化流程：点位表导入 → 接入验收（顺序接入 / 接头级拔插确认）
-- → 运行期接线指纹守卫。本迁移提供后两步的数据模型：
-- 1. channel_acceptance：每次验收动作一条记录（方法/电缆标签/声时快照/操作人），
--    验收报告（竣工资料）由此导出；同一通道可多次验收，最新一条为当前有效。
-- 2. channels.accepted_tof_ns/accepted_at：验收时的声时指纹，供运行期守卫比对，
--    与标定基线 ref_time_ns 分开——后者是安全攸关的标定资产，不混用。
-- ============================================================================

ALTER TABLE channels
    ADD COLUMN IF NOT EXISTS accepted_tof_ns numeric(12,2),
    ADD COLUMN IF NOT EXISTS accepted_at timestamptz;

COMMENT ON COLUMN channels.accepted_tof_ns IS
    '接入验收时的声时快照（接线指纹）。运行期声时相对它超差 → wiring_suspect 告警。与标定基线 ref_time_ns 无关。';
COMMENT ON COLUMN channels.accepted_at IS '最近一次接入验收时间（NULL=未验收）。';

CREATE TABLE IF NOT EXISTS channel_acceptance (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    device_id       bigint NOT NULL REFERENCES devices(id),
    channel_id      bigint NOT NULL REFERENCES channels(id),
    channel_no      smallint NOT NULL,                 -- 冗余通道号，报告直读
    -- sequential=顺序接入验收（新装，逐根接线确认）
    -- loopcheck =接头级拔插确认（存量补录，断开电缆接头观察哪个通道静默）
    -- manual    =人工登记（无信号验证，如备用通道按图纸登记）
    method          varchar(16) NOT NULL CHECK (method IN ('sequential','loopcheck','manual')),
    cable_label     varchar(64),                       -- 电缆标签号（现场线缆吊牌）
    position_label  varchar(64),                       -- 验收时登记的物理位置（同步写 channels.name）
    bolt_id         bigint REFERENCES bolts(id),       -- 验收时已绑定的螺栓（可空：先验收后建档）
    tof_ns          numeric(12,2),                     -- 验收时声时快照（manual 可空）
    peak_to_peak    integer,
    snr_db          numeric(6,2),
    hardware_state  varchar(32),                       -- 验收时的在位状态快照
    accepted_by     bigint NOT NULL REFERENCES users(id),
    accepted_at     timestamptz NOT NULL DEFAULT now(),
    note            text
);

CREATE INDEX IF NOT EXISTS idx_channel_acceptance_device
    ON channel_acceptance(device_id, channel_no, accepted_at DESC);

COMMENT ON TABLE channel_acceptance IS
    '通道接入验收记录（通道↔电缆↔位置对应关系的确认凭证）。一次验收一条，最新一条有效；验收报告由此导出。';

DO $$ BEGIN RAISE NOTICE '0025_channel_acceptance.sql 完成'; END $$;
