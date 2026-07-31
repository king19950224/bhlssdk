-- ============================================================================
-- 开发种子数据。仅本地 docker-compose 用，**不要在生产部署**。
-- docker-compose.yml 把 deploy/sql/dev 单独挂进 initdb 才会生效（默认不挂）。
--
-- 检测即螺栓：种子只建项目 / 监测组 / 设备 / 通道目录，不预插螺栓档案。
-- 螺栓由探头在位自动建档产生（设备 default_group_id 指向 TEST_GROUP_01）。
-- ============================================================================

INSERT INTO projects (code, name, industry, location, sla_level)
VALUES ('DEV_TEST', '开发测试项目', 'test', '本地', 'standard')
ON CONFLICT (code) DO NOTHING;

INSERT INTO monitor_groups (project_id, code, name, structure_type, expected_bolt_count, target_preload_n)
SELECT id, 'TEST_GROUP_01', '塔筒顶法兰', 'flange', 48, 100000
  FROM projects WHERE code = 'DEV_TEST'
ON CONFLICT (project_id, code) DO NOTHING;

-- 实验台真机为 48 通道设备（SDK model=3 多通道监测协议单板上限 48）
INSERT INTO devices (project_id, code, model, comm_type, comm_params, address, transfer_mode, channel_count, state)
SELECT id, 'DEV_LAB_001', 3, 1, '192.168.0.7:23', 1, 1, 48, 'unknown'
  FROM projects WHERE code = 'DEV_TEST'
ON CONFLICT (project_id, code) DO NOTHING;

-- 通道数对齐 + 默认监测组（自动建档归属）
UPDATE devices d
   SET channel_count = 48,
       default_group_id = g.id,
       updated_at = now()
  FROM monitor_groups g
 WHERE d.code = 'DEV_LAB_001'
   AND g.code = 'TEST_GROUP_01'
   AND g.project_id = d.project_id
   AND (d.channel_count <> 48 OR d.default_group_id IS DISTINCT FROM g.id);

WITH dev AS (SELECT id AS dev_id FROM devices WHERE code = 'DEV_LAB_001')
INSERT INTO channels (device_id, channel_no, probe_freq_mhz, gain, state, sampling_enabled, business_state)
SELECT dev.dev_id, ch::smallint, 2.0, 10, 'manual', true, 'unbound'
  FROM dev CROSS JOIN generate_series(1, 48) AS v(ch)
ON CONFLICT DO NOTHING;

-- admin 账号由 Program.cs 启动自举创建（开发模式下 password 默认 admin123，
-- 用 ASP.NET Identity PasswordHasher 写入；生产模式启动不自举）。

DO $$ BEGIN RAISE NOTICE 'dev/0001_dev_seed.sql 完成（检测即螺栓基线：无预插螺栓，仅开发用）'; END $$;
