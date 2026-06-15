-- ============================================================================
-- 0015 正式载荷标定的两人复核（dual-sign）工作流
-- ----------------------------------------------------------------------------
-- 商用安全底线：正式 load_calibrated 标定必须"操作人提交 + 另一名复核人会签"才生效，
-- 不允许自签自用。为此引入 review_state，并放开 reviewer 字段的 NOT NULL（待复核时为空）。
-- 既有 quick/torque/torque_fit 自签标定 review_state 默认 'approved'，行为不变。
-- ============================================================================

ALTER TABLE calibration_records
    ADD COLUMN IF NOT EXISTS review_state varchar(16) NOT NULL DEFAULT 'approved';

ALTER TABLE calibration_records
    ALTER COLUMN reviewer_user_id DROP NOT NULL,
    ALTER COLUMN reviewer_signature_at DROP NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'chk_calib_review_state'
    ) THEN
        ALTER TABLE calibration_records
            ADD CONSTRAINT chk_calib_review_state
            CHECK (review_state IN ('pending', 'approved', 'rejected'));
    END IF;
END $$;

-- 待复核队列（pending 通常很少，部分索引足够）
CREATE INDEX IF NOT EXISTS idx_calib_pending
    ON calibration_records(bolt_id, created_at DESC)
    WHERE review_state = 'pending';

COMMENT ON COLUMN calibration_records.review_state IS
    '复核状态：pending=待第二人会签（未激活）/ approved=已会签生效 / rejected=驳回。正式 load_calibrated 走双签。';
