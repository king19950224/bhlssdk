-- ============================================================================
-- 设备参数历史表（追踪所有读到的设备/通道参数变化）
-- 用于：诊断追溯、标定历史审计、配置变更回滚参考
-- ============================================================================

CREATE TABLE IF NOT EXISTS device_parameter_history (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    device_id           bigint NOT NULL,
    address             smallint NOT NULL,
    channel_no          smallint,                       -- NULL 表示设备级参数
    param_name          varchar(64) NOT NULL,
    param_value_text    text,                           -- 简单类型直接存字符串
    param_value_jsonb   jsonb,                          -- 复杂类型（PulseConfig/LEchoWave 等）存 JSON
    received_at         timestamptz NOT NULL,           -- 设备事件时间
    ingested_at         timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_dph_device_param
    ON device_parameter_history(device_id, param_name, received_at DESC);

CREATE INDEX IF NOT EXISTS idx_dph_device_channel
    ON device_parameter_history(device_id, channel_no, received_at DESC)
    WHERE channel_no IS NOT NULL;

COMMENT ON TABLE device_parameter_history IS
    '设备参数读取历史，用于诊断（RefTime/RefTemp 变更追溯、Gain 调整记录等）。永久保留（append-only）。';
