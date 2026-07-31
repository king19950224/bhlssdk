-- ============================================================================
-- 开发用资产树：2 台风机 + 多监测组（法兰部位）。
-- 依赖 0018_turbines + 0001_dev_seed。仅开发挂载，**不要在生产部署**。
-- 幂等：全部 ON CONFLICT / 条件 UPDATE。
--
-- 检测即螺栓：本脚本不预插螺栓——档案由探头在位自动建档（或点位表导入）产生。
-- ============================================================================

INSERT INTO turbines (project_id, code, name, model, capacity_kw, location)
SELECT id, 'WT-01', '1号风机', 'GW155-4.5MW', 4500, 'A区-01' FROM projects WHERE code = 'DEV_TEST'
ON CONFLICT (project_id, code) DO NOTHING;

INSERT INTO turbines (project_id, code, name, model, capacity_kw, location)
SELECT id, 'WT-02', '2号风机', 'GW155-4.5MW', 4500, 'A区-02' FROM projects WHERE code = 'DEV_TEST'
ON CONFLICT (project_id, code) DO NOTHING;

-- 现有测试监测组挂到 WT-01
UPDATE monitor_groups mg
SET turbine_id = t.id, name = '塔筒顶法兰', structure_type = 'flange',
    expected_bolt_count = COALESCE(mg.expected_bolt_count, 48)
FROM turbines t
WHERE mg.code = 'TEST_GROUP_01' AND t.code = 'WT-01' AND t.project_id = mg.project_id;

-- WT-01 叶根法兰（孔位配置示例；螺栓不预插）
INSERT INTO monitor_groups (project_id, turbine_id, code, name, structure_type, expected_bolt_count, target_preload_n)
SELECT p.id, t.id, 'WT01_BLADE', '叶根法兰', 'flange', 48, 120000
  FROM projects p JOIN turbines t ON t.project_id = p.id AND t.code = 'WT-01'
 WHERE p.code = 'DEV_TEST'
ON CONFLICT (project_id, code) DO NOTHING;

-- WT-02 塔筒顶法兰
INSERT INTO monitor_groups (project_id, turbine_id, code, name, structure_type, expected_bolt_count, target_preload_n)
SELECT p.id, t.id, 'WT02_TOWER', '塔筒顶法兰', 'flange', 48, 100000
  FROM projects p JOIN turbines t ON t.project_id = p.id AND t.code = 'WT-02'
 WHERE p.code = 'DEV_TEST'
ON CONFLICT (project_id, code) DO NOTHING;

-- 清理空的默认机组（部位已挂到具名风机后）
UPDATE turbines SET deleted_at = now()
WHERE code = 'DEFAULT' AND deleted_at IS NULL
  AND NOT EXISTS (SELECT 1 FROM monitor_groups mg WHERE mg.turbine_id = turbines.id AND mg.deleted_at IS NULL);

DO $$ BEGIN RAISE NOTICE 'dev/0002_dev_seed_assets.sql 完成（资产树，无预插螺栓，仅开发用）'; END $$;
