-- ============================================================================
-- 2026-08-13 仿真基线（sim_baseline）存量清理（一次性运维脚本）
--
-- 背景：设备模拟器已整体移除。移除前，SimulatedCalibrationProvisioner 会在开发/演示
-- 环境启动时给模拟通道自动写入 method='sim_baseline' / fitter_version='sim:1.0' 的标定，
-- 采集链路照常按它算出预紧力入库。这批数据物理上自洽、界面上跟真数据没有区别——
-- 留在库里，趋势图和报表就会把演示数据当成现场证据。
--
-- 处置：
--   1) 删掉引用 sim_baseline 标定算出的 measurements（假预紧力，不是现场证据）；
--   2) 清掉指向 sim_baseline 的 channels 标定列，螺栓回"未标定"；
--   3) 删掉 sim_baseline 标定记录本身。
--   全部操作留审计（audit_log，user_id=1 管理员，note 标明 [运维直改]）。
--
-- 【识别口径的局限，必须知道】
--   只有"经 sim_baseline 标定算出预紧力"的测量能被可靠识别（靠 measurements.calibration_id）。
--   模拟器跑过但当时通道未标定的测量，库里没有任何字段能把它和真机数据区分开——
--   那批只能按时间窗删，而只有你知道模拟器在哪些时段跑过。见文末第 5 段模板（默认不执行）。
--
-- 【为什么不保留 DisplayLabels 的 sim_baseline 标签】
--   保留。DisplayLabels 仍认 sim_baseline（可信等级"参考"、精度"不适用（演示基线）"）：
--   现场可能有本脚本没跑到的旧库，标签删了那些库的界面会显示空白，比显示"演示基线"更糟。
--
-- 【执行前确认】
--   a) SELECT id, username FROM users WHERE id = 1;  -- 审计的 FK，必须存在
--   b) 先只跑第 0 段看清爆炸半径，确认数量符合预期再跑 BEGIN 之后的部分
-- 【执行后必做】
--   c) 服务在运行则调 POST /api/diagnostics/cache-stats/reset（reviewer+）或重启服务——
--      标定路由缓存 TTL 5 分钟，纯 SQL 直改不失效缓存；
--   d) 核对文末第 4 段验证输出：sim_baseline 计数应为 0。
-- ============================================================================

-- ---------- 0. 干跑：先看清要动多少行（不改数据，可单独执行） ----------
SELECT 'sim_baseline 标定记录' AS what, count(*) AS rows
FROM calibration_records WHERE method = 'sim_baseline'
UNION ALL
SELECT 'channels 指向 sim_baseline', count(*)
FROM channels c
WHERE c.active_calibration_id IN (SELECT id FROM calibration_records WHERE method = 'sim_baseline')
UNION ALL
SELECT 'measurements 由 sim_baseline 算出', count(*)
FROM measurements m
WHERE m.calibration_id IN (SELECT id FROM calibration_records WHERE method = 'sim_baseline')
UNION ALL
SELECT '被 superseded_by 引用的 sim_baseline', count(*)
FROM calibration_records cr
WHERE cr.superseded_by IN (SELECT id FROM calibration_records WHERE method = 'sim_baseline');

BEGIN;

-- ---------- 1. 审计先写：后面几段会把依据删掉，审计必须在删之前取快照 ----------
INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
SELECT 1, 'calibration_revoke', 'channel', c.id,
       jsonb_build_object(
           'calibrationId', cr.id, 'method', cr.method,
           'kL', cr.k_l, 'refTimeNs', cr.ref_time_ns,
           'measurementsPurged', (SELECT count(*) FROM measurements m WHERE m.calibration_id = cr.id)),
       jsonb_build_object('calibrationId', NULL, 'kL', NULL),
       '[运维直改] 清理仿真基线标定（' || cr.method || '/' || COALESCE(cr.fitter_version, '?')
           || '）：设备模拟器已整体移除，其自动写入的演示标定与据此算出的假预紧力一并清除',
       now()
