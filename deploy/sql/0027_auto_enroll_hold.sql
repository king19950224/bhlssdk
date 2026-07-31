-- ============================================================================
-- 0027_auto_enroll_hold.sql
-- 检测即螺栓：人工删除抑制 + 默认监测组 ON DELETE SET NULL
-- ----------------------------------------------------------------------------
-- 1) channels.auto_enroll_hold：人工解绑/删除时若探头仍在位，置 true，阻止立刻自动重建；
--    真正离位（Present=false）后清 false；进程重启不清——避免「删了重启又回来」。
-- 2) devices.default_group_id：监测组被删时自动置 NULL（否则删组被 FK 挡住或留下脏引用）。
-- ============================================================================

ALTER TABLE channels
    ADD COLUMN IF NOT EXISTS auto_enroll_hold boolean NOT NULL DEFAULT false;

COMMENT ON COLUMN channels.auto_enroll_hold IS
    '自动建档抑制：人工删除螺栓且探头仍在位时置 true；真正离位后清 false。重启不清。';

-- 0026 加列时默认是 NO ACTION；改为 ON DELETE SET NULL
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'devices_default_group_id_fkey'
    ) THEN
        ALTER TABLE devices DROP CONSTRAINT devices_default_group_id_fkey;
    END IF;
END $$;

ALTER TABLE devices
    ADD CONSTRAINT devices_default_group_id_fkey
    FOREIGN KEY (default_group_id) REFERENCES monitor_groups(id) ON DELETE SET NULL;

COMMENT ON COLUMN devices.default_group_id IS
    '探头在位自动建档的默认监测组（NULL=未设置；同项目唯一活跃组时自动兜底）。监测组删除时自动置 NULL。';

DO $$ BEGIN RAISE NOTICE '0027_auto_enroll_hold.sql 完成'; END $$;
