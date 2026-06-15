-- ============================================================================
-- 0014_channel_name.sql
-- channels 加可读名称（物理位置标签）
-- ----------------------------------------------------------------------------
-- 设计意图：
-- 现场按硬件办卡通道号读取数据，但运维和报表需要可读的"物理位置"名（如"塔筒法兰-北侧"）。
-- 这个名称属于通道本身（"5 号板卡口对应的物理位置"），不属于 bolt（bolt 是上面拧的具体螺栓，可换）。
-- 所以放在 channels 表，而不是 bolts。
-- ============================================================================

ALTER TABLE channels
    ADD COLUMN IF NOT EXISTS name varchar(64);

COMMENT ON COLUMN channels.name IS
    '通道可读名称（物理位置标签）。运维起的，如"塔筒法兰-北侧"。不强制唯一，允许空。';

-- name 不是查询热路径，只在管理/报表场景用；只对非空值建轻量索引
CREATE INDEX IF NOT EXISTS idx_channels_name
    ON channels(name)
    WHERE name IS NOT NULL AND deleted_at IS NULL;

DO $$ BEGIN RAISE NOTICE '0014_channel_name.sql 完成'; END $$;
