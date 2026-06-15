-- ============================================================================
-- BoltPreload 初始数据库 schema
-- 对应方案附录 D
-- 商用关键基础设施监测系统，所有表设计必须可追溯
-- ============================================================================

-- 启用 TimescaleDB 扩展
CREATE EXTENSION IF NOT EXISTS timescaledb;

-- ============================================================================
-- 业务表
-- ============================================================================

-- 项目（多租户边界）
CREATE TABLE projects (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code            varchar(32) UNIQUE NOT NULL,
    name            varchar(128) NOT NULL,
    industry        varchar(32) NOT NULL,
    location        varchar(256),
    owner_company   varchar(256),
    contract_no     varchar(64),
    sla_level       varchar(16) NOT NULL DEFAULT 'standard',
    enabled         boolean NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    deleted_at      timestamptz
);
COMMENT ON TABLE projects IS '项目；多租户边界。如风电场、桥梁工程';

-- 监测组
CREATE TABLE monitor_groups (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id          bigint NOT NULL REFERENCES projects(id),
    code                varchar(64) NOT NULL,
    name                varchar(128) NOT NULL,
    structure_type      varchar(32),
    expected_bolt_count smallint,
    target_preload_n    numeric(12,2),
    enabled             boolean NOT NULL DEFAULT true,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    deleted_at          timestamptz,
    UNIQUE (project_id, code)
);
CREATE INDEX idx_groups_project ON monitor_groups(project_id) WHERE deleted_at IS NULL;
COMMENT ON TABLE monitor_groups IS '监测组；业务分组（如某法兰的 12 颗螺栓为一组）';

-- 设备（终端）
CREATE TABLE devices (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id          bigint NOT NULL REFERENCES projects(id),
    code                varchar(64) NOT NULL,
    model               smallint NOT NULL,                    -- SDK model: 0/1/2/3
    comm_type           smallint NOT NULL,                    -- 0 串口/1 TCPClient/2 TCPServer
    comm_params         varchar(128) NOT NULL,                -- "192.168.0.7:23"
    address             smallint NOT NULL DEFAULT 1,
    transfer_mode       smallint NOT NULL DEFAULT 1,          -- 0 LoRa / 1 直连
    channel_count       smallint NOT NULL DEFAULT 40,
    enabled             boolean NOT NULL DEFAULT true,
    calib_cert_no       varchar(64),
    calib_date          date,
    next_calib_date     date,
    calib_institution   varchar(128),
    last_seen_at        timestamptz,
    state               varchar(16) NOT NULL DEFAULT 'unknown',
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    deleted_at          timestamptz,
    UNIQUE (project_id, code)
);
CREATE INDEX idx_devices_project ON devices(project_id) WHERE deleted_at IS NULL;
CREATE INDEX idx_devices_state ON devices(state) WHERE deleted_at IS NULL AND enabled;
COMMENT ON TABLE devices IS '物理终端设备，对应 SDK sessionId';

-- 通道
CREATE TABLE channels (
    id                      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    device_id               bigint NOT NULL REFERENCES devices(id),
    channel_no              smallint NOT NULL,
    probe_freq_mhz          numeric(5,2) NOT NULL DEFAULT 2.0,
    gain                    real NOT NULL DEFAULT 10,
    threshold               smallint NOT NULL DEFAULT 0,
    echo_win_ns             numeric(12,2),
    echo_width_us           smallint NOT NULL DEFAULT 15,
    pulse_voltage           smallint NOT NULL DEFAULT 50,
    pulse_forward           smallint NOT NULL DEFAULT 3,
    pulse_reverse           smallint NOT NULL DEFAULT 0,
    meas_mode               smallint NOT NULL DEFAULT 2,        -- 0 自动/1 位置/2 阈值
    tc_mode                 smallint NOT NULL DEFAULT 1,        -- 0 不补/1 材料/2 自定义
    temp_coefficient        numeric(12,10),                     -- 自定义温度系数
    ref_time_ns             numeric(12,2),                      -- 参考声时
    ref_temp_c              numeric(6,2),                       -- 参考温度
    k_l                     numeric(20,15),                     -- 当前生效的载荷因子（冗余自标定档案）
    k_l_uncertainty         numeric(20,15),                     -- 当前 K_L 扩展不确定度
    active_calibration_id   uuid,                               -- 指向当前生效的标定档案
    template_wave_url       varchar(512),
    tof_algorithm           varchar(32) NOT NULL DEFAULT 'ThrSignal',
    enabled                 boolean NOT NULL DEFAULT true,
    sampling_enabled        boolean NOT NULL DEFAULT true,
    hardware_state          varchar(32) NOT NULL DEFAULT 'unknown',
    business_state          varchar(32) NOT NULL DEFAULT 'unbound',
    config_source           varchar(32) NOT NULL DEFAULT 'database',
    config_synced_at        timestamptz,
    discovered_at           timestamptz NOT NULL DEFAULT now(),
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    deleted_at              timestamptz,
    UNIQUE (device_id, channel_no)
);
CREATE INDEX idx_channels_device ON channels(device_id) WHERE deleted_at IS NULL;
COMMENT ON TABLE channels IS '物理通道；K_L/标定参数从最新通过的标定档案拷贝过来，避免热路径 JOIN';

