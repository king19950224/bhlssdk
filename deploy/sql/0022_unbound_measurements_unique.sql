-- ============================================================================
-- 0022_unbound_measurements_unique.sql
-- unbound_measurements 幂等：加 (channel_id, ts) 唯一键，配合应用侧 ON CONFLICT DO NOTHING。
-- ----------------------------------------------------------------------------
-- 背景：绑定测量的 INSERT 早有 ON CONFLICT (channel_id, ts) DO NOTHING（measurements 主键即此），
-- 但未绑定通道走 WriteUnboundAsync 的普通 INSERT、表上也无对应唯一约束。入库失败 → WAL 重放
-- 会对同一 (channel_id, ts) 再插一行，造成 /unbound 报表与 backfill 重复。此迁移与热路径对齐口径。
-- 幂等：先去重（保留每组最小 id），再建 IF NOT EXISTS 唯一索引，可重复执行。
-- ============================================================================

-- 1. 去重历史重复行（保留每组最小 id）——建唯一索引前必须先清，否则建索引失败。
DELETE FROM unbound_measurements a
USING unbound_measurements b
WHERE a.channel_id = b.channel_id
  AND a.ts = b.ts
  AND a.id > b.id;

-- 2. 唯一索引（与 measurements(channel_id, ts) 主键同口径）。
CREATE UNIQUE INDEX IF NOT EXISTS ux_unbound_channel_ts
    ON unbound_measurements(channel_id, ts);

DO $$ BEGIN RAISE NOTICE '0022_unbound_measurements_unique.sql 完成'; END $$;
