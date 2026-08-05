-- ============================================================================
-- 0029_notification_outbox.sql
-- 告警出站通知：渠道 / 规则 / 事务性发件箱
-- ----------------------------------------------------------------------------
-- 此前告警只有两个出口：SignalR 推给已打开的页面 + 页面提示音，值班的人不盯屏幕就
-- 什么都收不到。通知走事务性发件箱而不是内存接缝（IAlarmPublisher 推完即忘，进程
-- 重启/对端抖动事件就没了）：告警落库与发件箱入队同一事务，落库的告警必然有对应的
-- 发送记录；派发由 NotificationDispatcherService 轮询、失败退避重试、超限置 dead。
-- ============================================================================

CREATE TABLE IF NOT EXISTS notification_channels (
    id                   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name                 varchar(64) NOT NULL UNIQUE,
    kind                 varchar(16) NOT NULL,     -- 本轮只实现 'webhook'；'smtp'/'sms' 是预留枚举，端点暂不放行
    config_jsonb         jsonb NOT NULL,           -- webhook: {url, format, secret}；format: raw/wecom/dingtalk
    min_interval_seconds integer NOT NULL DEFAULT 60,   -- 渠道级限流：两次发送的最小间隔（风暴合并之外的第二道闸）
    enabled              boolean NOT NULL DEFAULT true,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CHECK (kind IN ('webhook', 'smtp', 'sms')),
    CHECK (min_interval_seconds >= 0)
);

CREATE TABLE IF NOT EXISTS notification_rules (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    channel_id      bigint NOT NULL REFERENCES notification_channels(id) ON DELETE CASCADE,
    min_severity    varchar(16) NOT NULL DEFAULT 'warn',   -- 该级别及以上才发；critical = 只发最高级
    alarm_types     text[],                                -- NULL = 全部类型
    group_id        bigint REFERENCES monitor_groups(id),  -- NULL = 全站
    notify_on_clear boolean NOT NULL DEFAULT false,        -- 恢复事件也通知（恢复不做级别过滤，规则勾了就发）
    quiet_start     time,                                  -- 服务器本地时区静默窗；critical 无视静默
    quiet_end       time,                                  -- start > end 表示跨零点窗（如 22:00–06:00）
    enabled         boolean NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CHECK (min_severity IN ('info', 'warn', 'critical')),
    CHECK ((quiet_start IS NULL) = (quiet_end IS NULL))
);
CREATE INDEX IF NOT EXISTS idx_notification_rules_channel ON notification_rules(channel_id);

-- 发件箱：一行 = 一个告警事件发给一个渠道，重试互不牵连。
-- payload_jsonb 是入队时的事件快照（螺栓/组/类型/级别/值/消息文本），发送时不回查业务表——
-- 螺栓事后改名/删除不影响已入队通知的内容。
CREATE TABLE IF NOT EXISTS notification_outbox (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    alarm_id        bigint NOT NULL REFERENCES alarms(id),
    channel_id      bigint NOT NULL REFERENCES notification_channels(id),
    event_kind      varchar(8) NOT NULL,           -- 'trigger' / 'clear'
    payload_jsonb   jsonb NOT NULL,
    status          varchar(8) NOT NULL DEFAULT 'pending',   -- pending / sent / dead
    attempts        integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    last_error      text,
    created_at      timestamptz NOT NULL DEFAULT now(),
    sent_at         timestamptz,
    CHECK (event_kind IN ('trigger', 'clear')),
    CHECK (status IN ('pending', 'sent', 'dead'))
);
CREATE INDEX IF NOT EXISTS idx_notification_outbox_due ON notification_outbox(next_attempt_at) WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS idx_notification_outbox_alarm ON notification_outbox(alarm_id);

COMMENT ON TABLE notification_channels IS
    '出站通知渠道。config_jsonb 里的 secret 在 API 返回时抹掉；删除渠道受发件箱历史 FK 保护（有历史时改用禁用）。';
COMMENT ON TABLE notification_rules IS
    '通知规则：级别/类型/部位过滤 + 静默窗（critical 无视静默）。规则匹配在入队侧（内存快照），一条不中就不入队。';
COMMENT ON TABLE notification_outbox IS
    '事务性发件箱：与 alarms 同事务写入。退避 1/5/15/60 分钟，第 6 次失败置 dead（页面可见、可手工重发）。语义为至少一次：发送成功与置 sent 之间进程被杀会重发一次。';

DO $$ BEGIN RAISE NOTICE '0029_notification_outbox.sql 完成'; END $$;
