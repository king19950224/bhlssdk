-- ============================================================================
-- 0023_backfill_bolt_state_calibrated.sql
-- 回填 bolts.state —— 修复"已标定却显示未标定"的历史数据漂移。
-- ----------------------------------------------------------------------------
-- 背景：API 标定路径（quick/torque/torque_fit/countersign）历史上只更新 channels 的
-- k_l / active_calibration_id，漏写 bolts.state，导致这些在产螺栓被总览/资产/验收报告误判"未标定"。
-- 本轮已把"是否已标定"的判定改为按通道有效标定派生（单一事实源），并让标定接口回写 state；
-- 此迁移把存量数据一次性对齐：通道有 k_l + active_calibration_id 的螺栓置 'calibrated'。
-- 幂等：state <> 'calibrated' 守卫，可重复执行。
-- ============================================================================

UPDATE bolts b
SET state = 'calibrated', updated_at = now()
FROM channels c
WHERE c.id = b.channel_id
  AND b.deleted_at IS NULL
  AND b.state <> 'calibrated'
  AND c.k_l IS NOT NULL
  AND c.active_calibration_id IS NOT NULL;

DO $$ BEGIN RAISE NOTICE '0023_backfill_bolt_state_calibrated.sql 完成'; END $$;
