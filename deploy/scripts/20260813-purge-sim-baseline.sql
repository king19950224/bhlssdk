-- ============================================================================
-- 2026-08-13 仿真基线（sim_baseline）存量清理（一次性运维脚本）
--
-- 背景：设备模拟器已整体移除。移除前，SimulatedCalibrationProvisioner 会在开发/演示
-- 环境启动时给通道自动写入 method='sim_baseline' / fitter_version='sim:1.0' 的标定。
-- 它写的是模拟器那套自洽的假物理参数（ref_time_ns=26000、k_l=0.003、ref_temp_c=22），
-- 一旦落到装了真探头的通道上，这台通道就在拿一条假基线换算真回波。
--
-- 【本机实测到的真实状况，决定了下面的处置口径】（行数为 2026-08-13 快照；CH8 在测，
--   数字随时间涨，08-14 复核时已到 86219，口径不变）
--   查库发现：11 条 sim_baseline 标定；2 个通道（CH6/CH8）的 active 仍指向它们；
--   70540 条测量的 calibration_id 指向它们。
--   但这些测量**全是真探头数据**，不是模拟器产出的：
--     CH2  声时 32237~32267 ns
--     CH11 声时 32719~32754 ns
--     CH8  6~7 月 32223~32313 ns、8 月起 27216~27241 ns（探头重贴后回波位移，仍在进）
--   模拟器把预紧力编码在 26000 ns 附近（基准 RefTofNs=26000 + 预紧力对应的数百 ns），
--   上面一条都不在那个区间。
--   也就是说：测量是真的，只有标定关联是假的——真探头的测量被挂在了假标定 id 下。
--   这批测量的 preload_n 全部为 NULL（标定护栏已把它们拦成无效，界面没在显示假读数）。
--
-- 处置（按上面的事实定的，与直觉相反的一条特别注明）：
--   1) 删掉 sim_baseline 标定记录、清掉通道对它的指向，通道回"未标定"——CH8 是带真探头
--      的在测通道，清完界面从"超范围无数据"变成"未标定"，这是变诚实不是变坏；
--   2) **不删那 70540 条测量**。它们是真探头的回波与温度历史，删掉是毁真实证据。
--      只把悬空的 calibration_id 置 NULL 并打 algo_version 标记，保留可追溯性。
--   全部操作留审计（audit_log，user_id=1 管理员，note 标明 [运维直改]）。
--
-- 【执行后 CH8 必须做的事】
--   CH8 走自动法（meas_mode=0）。清掉假基线后要按 calibration-field-guide.md 重新置零，
--   置零会连带写入 0031 迁移加的锚点组（ref_env_time_ns / ref_period_ns / ref_echo_wave）——
--   自动法缺锚点每次重启都会重新捕获，有整周期跳变风险。别只补 k_l 就当标定完了。
--
-- 【执行前确认】
--   a) SELECT id, username FROM users WHERE id = 1;  -- 审计的 FK，必须存在
--   b) 先只跑第 0 段。若你那台库的声时分布与上面记录的不同（出现大量 26000 附近的行），
--      说明确实有模拟器产出的测量混在里面，那就得另外按时间窗处理，别直接跑下面的 BEGIN。
-- 【执行后必做，顺序不能颠倒】（08-14 对本机库实跑校准过的流程）
--   c) 先调 POST /api/diagnostics/cache-stats/reset（reviewer+）或重启服务——路由缓存
--      TTL 5 分钟，主事务执行期间（本机实测 4.4 分钟，解压开销）在测通道仍按缓存里的
--      旧标定 id 写入，不重置缓存悬空行会一直长；
--   d) 再跑第 5 段补扫：把主事务窗口内写入的悬空行解关联（本机实测 20 条）；
--   e) 核对第 4/5 段验证输出：sim_baseline 计数、sim 常数残值、与本次相关的悬空均应为 0。
--      注意"悬空 calibration_id"不一定归零——本机就查出 303 条挂在更早被删的
--      TorqueFit 等实验标定上的历史遗留（2026-06），那不是本脚本的对象，别顺手动它。
-- ============================================================================

