-- ============================================================================
-- 0019 标定四眼：区分“自签临时标定”与“两人会签正式标定”
-- ----------------------------------------------------------------------------
-- 背景（整体性修复 P1）：0015 引入 review_state 时，quick / torque_estimate /
-- torque_fit 这类“操作人自签”的临时标定沿用了默认值 'approved'，与 load_calibrated
-- 经【第二人会签】后的 'approved' 混在同一个状态里，导致单看 review_state 无法区分
-- “是否真有第二个人签过字”。
--
-- 本迁移引入独立状态 'self_signed'，使 review_state 语义变为：
--   pending     待第二人会签（未激活，仅 load_calibrated 提交后短暂处于此态）
--   self_signed 操作人自签的临时 / 工程估算标定（quick / torque / torque_fit），单人、已激活
--   approved    正式载荷标定经【另一名】reviewer 会签激活（真正的两人四眼）
--   rejected    驳回
-- 这样 'approved' 严格等价于“通过两人会签”，报告 / 审计 / 后续门禁可直接信任该字段。
-- 注意：本迁移不改变任何标定的“计算结果 / 是否生效”，只把信任级别如实记到 review_state。
-- ============================================================================

ALTER TABLE calibration_records DROP CONSTRAINT IF EXISTS chk_calib_review_state;
ALTER TABLE calibration_records
    ADD CONSTRAINT chk_calib_review_state
    CHECK (review_state IN ('pending', 'self_signed', 'approved', 'rejected'));

-- 回填：把历史上“自签方法”（非正式载荷标定）的 'approved' 归正为 'self_signed'，
-- 只有 load_calibrated（走过 countersign 的）保留 'approved'。
UPDATE calibration_records
   SET review_state = 'self_signed'
 WHERE review_state = 'approved'
   AND method IN ('quick', 'torque_estimate', 'torque_fit');

COMMENT ON COLUMN calibration_records.review_state IS
    '复核状态：pending=待第二人会签（未激活）/ self_signed=操作人自签的临时/工程估算标定（单人，已激活）/ approved=正式载荷标定经另一名 reviewer 会签激活（两人四眼）/ rejected=驳回。';
