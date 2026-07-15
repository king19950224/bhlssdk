-- ============================================================================
-- 0024: alarms.cleared_reason varchar(64) → text
-- ----------------------------------------------------------------------------
-- 该列最初设计为短代码（auto_recovered / manual_reset / shelved），但手动闭合端点
-- 会把操作员填写的自由文本原样写入——超过 64 字符直接 PostgresException → 500。
-- 闭合原因属处置留痕，不应截断；放宽为 text，长度上限由 API 层校验（500 字符）。
-- ============================================================================

ALTER TABLE alarms ALTER COLUMN cleared_reason TYPE text;

COMMENT ON COLUMN alarms.cleared_reason IS
    '闭合原因：auto_recovered=状态机自动恢复；其余为手动闭合时操作员填写的自由文本（无值时落 manual_reset）。API 层限 500 字符。';

DO $$ BEGIN RAISE NOTICE '0024_alarm_cleared_reason_text.sql 完成'; END $$;
