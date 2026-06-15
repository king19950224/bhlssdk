-- ============================================================================
-- 开发种子数据。仅本地 docker-compose 用，**不要在生产部署**。
-- docker-compose.yml 把 deploy/sql/dev 单独挂进 initdb 才会生效（默认不挂）。
-- ============================================================================

INSERT INTO projects (code, name, industry, location, sla_level)
VALUES ('DEV_TEST', '开发测试项目', 'test', '本地', 'standard')
ON CONFLICT (code) DO NOTHING;

INSERT INTO monitor_groups (project_id, code, name, structure_type, expected_bolt_count, target_preload_n)
SELECT id, 'TEST_GROUP_01', '测试监测组', 'flange', 40, 100000
  FROM projects WHERE code = 'DEV_TEST'
ON CONFLICT (project_id, code) DO NOTHING;

INSERT INTO devices (project_id, code, model, comm_type, comm_params, address, transfer_mode, state)
SELECT id, 'DEV_LAB_001', 3, 1, '192.168.0.7:23', 1, 1, 'unknown'
  FROM projects WHERE code = 'DEV_TEST'
ON CONFLICT (project_id, code) DO NOTHING;

WITH dev AS (SELECT id AS dev_id FROM devices WHERE code = 'DEV_LAB_001')
INSERT INTO channels (device_id, channel_no, probe_freq_mhz, gain, state, sampling_enabled, business_state)
SELECT dev.dev_id, ch::smallint, 2.0, 10, 'manual', true, 'unbound'
  FROM dev CROSS JOIN generate_series(1, 40) AS v(ch)
ON CONFLICT DO NOTHING;

WITH grp AS (SELECT id AS grp_id FROM monitor_groups WHERE code = 'TEST_GROUP_01'),
     chs AS (
        SELECT c.id AS ch_id, c.channel_no FROM channels c
          JOIN devices d ON d.id = c.device_id
         WHERE d.code = 'DEV_LAB_001'
           AND c.channel_no IN (1, 2, 3)
     )
INSERT INTO bolts (
    group_id, channel_id, code, nominal_diam, grade, rp02_mpa, material,
    elastic_modulus_gpa, effective_length_mm, total_length_mm, cross_section_mm2,
    sound_velocity_ms, target_preload_n, min_preload_n, gb_compliant, state)
SELECT
    grp.grp_id, chs.ch_id,
    'M16-' || lpad(chs.channel_no::text, 3, '0'),
    'M16', '8.8', 640, '42CrMo',
    210, 80, 100, 201, 5890, 100000, 80000, true, 'uncalibrated'
  FROM grp CROSS JOIN chs
ON CONFLICT (group_id, code) DO NOTHING;

-- admin 账号由 Program.cs 启动自举创建（开发模式下 password 默认 admin123，
-- 用 ASP.NET Identity PasswordHasher 写入；生产模式启动不自举）。

DO $$ BEGIN RAISE NOTICE 'dev/0001_dev_seed.sql 完成（仅开发用）'; END $$;
