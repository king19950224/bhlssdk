-- ============================================================================
-- 通道参数变更请求
-- ----------------------------------------------------------------------------
-- 商业版要求：参数修改不能裸写设备，必须有请求、执行、读回验证、审计。
-- ============================================================================

CREATE TABLE IF NOT EXISTS channel_config_change_requests (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    device_id           bigint NOT NULL REFERENCES devices(id),
    channel_id          bigint NOT NULL REFERENCES channels(id),
    channel_no          smallint NOT NULL,
    requested_by        bigint REFERENCES users(id),
    requested_at        timestamptz NOT NULL DEFAULT now(),
    requested_jsonb     jsonb NOT NULL,
    status              varchar(24) NOT NULL DEFAULT 'requested',
    executed_at         timestamptz,
    executed_by         bigint REFERENCES users(id),
    verify_started_at   timestamptz,
    verified_at         timestamptz,
    verify_jsonb        jsonb,
    failure_reason      text,
    note                text
);

CREATE INDEX IF NOT EXISTS idx_channel_config_change_channel
    ON channel_config_change_requests(channel_id, requested_at DESC);

CREATE INDEX IF NOT EXISTS idx_channel_config_change_status
    ON channel_config_change_requests(status, requested_at DESC);

COMMENT ON TABLE channel_config_change_requests IS
    '通道参数变更请求：记录参数改动、执行、验证和失败原因，避免裸写设备参数。';
