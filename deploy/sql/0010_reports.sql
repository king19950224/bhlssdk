-- ============================================================================
-- 报告中心
-- ----------------------------------------------------------------------------
-- 商业软件要求报告可复现、可审计。报告不能只依赖实时查询结果，
-- 必须保存生成当时的数据快照、算法版本、标定版本、排除点原因。
-- ============================================================================

CREATE TABLE IF NOT EXISTS reports (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    report_type     varchar(64) NOT NULL,
    title           varchar(256) NOT NULL,
    status          varchar(24) NOT NULL DEFAULT 'generated',
    subject_type    varchar(32),
    subject_id      bigint,
    file_url        varchar(512),
    generated_by    bigint REFERENCES users(id),
    generated_at    timestamptz NOT NULL DEFAULT now(),
    signed_at       timestamptz,
    signed_by       bigint REFERENCES users(id),
    notes           text
);

CREATE TABLE IF NOT EXISTS report_snapshots (
    report_id       bigint PRIMARY KEY REFERENCES reports(id) ON DELETE CASCADE,
    snapshot_jsonb  jsonb NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_reports_type_time
    ON reports(report_type, generated_at DESC);

CREATE INDEX IF NOT EXISTS idx_reports_subject
    ON reports(subject_type, subject_id, generated_at DESC);

COMMENT ON TABLE reports IS '报告索引：标定报告、实验报告、健康报告、告警报告等。';
COMMENT ON TABLE report_snapshots IS '报告生成时的数据快照，保证报告可复现。';
