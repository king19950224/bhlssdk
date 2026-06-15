-- ============================================================================
-- 波形特征与参考波形
-- ============================================================================

CREATE TABLE IF NOT EXISTS wave_features (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    bolt_id             bigint REFERENCES bolts(id),
    channel_id          bigint NOT NULL REFERENCES channels(id),
    ts                  timestamptz NOT NULL,
    wave_blob_url       varchar(512) NOT NULL,
    point_count         integer NOT NULL,
    echo_start_ns       numeric(12,2),
    clock_int_ns        integer,
    threshold           smallint,
    peak_positive       integer,
    peak_negative       integer,
    peak_to_peak        integer,
    mean_value          numeric(12,4),
    rms_value           numeric(12,4),
    noise_rms           numeric(12,4),
    snr_db              numeric(8,3),
    created_at          timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_wave_features_channel_ts
    ON wave_features(channel_id, ts DESC);

CREATE INDEX IF NOT EXISTS idx_wave_features_bolt_ts
    ON wave_features(bolt_id, ts DESC);

CREATE TABLE IF NOT EXISTS reference_waves (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    channel_id          bigint NOT NULL REFERENCES channels(id),
    bolt_id             bigint REFERENCES bolts(id),
    wave_feature_id     bigint NOT NULL REFERENCES wave_features(id),
    reason              varchar(64) NOT NULL DEFAULT 'manual',
    active              boolean NOT NULL DEFAULT true,
    created_by          bigint REFERENCES users(id),
    created_at          timestamptz NOT NULL DEFAULT now(),
    note                text
);

CREATE INDEX IF NOT EXISTS idx_reference_waves_active
    ON reference_waves(channel_id, active, created_at DESC);

COMMENT ON TABLE wave_features IS '波形特征：保存波形文件之外的可查询质量指标，用于波形诊断和报告。';
COMMENT ON TABLE reference_waves IS '参考波形：每个通道/螺栓用于对比的基准波形。';