-- 螺栓
CREATE TABLE bolts (
    id                      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    group_id                bigint NOT NULL REFERENCES monitor_groups(id),
    channel_id              bigint NOT NULL REFERENCES channels(id),
    code                    varchar(64) NOT NULL,
    nominal_diam            varchar(8) NOT NULL,                -- "M16"
    grade                   varchar(8) NOT NULL,                -- "8.8"
    rp02_mpa                numeric(8,2) NOT NULL,
    material                varchar(32) NOT NULL,               -- "42CrMo"
    elastic_modulus_gpa     numeric(6,2) NOT NULL,
    effective_length_mm     numeric(8,2) NOT NULL,
    grip_length_mm          numeric(8,2),
    total_length_mm         numeric(8,2) NOT NULL,
    cross_section_mm2       numeric(8,2) NOT NULL,
    sound_velocity_ms       numeric(8,2) NOT NULL DEFAULT 5890,
    install_torque_nm       numeric(8,2),
    target_preload_n        numeric(12,2),
    min_preload_n           numeric(12,2),
    position_label          varchar(64),
    install_date            date,
    gb_compliant            boolean NOT NULL DEFAULT true,      -- 是否符合 GB/T 43232 适用范围
    state                   varchar(16) NOT NULL DEFAULT 'uncalibrated',
    enabled                 boolean NOT NULL DEFAULT true,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    deleted_at              timestamptz,
    UNIQUE (group_id, code)
);
CREATE INDEX idx_bolts_group ON bolts(group_id) WHERE deleted_at IS NULL;
CREATE INDEX idx_bolts_channel ON bolts(channel_id) WHERE deleted_at IS NULL;
CREATE UNIQUE INDEX ux_bolts_active_channel ON bolts(channel_id) WHERE deleted_at IS NULL;
COMMENT ON TABLE bolts IS '业务实体螺栓；1:1 绑定 Channel';

-- 用户（精简版，Phase 1 用）
CREATE TABLE users (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    username        varchar(64) UNIQUE NOT NULL,
    full_name       varchar(128),
    email           varchar(256),
    phone           varchar(32),
    password_hash   varchar(256) NOT NULL,
    failed_attempts smallint NOT NULL DEFAULT 0,
    locked_until    timestamptz,
    last_login_at   timestamptz,
    enabled         boolean NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    deleted_at      timestamptz
);

