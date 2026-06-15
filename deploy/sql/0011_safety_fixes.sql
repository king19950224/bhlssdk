-- ============================================================================
-- 0011_safety_fixes.sql
-- 合并多条安全 / 数据完整性补丁，对应深度审查报告中的 C5/C6/C8/H13/M6/M10 等。
-- 每条都用 IF NOT EXISTS / DO $$ ... $$ 包裹，幂等可重复跑。
-- ============================================================================

-- ----------------------------------------------------------------------------
-- C6: alarms 同 (bolt_id, type) 同时只允许一条开启告警 — 数据库强约束
-- 之前注释里写"应用层保证"，跨重启 / 并发会失守。改用 partial unique index。
-- ----------------------------------------------------------------------------
CREATE UNIQUE INDEX IF NOT EXISTS ux_alarms_open
    ON alarms(bolt_id, type)
    WHERE cleared_at IS NULL;

-- ----------------------------------------------------------------------------
-- C6 续：severity 加 CHECK 防业务侧拼错。允许集合保持开放，便于后续扩展。
-- ----------------------------------------------------------------------------
ALTER TABLE alarms
    DROP CONSTRAINT IF EXISTS chk_alarms_severity,
    ADD CONSTRAINT chk_alarms_severity
        CHECK (severity IN ('info','warn','critical'));

-- ----------------------------------------------------------------------------
-- M6: alarms 加 last_active_at 字段（告警持续期间最后一次命中条件的时间）
-- ----------------------------------------------------------------------------
ALTER TABLE alarms
    ADD COLUMN IF NOT EXISTS last_active_at timestamptz;

-- ----------------------------------------------------------------------------
-- M6 续：alarm_shelvings 维护抑制表，维护时段操作员能把规则关掉，过期自动解
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS alarm_shelvings (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    bolt_id         bigint REFERENCES bolts(id),
    alarm_type      varchar(32),
    shelved_by      bigint NOT NULL REFERENCES users(id),
    shelved_at      timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz NOT NULL,
    reason          text NOT NULL,
    cancelled_at    timestamptz,
    cancelled_by    bigint REFERENCES users(id),
    CHECK (expires_at > shelved_at)
);

CREATE INDEX IF NOT EXISTS idx_alarm_shelvings_active
    ON alarm_shelvings(bolt_id, alarm_type, expires_at)
    WHERE cancelled_at IS NULL;

COMMENT ON TABLE alarm_shelvings IS '告警抑制：维护时段操作员临时屏蔽某 bolt / 某 type 的告警，过期自动解除';

-- ----------------------------------------------------------------------------
-- C8: bolts 表加温度系数列（按材料标准值；缺则 evaluator 退化到 SDK 默认）
-- ----------------------------------------------------------------------------
ALTER TABLE bolts
    ADD COLUMN IF NOT EXISTS temp_coefficient_per_c numeric(12,10);

COMMENT ON COLUMN bolts.temp_coefficient_per_c IS
    '温度系数（1/°C）。按螺栓材料标准值；NULL 时业务侧退化到 SDK 默认 1.0269e-4。';

-- ----------------------------------------------------------------------------
-- H13: measurements 错位索引 — status_code < 0 的不入 measurements 而是 measurement_anomalies。
-- 该 partial index 几乎用不上，drop 掉省空间。
-- ----------------------------------------------------------------------------
DROP INDEX IF EXISTS idx_meas_status;

-- ----------------------------------------------------------------------------
-- H13 续：measurement_anomalies 补压缩 + retention（与 measurements 对齐，避免无限增长）
-- ----------------------------------------------------------------------------
DO $$
BEGIN
    BEGIN
        ALTER TABLE measurement_anomalies SET (
            timescaledb.compress,
            timescaledb.compress_segmentby = 'bolt_id',
            timescaledb.compress_orderby   = 'ts DESC'
        );
        PERFORM add_compression_policy('measurement_anomalies', INTERVAL '30 days', if_not_exists => true);
    EXCEPTION WHEN OTHERS THEN
        RAISE NOTICE 'measurement_anomalies 压缩策略已存在或 TimescaleDB 不可用 - 跳过';
    END;

    BEGIN
        PERFORM add_retention_policy('measurement_anomalies', INTERVAL '2 years', if_not_exists => true);
    EXCEPTION WHEN OTHERS THEN
        RAISE NOTICE 'measurement_anomalies 保留策略已存在 - 跳过';
    END;
END $$;

-- ----------------------------------------------------------------------------
-- H13 续：channels (device_id, channel_no) 唯一约束没加 WHERE deleted_at IS NULL，
-- 通道软删后无法重建同号 channel。改成 partial unique index。
-- 历史 UNIQUE 约束保留（drop 需删 FK 关联），新增的 partial 索引覆盖软删情况。
-- ----------------------------------------------------------------------------
DO $$
BEGIN
    -- 如果原 UNIQUE 约束存在，drop 后重建
    IF EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'channels_device_id_channel_no_key'
    ) THEN
        ALTER TABLE channels DROP CONSTRAINT channels_device_id_channel_no_key;
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_channels_active_device_channel
    ON channels(device_id, channel_no)
    WHERE deleted_at IS NULL;

-- ----------------------------------------------------------------------------
-- H13 续：audit_log 外键改 SET NULL，否则删用户被 FK 卡住（审计日志保留，关系解绑）
-- ----------------------------------------------------------------------------
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_constraint c
        JOIN pg_class t ON c.conrelid = t.oid
        WHERE t.relname = 'audit_log' AND c.conname LIKE '%user_id_fkey'
    ) THEN
        ALTER TABLE audit_log DROP CONSTRAINT audit_log_user_id_fkey;
    END IF;
END $$;

ALTER TABLE audit_log
    ADD CONSTRAINT audit_log_user_id_fkey
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE SET NULL;

-- ----------------------------------------------------------------------------
-- C1: 用户表加角色（最小三档：operator / reviewer / admin），用于 JWT claim
-- ----------------------------------------------------------------------------
ALTER TABLE users
    ADD COLUMN IF NOT EXISTS role varchar(16) NOT NULL DEFAULT 'operator';

ALTER TABLE users
    DROP CONSTRAINT IF EXISTS chk_users_role,
    ADD CONSTRAINT chk_users_role
        CHECK (role IN ('operator','reviewer','admin'));

-- 老 placeholder admin（无效 bcrypt 占位）置 disabled，强制重新种子
UPDATE users
   SET enabled = false,
       password_hash = ''
 WHERE username = 'admin'
   AND password_hash = '$2a$11$placeholder_replace_in_phase_2';

-- ----------------------------------------------------------------------------
-- M10: reports 加 sha256 + file_bytes 防篡改字段
-- ----------------------------------------------------------------------------
ALTER TABLE reports
    ADD COLUMN IF NOT EXISTS file_sha256 char(64),
    ADD COLUMN IF NOT EXISTS file_bytes  bigint;

-- ----------------------------------------------------------------------------
-- 完成
-- ----------------------------------------------------------------------------
DO $$ BEGIN RAISE NOTICE '0011_safety_fixes.sql 完成'; END $$;
