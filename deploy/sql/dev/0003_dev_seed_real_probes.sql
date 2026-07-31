-- ============================================================================
-- 固化实验台真机 DEV_LAB_001 (@192.168.0.7:23) 若干通道的调试参数（窗口/增益/基线）。
-- 依赖 0001_dev_seed + 0002_dev_seed_assets。仅开发挂载，**不要在生产部署**。
--
-- 检测即螺栓：本脚本只写通道硬件参数，不预插螺栓档案。
-- 插上探头后由自动建档生成螺栓；规格/目标值现场补全或点位表导入。
--
-- 注意：ref_time_ns / ref_temp_c 是"调试当时"的零载基线快照，真机重新部署时应按当时
--       声时用 UI「设当前为基线」重置；此处仅为可复现的起点。
-- ============================================================================

-- 若干通道调试参数（按 device+channel_no 自然键 upsert）
--    gain16 / threshold-490 / echo_win 30µs / measMode0(自动峰) / probe 2MHz / tc_mode1
WITH cfg(channel_no, ref_time, ref_temp) AS (VALUES
    ( 1::smallint, 32558::numeric, 15.30::numeric),
    ( 2,           32558,          15.30),
    ( 7,           32262,          28.68),
    ( 8,           32293,          28.29),
    (11,           32558,          15.30),
    (14,           32558,          15.30),
    (17,           32558,          15.30),
    (20,           32558,          15.30)
)
UPDATE channels c SET
    gain = 16, threshold = -490, echo_win_ns = 30000, meas_mode = 0,
    probe_freq_mhz = 2.0, tc_mode = 1,
    ref_time_ns = cfg.ref_time, ref_temp_c = cfg.ref_temp,
    state = 'manual', sampling_enabled = true, updated_at = now()
FROM cfg, devices d
WHERE c.device_id = d.id AND d.code = 'DEV_LAB_001' AND c.channel_no = cfg.channel_no;

-- 确保默认监测组仍指向塔筒顶法兰（自动建档归属）
UPDATE devices d
   SET default_group_id = g.id, updated_at = now()
  FROM monitor_groups g
 WHERE d.code = 'DEV_LAB_001'
   AND g.code = 'TEST_GROUP_01'
   AND g.project_id = d.project_id
   AND d.default_group_id IS DISTINCT FROM g.id;

DO $$ BEGIN RAISE NOTICE 'dev/0003_dev_seed_real_probes.sql 完成（通道参数预置，螺栓由在位自动建档，仅开发用）'; END $$;