-- 审计日志（append-only，永久保留）
CREATE TABLE audit_log (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id         bigint REFERENCES users(id),
    ip_addr         inet,
    action          varchar(64) NOT NULL,
    target_type     varchar(32),
    target_id       bigint,
    before_jsonb    jsonb,
    after_jsonb     jsonb,
    note            text,
    at              timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_audit_user ON audit_log(user_id, at DESC);
CREATE INDEX idx_audit_action ON audit_log(action, at DESC);
COMMENT ON TABLE audit_log IS 'append-only 审计日志，永久保留';

-- 通道业务绑定历史
CREATE TABLE channel_binding_history (
    id                          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    bolt_id                     bigint NOT NULL REFERENCES bolts(id),
    from_channel_id             bigint REFERENCES channels(id),
    to_channel_id               bigint REFERENCES channels(id),
    action                      varchar(32) NOT NULL,
    calibration_action          varchar(32) NOT NULL DEFAULT 'invalidate',
    moved_by                    bigint REFERENCES users(id),
    moved_at                    timestamptz NOT NULL DEFAULT now(),
    before_jsonb                jsonb,
    after_jsonb                 jsonb,
    note                        text,
    CHECK (action IN ('bind','unbind','move')),
    CHECK (calibration_action IN ('invalidate','transfer_with_validation','keep_uncomputed'))
);
CREATE INDEX idx_channel_binding_history_bolt ON channel_binding_history(bolt_id, moved_at DESC);
CREATE INDEX idx_channel_binding_history_channel ON channel_binding_history(to_channel_id, moved_at DESC);
COMMENT ON TABLE channel_binding_history IS 'bolt 与物理 channel 的业务绑定历史。所有绑定/解绑/换接口必须经 API 写入并审计。';

-- 标定档案（Phase 2 完整实现，Phase 1 先建空表）
CREATE TABLE calibration_records (
    id                          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    bolt_id                     bigint NOT NULL REFERENCES bolts(id),
    method                      varchar(32) NOT NULL,
    operator_user_id            bigint NOT NULL REFERENCES users(id),
    reviewer_user_id            bigint NOT NULL REFERENCES users(id),
    loading_device              varchar(128),
    loading_device_cert         varchar(64),
    env_temp_c                  numeric(6,2),
    env_humidity                numeric(5,2),
    points_jsonb                jsonb NOT NULL,
    k_l                         numeric(20,15) NOT NULL,
    k_l_uncertainty             numeric(20,15) NOT NULL,
    r_squared                   numeric(6,4) NOT NULL,
    outliers_removed            smallint NOT NULL DEFAULT 0,
    uncertainty_breakdown       jsonb,
    fitter_version              varchar(16) NOT NULL,
    ref_time_ns                 numeric(12,2) NOT NULL,
    ref_temp_c                  numeric(6,2) NOT NULL,
    passed                      boolean NOT NULL,
    notes                       text,
    pdf_report_url              varchar(512),
    operator_signature_at       timestamptz NOT NULL,
    reviewer_signature_at       timestamptz NOT NULL,
    expires_at                  date NOT NULL,
    superseded_by               uuid REFERENCES calibration_records(id),
    created_at                  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_calib_bolt ON calibration_records(bolt_id, created_at DESC);
CREATE INDEX idx_calib_active ON calibration_records(bolt_id) WHERE superseded_by IS NULL;
COMMENT ON TABLE calibration_records IS '标定档案；永久不可删；新标定通过 superseded_by 链接旧档案';

-- 通道表的外键补上（建表时 calibration_records 还不存在）
ALTER TABLE channels
    ADD CONSTRAINT fk_channels_active_calib
    FOREIGN KEY (active_calibration_id) REFERENCES calibration_records(id);

-- ============================================================================
-- 时序表（TimescaleDB hypertable）
-- ============================================================================

CREATE TABLE measurements (
    bolt_id                 bigint NOT NULL,
    channel_id              bigint NOT NULL,
    ts                      timestamptz NOT NULL,           -- 设备事件时间
    ingested_at             timestamptz NOT NULL DEFAULT now(),
    echo_time_ns            numeric(12,2),                  -- 可空：真机有时只回温度心跳无声时
    delta_tof_ns            numeric(12,2),
    sp_temp_c               numeric(6,2),
    gain                    real NOT NULL,
    status_code             smallint NOT NULL,
    preload_n               numeric(12,2),
    preload_ratio           numeric(6,4),
    preload_uncertainty_n   numeric(12,2),
    health_score            smallint,
    snr_db                  numeric(6,2),
    similarity              numeric(6,4),
    main_freq_mhz           numeric(5,2),
    feature_vector          jsonb,
    wave_blob_url           varchar(512),
    algo_version            varchar(64),
    calibration_id          uuid,
    PRIMARY KEY (channel_id, ts)
);

SELECT create_hypertable('measurements', 'ts', chunk_time_interval => INTERVAL '1 day');

CREATE INDEX idx_meas_status ON measurements(status_code, ts DESC) WHERE status_code < 0;
CREATE INDEX idx_meas_channel ON measurements(channel_id, ts DESC);
CREATE INDEX idx_meas_bolt_ts ON measurements(bolt_id, ts DESC);

-- 压缩与归档（按方案附录 D.5）
ALTER TABLE measurements SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'bolt_id',
    timescaledb.compress_orderby = 'ts DESC'
);
SELECT add_compression_policy('measurements', INTERVAL '30 days');
SELECT add_retention_policy('measurements', INTERVAL '5 years');

COMMENT ON TABLE measurements IS '时序测量数据 hypertable；status_code < 0 的不进这里，进 measurement_anomalies';

-- 异常测量
CREATE TABLE measurement_anomalies (
    bolt_id         bigint NOT NULL,
    channel_id      bigint NOT NULL,
    ts              timestamptz NOT NULL,
    status_code     smallint NOT NULL,
    reason          varchar(64) NOT NULL,
    raw_data_jsonb  jsonb,
    PRIMARY KEY (bolt_id, ts, status_code)
);
SELECT create_hypertable('measurement_anomalies', 'ts', chunk_time_interval => INTERVAL '7 days');

-- ============================================================================
-- 注：开发种子数据已抽到 deploy/sql/dev/0001_dev_seed.sql，
-- 默认 initdb 不挂载该目录。docker-compose.yml dev profile 才挂。
-- 生产 Inno Setup 安装包只跑 deploy/sql/ 下的 0001~00NN，不跑 dev/。
-- ============================================================================
