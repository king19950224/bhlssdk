-- ============================================================================
-- 告警闭合可追责：补 cleared_by
-- ----------------------------------------------------------------------------
-- alarms 原有 acknowledged_by（确认人），但手动闭合（/clear，安全动作）只记
-- cleared_at/cleared_reason，不记是谁闭合的。商业版安全红线“告警确认可追责”
-- 要求闭合也可追溯到人。
-- cleared_by 为 NULL = 系统自动闭合（条件恢复）；非 NULL = 该用户手动闭合。
-- ============================================================================

ALTER TABLE alarms
    ADD COLUMN IF NOT EXISTS cleared_by bigint REFERENCES users(id);

COMMENT ON COLUMN alarms.cleared_by IS '手动闭合人（NULL 表示系统自动闭合）';