FROM calibration_records cr
JOIN bolts b ON b.id = cr.bolt_id
JOIN channels c ON c.id = b.channel_id
WHERE cr.method = 'sim_baseline';

-- ---------- 2. 删掉由 sim_baseline 算出的测量 ----------
-- measurements.calibration_id 没有外键约束，删标定不会被它挡住，但会留下悬空引用；
-- 这些行的 preload_n 全部是模拟器编码进 EchoTime 再反算出来的，不是现场证据，直接删。
DELETE FROM measurements
WHERE calibration_id IN (SELECT id FROM calibration_records WHERE method = 'sim_baseline');

-- 同口径清异常表（模拟器也会产出负状态码样本）
DELETE FROM measurement_anomalies
WHERE channel_id IN (
    SELECT c.id FROM channels c
    JOIN bolts b ON b.channel_id = c.id
    JOIN calibration_records cr ON cr.bolt_id = b.id
    WHERE cr.method = 'sim_baseline')
  AND ts < (SELECT max(created_at) FROM calibration_records WHERE method = 'sim_baseline');

-- ---------- 3. 解开引用，再删标定记录本身 ----------
-- 3a. channels.active_calibration_id 有外键（fk_channels_active_calib），必须先清列
WITH cleared AS (
    UPDATE channels SET
        k_l = NULL, k_l_uncertainty = NULL, k_l_intercept_ns = NULL,
        active_calibration_id = NULL, updated_at = now()
    WHERE active_calibration_id IN (SELECT id FROM calibration_records WHERE method = 'sim_baseline')
    RETURNING id
)
UPDATE bolts b SET state = 'uncalibrated', updated_at = now()
FROM cleared c
WHERE c.id = b.channel_id AND b.deleted_at IS NULL;

-- 3b. superseded_by 是自引用外键：正式标定若把 sim_baseline 顶替过，指针要先断开，
--     否则删除会 FK 冲突。断开不丢信息——被顶替这件事本身没有价值，顶替它的记录还在。
UPDATE calibration_records SET superseded_by = NULL
WHERE superseded_by IN (SELECT id FROM calibration_records WHERE method = 'sim_baseline');

-- 3c. 删记录（此时已无任何外键指向它）
DELETE FROM calibration_records WHERE method = 'sim_baseline';

COMMIT;

-- ---------- 4. 验证 ----------
SELECT 'sim_baseline 残留（应为 0）' AS what, count(*) AS rows
FROM calibration_records WHERE method = 'sim_baseline'
UNION ALL
SELECT 'fitter_version=sim:1.0 残留（应为 0）', count(*)
FROM calibration_records WHERE fitter_version = 'sim:1.0'
UNION ALL
SELECT 'measurements 悬空 calibration_id（应为 0）', count(*)
FROM measurements m
WHERE m.calibration_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM calibration_records cr WHERE cr.id = m.calibration_id);

SELECT b.code, c.channel_no, b.state, c.k_l, c.active_calibration_id IS NOT NULL AS has_active
FROM bolts b JOIN channels c ON c.id = b.channel_id
WHERE b.deleted_at IS NULL ORDER BY c.device_id, c.channel_no;

-- ---------- 5. 无标定关联的模拟器样本：只给模板，默认不执行 ----------
-- 模拟器跑过但当时通道未标定的测量，库里没有字段能与真机数据区分。只能按时间窗删，
-- 而时间窗只有你知道。先用 SELECT 看清范围，确认了再把 DELETE 那行的注释去掉。
--
-- SELECT date_trunc('hour', ts) AS hour, count(*), min(preload_n), max(preload_n)
-- FROM measurements
-- WHERE ts >= '2026-05-01' AND ts < '2026-08-01'      -- ← 改成模拟器实际跑过的时段
-- GROUP BY 1 ORDER BY 1;
--
-- DELETE FROM measurements
-- WHERE ts >= '2026-05-01' AND ts < '2026-08-01'      -- ← 同上，务必先跑上面的 SELECT
--   AND calibration_id IS NULL;
