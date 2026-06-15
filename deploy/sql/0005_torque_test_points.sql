-- ============================================================================
-- 扭矩实验点记录表
-- ----------------------------------------------------------------------------
-- 用于现场 50/100/150/200 N·m 阶梯实验：
-- 每个扭矩点稳定一段时间后，从 measurements 中取时间窗统计，
-- 保存 EchoTime、温度、温补 Δt、扭矩法估算预紧力。
--
-- 注意：这不是 GB/T 43232-2023 的正式载荷标定，只是现场工况下的
-- 扭矩-超声响应一致性检查。正式验收仍需多点已知载荷标定。
-- ============================================================================

CREATE TABLE IF NOT EXISTS torque_test_points (
    id                      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    bolt_id                 bigint NOT NULL REFERENCES bolts(id),
    channel_id              bigint NOT NULL REFERENCES channels(id),
    torque_nm               numeric(10,2) NOT NULL,
    nut_factor              numeric(8,4) NOT NULL DEFAULT 0.20,
    nominal_diameter_mm     numeric(8,2) NOT NULL,
    estimated_preload_n     numeric(12,2) NOT NULL,
    sample_window_seconds   integer NOT NULL,
    sample_count            integer NOT NULL,
    window_start            timestamptz NOT NULL,
    window_end              timestamptz NOT NULL,
    avg_echo_time_ns        numeric(12,4) NOT NULL,
    stddev_echo_time_ns     numeric(12,4),
    min_echo_time_ns        numeric(12,4),
    max_echo_time_ns        numeric(12,4),
    avg_temp_c              numeric(6,3) NOT NULL,
    ref_time_ns             numeric(12,4) NOT NULL,
    ref_temp_c              numeric(6,3) NOT NULL,
    temp_coefficient        numeric(12,10) NOT NULL DEFAULT 0.00010269,
    normalized_echo_time_ns numeric(12,4) NOT NULL,
    delta_tof_ns            numeric(12,4) NOT NULL,
    operator_user_id        bigint REFERENCES users(id),
    note                    text,
    created_at              timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_torque_points_bolt_created
    ON torque_test_points(bolt_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_torque_points_channel_torque
    ON torque_test_points(channel_id, torque_nm, created_at DESC);

COMMENT ON TABLE torque_test_points IS
    '扭矩阶梯实验点：记录每个扭矩平台的 EchoTime/温度/Δt 统计，用于检查扭矩-超声响应线性。';
