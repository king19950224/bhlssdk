-- ============================================================================
-- 开发用资产示例数据：2 台风机 + 多部位 + 螺栓，撑起资产树演示。
-- 依赖 0018_turbines + 0001_dev_seed。仅开发挂载，**不要在生产部署**。
-- 幂等：全部 ON CONFLICT / 条件 UPDATE。
-- ============================================================================

INSERT INTO turbines (project_id, code, name, model, capacity_kw, location)
SELECT id, 'WT-01', '1号风机', 'GW155-4.5MW', 4500, 'A区-01' FROM projects WHERE code = 'DEV_TEST'
ON CONFLICT (project_id, code) DO NOTHING;

INSERT INTO turbines (project_id, code, name, model, capacity_kw, location)
SELECT id, 'WT-02', '2号风机', 'GW155-4.5MW', 4500, 'A区-02' FROM projects WHERE code = 'DEV_TEST'
ON CONFLICT (project_id, code) DO NOTHING;

-- 现有测试监测组挂到 WT-01，更名为塔筒顶法兰
UPDATE monitor_groups mg
SET turbine_id = t.id, name = '塔筒顶法兰', structure_type = 'flange'
FROM turbines t
WHERE mg.code = 'TEST_GROUP_01' AND t.code = 'WT-01' AND t.project_id = mg.project_id;

-- WT-01 叶根法兰
INSERT INTO monitor_groups (project_id, turbine_id, code, name, structure_type, expected_bolt_count, target_preload_n)
SELECT p.id, t.id, 'WT01_BLADE', '叶根法兰', 'flange', 3, 120000
  FROM projects p JOIN turbines t ON t.project_id = p.id AND t.code = 'WT-01'
 WHERE p.code = 'DEV_TEST'
ON CONFLICT (project_id, code) DO NOTHING;

-- WT-02 塔筒顶法兰
INSERT INTO monitor_groups (project_id, turbine_id, code, name, structure_type, expected_bolt_count, target_preload_n)
SELECT p.id, t.id, 'WT02_TOWER', '塔筒顶法兰', 'flange', 3, 100000
  FROM projects p JOIN turbines t ON t.project_id = p.id AND t.code = 'WT-02'
 WHERE p.code = 'DEV_TEST'
ON CONFLICT (project_id, code) DO NOTHING;

-- 叶根法兰螺栓（通道 4-6, M20/10.9）
WITH grp AS (SELECT id FROM monitor_groups WHERE code = 'WT01_BLADE'),
     chs AS (SELECT c.id ch_id, c.channel_no FROM channels c JOIN devices d ON d.id = c.device_id
             WHERE d.code = 'DEV_LAB_001' AND c.channel_no IN (4, 5, 6))
INSERT INTO bolts (group_id, channel_id, code, nominal_diam, grade, rp02_mpa, material,
    elastic_modulus_gpa, effective_length_mm, total_length_mm, cross_section_mm2,
    sound_velocity_ms, target_preload_n, min_preload_n, gb_compliant, state)
SELECT grp.id, chs.ch_id, 'M20-' || lpad(chs.channel_no::text, 3, '0'), 'M20', '10.9', 940, '42CrMo',
    206, 90, 120, 245, 5890, 120000, 96000, true, 'uncalibrated'
  FROM grp CROSS JOIN chs
ON CONFLICT (group_id, code) DO NOTHING;

-- WT-02 塔筒顶法兰螺栓（通道 7-9, M16/8.8）
WITH grp AS (SELECT id FROM monitor_groups WHERE code = 'WT02_TOWER'),
     chs AS (SELECT c.id ch_id, c.channel_no FROM channels c JOIN devices d ON d.id = c.device_id
             WHERE d.code = 'DEV_LAB_001' AND c.channel_no IN (7, 8, 9))
INSERT INTO bolts (group_id, channel_id, code, nominal_diam, grade, rp02_mpa, material,
    elastic_modulus_gpa, effective_length_mm, total_length_mm, cross_section_mm2,
    sound_velocity_ms, target_preload_n, min_preload_n, gb_compliant, state)
SELECT grp.id, chs.ch_id, 'M16-' || lpad(chs.channel_no::text, 3, '0'), 'M16', '8.8', 640, '42CrMo',
    210, 80, 100, 201, 5890, 100000, 80000, true, 'uncalibrated'
  FROM grp CROSS JOIN chs
ON CONFLICT (group_id, code) DO NOTHING;

-- 清理空的默认机组（部位已挂到具名风机后）
UPDATE turbines SET deleted_at = now()
WHERE code = 'DEFAULT' AND deleted_at IS NULL
  AND NOT EXISTS (SELECT 1 FROM monitor_groups mg WHERE mg.turbine_id = turbines.id AND mg.deleted_at IS NULL);

DO $$ BEGIN RAISE NOTICE 'dev/0002_dev_seed_assets.sql 完成（仅开发用）'; END $$;