-- ---------- 0. 干跑：先看清要动多少行，并确认测量到底是真是假（不改数据） ----------
SELECT 'sim_baseline 标定记录' AS what, count(*)::text AS val
FROM calibration_records WHERE method = 'sim_baseline'
UNION ALL
SELECT 'channels 指向 sim_baseline', count(*)::text
FROM channels c
WHERE c.active_calibration_id IN (SELECT id FROM calibration_records WHERE method = 'sim_baseline')
UNION ALL
SELECT 'measurements 挂在 sim_baseline 下', count(*)::text
FROM measurements m
WHERE m.calibration_id IN (SELECT id FROM calibration_records WHERE method = 'sim_baseline')
UNION ALL
SELECT '其中 preload_n 非空（应为 0）', count(preload_n)::text
FROM measurements m
WHERE m.calibration_id IN (SELECT id FROM calibration_records WHERE method = 'sim_baseline')
UNION ALL
SELECT '被 superseded_by 引用的 sim_baseline', count(*)::text
FROM calibration_records cr
WHERE cr.superseded_by IN (SELECT id FROM calibration_records WHERE method = 'sim_baseline');

-- 关键判据：声时落在模拟器编码区间的行数。若明显非零，先停下来另作处置。
-- 区间取 25500~27000：模拟器基准 26000 加预紧力对应的数百 ns，再留温补余量；
-- 别取更宽——CH8 探头重贴后的真实回波在 27.2k，粗暴放宽到 28000 会把真数据误圈进来
-- （08-14 实测：25500~27000 命中 0 行，27000~28000 里的 1.6 万行全是 CH8 在测真数据）。
SELECT c.channel_no,
       count(*) AS 总数,
       count(*) FILTER (WHERE m.echo_time_ns BETWEEN 25500 AND 27000) AS 疑似模拟器区间,
       round(min(m.echo_time_ns), 0) AS 最小声时,
       round(max(m.echo_time_ns), 0) AS 最大声时
FROM measurements m JOIN channels c ON c.id = m.channel_id
WHERE m.calibration_id IN (SELECT id FROM calibration_records WHERE method = 'sim_baseline')
GROUP BY c.channel_no ORDER BY c.channel_no;

BEGIN;

-- measurements 是 TimescaleDB 压缩表，calibration_id 不是压缩段的分段键：下面第 2 段的
-- UPDATE 只改几万行，却要把命中的整段解压来评估谓词，默认单事务 10 万元组的解压上限
-- 直接爆（08-14 实测：4579306 元组，ERROR: tuple decompression limit exceeded）。
-- SET LOCAL 只对本事务生效，提交/回滚后自动还原。被改的行落在未压缩区，
-- 由压缩策略后台自行重新压缩，不必手工处理。
SET LOCAL timescaledb.max_tuples_decompressed_per_dml_transaction = 0;

-- ---------- 1. 审计先写：后面会把依据删掉，审计必须在删之前取快照 ----------
INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
SELECT 1, 'calibration_revoke', 'channel', c.id,
       jsonb_build_object(
           'calibrationId', cr.id, 'method', cr.method,
           'kL', cr.k_l, 'refTimeNs', cr.ref_time_ns,
           'measurementsRelinked', (SELECT count(*) FROM measurements m WHERE m.calibration_id = cr.id)),
       jsonb_build_object('calibrationId', NULL, 'kL', NULL, 'boltState', 'uncalibrated'),
       '[运维直改] 清理仿真基线标定（' || cr.method || '/' || COALESCE(cr.fitter_version, '?')
           || '）：设备模拟器已整体移除，其自动写入的假基线（ref_time=26000/k_l=0.003）不能留在真探头通道上。'
           || '通道回未标定，须按 calibration-field-guide 重新置零并标定；真实测量历史保留不动',
       now()
FROM calibration_records cr
JOIN bolts b ON b.id = cr.bolt_id
JOIN channels c ON c.id = b.channel_id
WHERE cr.method = 'sim_baseline';

-- ---------- 2. 测量：只解开假标定的关联，不删数据 ----------
-- 这些行是真探头的回波与温度历史（第 0 段的声时分布已证），preload_n 本来就是 NULL。
-- 删掉等于毁真实证据；留着不动又会有悬空 calibration_id。所以置 NULL 并打标记，
-- 让事后追溯能看出"这段的基线曾经是假的，需要重新标定后才有预紧力"。
UPDATE measurements SET
    calibration_id = NULL,
    algo_version = 'phase1:raw:sim_baseline_revoked'
