-- ============================================================================
-- 维护脚本：标记/清理开发期测试数据和无效测量
-- ----------------------------------------------------------------------------
-- 只建议在开发/联调环境执行。生产执行前必须先备份。
--
-- 背景：
-- 1. 2026-05-21 接真机早期，SDK SessionContext 未注入 ChannelConfig，
--    导致 EchoTime 全 NULL。这批数据只能用于通信链路证明，不能用于预紧力统计。
-- 2. 模拟器测试产生过一批 preload_n / wave_blob_url 数据，不能混进正式验收。
-- 3. 未标定通道曾短暂写过 preload_n=0，已修复；历史值需要置 NULL。
-- ============================================================================

BEGIN;

-- 1. 未标定导致的 0N 预紧力，改回 NULL，避免误导报表。
--    条件：preload_n = 0 且 calibration_id IS NULL。
UPDATE measurements
SET preload_n = NULL,
    preload_ratio = NULL,
    preload_uncertainty_n = NULL,
    algo_version = 'phase1:raw:cleaned'
WHERE preload_n = 0
  AND calibration_id IS NULL;

-- 2. EchoTime NULL 的测量保留，但标记 algo_version，说明它不能用于算法统计。
UPDATE measurements
SET algo_version = 'phase1:no-echo:session-config-missing'
WHERE echo_time_ns IS NULL
  AND algo_version = 'phase1:raw';

-- 3. 模拟器样本：这里只给查询模板，默认不删除。
--    如确认要删，取消下面注释并修改时间窗口。
--
-- DELETE FROM measurements
-- WHERE ts BETWEEN '2026-05-21 13:40:00+08' AND '2026-05-21 15:05:00+08'
--   AND channel_id IN (2, 3)
--   AND wave_blob_url IS NOT NULL;

COMMIT;

-- 执行后检查：
-- SELECT algo_version, COUNT(*) FROM measurements GROUP BY algo_version ORDER BY COUNT(*) DESC;
-- SELECT COUNT(*) FROM measurements WHERE preload_n = 0 AND calibration_id IS NULL;
