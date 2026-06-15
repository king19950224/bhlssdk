-- ============================================================================
-- 标定截距（用于扭矩阶梯拟合）
-- ----------------------------------------------------------------------------
-- 扭矩阶梯拟合得到的关系通常是：
--   Δt = K_L * F + b
-- 如果忽略截距 b，直接 F = Δt / K_L，会系统性高估预紧力。
-- 因此把截距冗余到 channels hot path，同时归档到 calibration_records。
-- ============================================================================

ALTER TABLE channels
    ADD COLUMN IF NOT EXISTS k_l_intercept_ns numeric(12,4);

ALTER TABLE calibration_records
    ADD COLUMN IF NOT EXISTS k_l_intercept_ns numeric(12,4);

COMMENT ON COLUMN channels.k_l_intercept_ns IS
    '扭矩阶梯/多点拟合截距 b(ns)，公式 Δt = k_l * F + b；计算时 F=(Δt-b)/k_l。';

COMMENT ON COLUMN calibration_records.k_l_intercept_ns IS
    '标定拟合截距 b(ns)，用于 torque_fit 或多点载荷拟合。';
