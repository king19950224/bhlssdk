-- ============================================================================
-- 0026_device_default_group.sql
-- 设备默认监测组：探头在位自动建档（检测即螺栓）的归属组
-- ----------------------------------------------------------------------------
-- 背景：螺栓的"出现"改为检测驱动——通道回波过下限门+稳定性门确认在位后，
-- 系统自动生成螺栓档案并显示（编号自动、缺省规格，可后补），现场零点击。
-- 自动建档必须知道新螺栓归属哪个监测组（bolts.group_id NOT NULL）：
--   优先用本列；本列为空且全库恰好只有一个活跃组时用那个组兜底；否则跳过并提示。
-- 与设备同项目约束由应用层校验（同点位表导入：禁止跨项目建档）。
-- ============================================================================

ALTER TABLE devices
    ADD COLUMN IF NOT EXISTS default_group_id bigint REFERENCES monitor_groups(id);

COMMENT ON COLUMN devices.default_group_id IS
    '探头在位自动建档的默认监测组（NULL=未设置；全库唯一活跃组时自动兜底）。须与设备同项目。';

-- “曾在位”专用标记：区分"失联"（建档且曾在位，当前无信号→红色警示）与
-- "待接入"（建档但从未在位，如点位表预导入→中性显示）。
-- 不能复用 last_seen_at：它被 CachedChannelResolver 按分钟节流刷新（任何上报都算，
-- 包括无回波的空数据），语义是"最近有数据"而非"最近确认在位"。
-- 本列只由 ChannelPresenceService 在 Present=true 事件时写入。
ALTER TABLE channels
    ADD COLUMN IF NOT EXISTS last_present_at timestamptz;

COMMENT ON COLUMN channels.last_present_at IS
    '最近一次探头在位确认时间（仅在位事件写入，NULL=从未确认在位）。与 last_seen_at（最近任意上报）语义不同。';

DO $$ BEGIN RAISE NOTICE '0026_device_default_group.sql 完成'; END $$;
