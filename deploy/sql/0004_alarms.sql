-- ============================================================================
-- 告警表（业务告警，与 measurement_anomalies SDK 异常分开）
-- ----------------------------------------------------------------------------
-- 设计目标（对齐方案 §8.7 / GB/T 41261-2022）：
-- 1. severity 分层：info / warn / critical（GB/T 41261 三级警告）
-- 2. type 区分业务类型：preload_low / preload_high / preload_loss / signal_lost / 后续可扩展
-- 3. 状态机：开启时 acknowledged_at NULL，cleared_at NULL；
--    业主 ack 后 acknowledged_at 有值；
--    自动恢复后 cleared_at 有值（cleared_by_reason 描述如何闭合）
-- 4. 不带 deadband / on-delay 字段，这些是 evaluator 内存状态机的事
-- 5. 触发时拍快照存 preload_n / preload_ratio / target_preload_n / threshold，便于事后回溯
-- ============================================================================

CREATE TABLE IF NOT EXISTS alarms (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    bolt_id         bigint NOT NULL REFERENCES bolts(id),
    channel_id      bigint NOT NULL REFERENCES channels(id),
    type            varchar(32) NOT NULL,          -- preload_low / preload_high / signal_lost / sdk_error
    severity        varchar(16) NOT NULL,          -- info / warn / critical
    triggered_at    timestamptz NOT NULL,          -- 状态机判定为"已触发"的时刻
    acknowledged_at timestamptz,
    acknowledged_by bigint REFERENCES users(id),
    acknowledged_note text,
    cleared_at      timestamptz,
    cleared_reason  varchar(64),                   -- "auto_recovered" | "manual_reset" | "shelved"
    -- 触发时快照（防止后续读 measurements 时由于压缩/归档不一致）
    preload_n_at_trigger        numeric(12,2),
    preload_ratio_at_trigger    numeric(6,4),
    target_preload_n            numeric(12,2),
    threshold_low_ratio         numeric(6,4),
    threshold_high_ratio        numeric(6,4),
    consecutive_count           integer NOT NULL DEFAULT 1, -- on-delay 命中条数
    message         text,
    created_at      timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_alarms_bolt_ts ON alarms(bolt_id, triggered_at DESC);
CREATE INDEX IF NOT EXISTS idx_alarms_open ON alarms(triggered_at DESC) WHERE cleared_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_alarms_type_ts ON alarms(type, triggered_at DESC);

COMMENT ON TABLE alarms IS
    '业务告警（区别于 measurement_anomalies 的 SDK 异常）。 状态机：cleared_at IS NULL 为开启；acknowledged_at IS NULL 为未 ack。 同一 (bolt_id, type) 同时只允许一条开启告警（应用层保证，未加 unique 约束便于历史回放）。';
