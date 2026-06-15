-- ============================================================================
-- 通道参数变更：补「审批」环节（职责分离）
-- ----------------------------------------------------------------------------
-- 原表只有 requested → executed → verified，缺独立审批步：操作员可单方面把
-- 安全攸关的测量参数直接下发设备。商业版安全红线要求两人复核：
--   requested → approved（异人审批）→ executed → verified
-- 另含终态：rejected（审批驳回）、failed（执行失败）。
-- ============================================================================

ALTER TABLE channel_config_change_requests
    ADD COLUMN IF NOT EXISTS approved_by bigint REFERENCES users(id),
    ADD COLUMN IF NOT EXISTS approved_at timestamptz;

COMMENT ON COLUMN channel_config_change_requests.approved_by IS '审批人（必须不同于 requested_by，职责分离）；驳回时记录驳回人';
COMMENT ON COLUMN channel_config_change_requests.approved_at IS '审批/驳回时间';

-- 状态合法集合约束。用 NOT VALID 只约束新写入，避免历史行（若有非常规状态）阻断迁移。
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ccc_status') THEN
        ALTER TABLE channel_config_change_requests
            ADD CONSTRAINT ck_ccc_status
            CHECK (status IN ('requested', 'approved', 'executed', 'verified', 'rejected', 'failed'))
            NOT VALID;
    END IF;
END $$;