WHERE calibration_id IN (SELECT id FROM calibration_records WHERE method = 'sim_baseline');

-- ---------- 3. 解开引用，再删标定记录本身 ----------
-- 3a. channels.active_calibration_id 有外键（fk_channels_active_calib），必须先清列
WITH cleared AS (
    UPDATE channels SET
        k_l = NULL, k_l_uncertainty = NULL, k_l_intercept_ns = NULL,
        ref_time_ns = NULL, ref_temp_c = NULL,
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
SELECT 'sim_baseline 残留（应为 0）' AS what, count(*)::text AS val
FROM calibration_records WHERE method = 'sim_baseline'
UNION ALL
SELECT 'fitter_version=sim:1.0 残留（应为 0）', count(*)::text
FROM calibration_records WHERE fitter_version = 'sim:1.0'
UNION ALL
SELECT 'measurements 悬空 calibration_id（见头部说明 e，可能有本脚本之外的历史遗留）', count(*)::text
FROM measurements m
WHERE m.calibration_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM calibration_records cr WHERE cr.id = m.calibration_id)
UNION ALL
SELECT '已打 sim_baseline_revoked 标记的测量', count(*)::text
FROM measurements WHERE algo_version = 'phase1:raw:sim_baseline_revoked';

-- 通道现状：被清的通道应为 uncalibrated 且 k_l/ref_time 为空，其余不受影响
SELECT c.channel_no, b.code, b.state, c.meas_mode, c.k_l, c.ref_time_ns,
       c.active_calibration_id IS NOT NULL AS has_active
FROM channels c LEFT JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
WHERE c.device_id = 1 AND c.deleted_at IS NULL
ORDER BY c.channel_no;

-- ---------- 5. 补扫（先重置路由缓存，再跑这一段） ----------
-- 5a. 主事务执行窗口内，在测通道按旧缓存写入的悬空行：解关联打同一标记。
--     限最近两天：新行都在未压缩区，且避免 NOT EXISTS 谓词把全表压缩段都解开。
BEGIN;
SET LOCAL timescaledb.max_tuples_decompressed_per_dml_transaction = 0;

UPDATE measurements SET
    calibration_id = NULL,
    algo_version = 'phase1:raw:sim_baseline_revoked'
WHERE ts >= now() - interval '2 days'
  AND calibration_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM calibration_records cr WHERE cr.id = measurements.calibration_id);

-- 5b. 残值兜底：列值恰为 sim 常数三元组（k_l=0.003 / ref=26000.00）但 active 指针早已断开的
--     通道（本机的 CH5 就是这样），第 3a 段按指针圈选扫不到。假基线残值与挂着假标定同级危险。
--     谓词钉死在 sim 常数上，不会误伤真实标定（真实 k_l 量级 1e-3 但不会恰好等于 0.003000…
--     且 ref 恰好 26000.00）。
WITH cleared AS (
    UPDATE channels SET
        k_l = NULL, k_l_uncertainty = NULL, k_l_intercept_ns = NULL,
        ref_time_ns = NULL, ref_temp_c = NULL, updated_at = now()
    WHERE deleted_at IS NULL
      AND active_calibration_id IS NULL
      AND k_l = 0.003 AND ref_time_ns = 26000.00
    RETURNING id, channel_no
)
INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
SELECT 1, 'calibration_revoke', 'channel', c.id,
       jsonb_build_object('kL', 0.003, 'refTimeNs', 26000.00),
       jsonb_build_object('kL', NULL, 'refTimeNs', NULL),
       '[运维直改] 清除通道 ' || c.channel_no || ' 的仿真基线残值（k_l=0.003/ref=26000，active 指针已断）：与挂着假标定同级危险',
       now()
FROM cleared c;

COMMIT;

-- 5c. 复核：与 sim 相关的悬空与残值应双双归零
SELECT '近两天悬空 calibration_id（应为 0）' AS what, count(*)::text AS val
FROM measurements m
WHERE m.ts >= now() - interval '2 days' AND m.calibration_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM calibration_records cr WHERE cr.id = m.calibration_id)
UNION ALL
SELECT '残留 sim 常数的通道（应为 0）', count(*)::text
FROM channels WHERE (k_l = 0.003 OR ref_time_ns = 26000.00) AND deleted_at IS NULL;
