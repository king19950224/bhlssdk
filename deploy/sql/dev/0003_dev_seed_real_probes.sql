-- ============================================================================
-- 固化"实验台真机 DEV_LAB_001 (@192.168.0.7:23)"已调通的 8 路探头。
-- 依赖 0001_dev_seed（建 device/40 通道）+ 0002_dev_seed_assets（建 WT-01）。
-- 目的：全新库 docker initdb 重部署后，这 8 路开箱即处于"已调通"状态（通道窗口/增益/基线
--       + 干净命名的螺栓 + 绑定），并清掉 0001/0002 的占位螺栓，使 UI 只剩真实探头。
-- 仅开发挂载，**不要在生产部署**。全部 ON CONFLICT / 条件 UPDATE，幂等。
--
-- 注意：ref_time_ns / ref_temp_c 是"调试当时"的零载基线快照，真机重新部署时应按当时
--       声时用 UI「设当前为基线」重置；此处仅为可复现的起点。
--       螺栓规格（target/min 预紧力、有效长度等）为占位，待现场提供真实规格后经审批更新。
-- ============================================================================

-- 1) 专用监测组：实验台法兰（挂到 WT-01）
INSERT INTO monitor_groups (project_id, turbine_id, code, name, structure_type, expected_bolt_count, target_preload_n)
SELECT p.id, t.id, 'LAB_FLANGE', '实验台法兰（8探头）', 'flange', 8, 100000
  FROM projects p JOIN turbines t ON t.project_id = p.id AND t.code = 'WT-01'
 WHERE p.code = 'DEV_TEST'
ON CONFLICT (project_id, code) DO NOTHING;

-- 2) 8 路通道调试参数（按 device+channel_no 自然键 upsert）
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

-- 3) 先软删实验台设备上所有"非 LAB-CH"螺栓（0001/0002 占位 + 历史手工螺栓），
--    腾出 ux_bolts_active_channel（每通道仅一活跃螺栓）后再插入。
UPDATE bolts b SET deleted_at = now(), enabled = false, updated_at = now()
FROM channels c JOIN devices d ON d.id = c.device_id
WHERE b.channel_id = c.id AND d.code = 'DEV_LAB_001'
  AND b.code NOT LIKE 'LAB-CH%' AND b.deleted_at IS NULL;

-- 4) 8 个真实螺栓（规格为占位，待现场真实规格更新）。target/min 预紧力留空（未标定）。
WITH grp AS (SELECT id FROM monitor_groups WHERE code = 'LAB_FLANGE'),
     spec(channel_no, nominal, csa) AS (VALUES
        ( 1::smallint, 'M20', 245::numeric),
        ( 2,           'M16', 157),
        ( 7,           'M20', 245),
        ( 8,           'M20', 245),
        (11,           'M16', 157),
        (14,           'M20', 245),
        (17,           'M20', 245),
        (20,           'M20', 245)
     ),
     chs AS (SELECT c.id ch_id, c.channel_no FROM channels c
               JOIN devices d ON d.id = c.device_id WHERE d.code = 'DEV_LAB_001')
INSERT INTO bolts (group_id, channel_id, code, nominal_diam, grade, rp02_mpa, material,
    elastic_modulus_gpa, effective_length_mm, total_length_mm, cross_section_mm2,
    sound_velocity_ms, target_preload_n, min_preload_n, gb_compliant, state, enabled)
SELECT grp.id, chs.ch_id, 'LAB-CH' || lpad(spec.channel_no::text, 2, '0'),
    spec.nominal, '8.8', 640, '42CrMo', 206, 200, 220, spec.csa,
    5890, NULL, NULL, true, 'uncalibrated', true
  FROM grp CROSS JOIN spec JOIN chs ON chs.channel_no = spec.channel_no
ON CONFLICT (group_id, code) DO NOTHING;

DO $$ BEGIN RAISE NOTICE 'dev/0003_dev_seed_real_probes.sql 完成（实验台 8 路真机探头，仅开发用）'; END $$;
