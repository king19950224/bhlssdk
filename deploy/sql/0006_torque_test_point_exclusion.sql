-- ============================================================================
-- 扭矩实验点排除标记
-- ----------------------------------------------------------------------------
-- 有些点在扭矩切换过程中采集，EchoTime 标准差明显异常。
-- 这些点需要保留审计记录，但不参与线性拟合。
-- ============================================================================

ALTER TABLE torque_test_points
    ADD COLUMN IF NOT EXISTS excluded boolean NOT NULL DEFAULT false;

ALTER TABLE torque_test_points
    ADD COLUMN IF NOT EXISTS exclude_reason text;

COMMENT ON COLUMN torque_test_points.excluded IS
    '是否从拟合/统计中排除。保留原始记录但不参与 R²/K_L 拟合。';

COMMENT ON COLUMN torque_test_points.exclude_reason IS
    '排除原因，如：切换扭矩过渡期、EchoTime 标准差异常、操作记录错误。';
