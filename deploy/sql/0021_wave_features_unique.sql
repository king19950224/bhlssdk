-- ============================================================================
-- wave_features 唯一约束：(channel_id, ts)
-- ----------------------------------------------------------------------------
-- 周期波形采集（WaveSampling 主动读 LEchoWave）与 WAL 重放可能对同一通道、同一时刻
-- 重复写 wave_features，产生重复行（/compare、/features/recent 用 ORDER BY ts DESC LIMIT
-- 容忍，但属冗余且口径不稳）。加唯一约束做 DB 级去重，配合写入端
-- INSERT ... ON CONFLICT (channel_id, ts) DO NOTHING。
--
-- 幂等：本脚本可重复执行（与 deploy/sql 其它迁移一致，供 initdb / 安装器按序重跑）。
--   1) 先去重历史重复行：每个 (channel_id, ts) 仅保留 id 最大的一行；
--   2) 仅当约束不存在时才添加。
-- ============================================================================

DELETE FROM wave_features wf
USING wave_features dup
WHERE wf.channel_id = dup.channel_id
  AND wf.ts = dup.ts
  AND wf.id < dup.id;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'uq_wave_features_channel_ts'
    ) THEN
        ALTER TABLE wave_features
            ADD CONSTRAINT uq_wave_features_channel_ts UNIQUE (channel_id, ts);
    END IF;
END $$;

COMMENT ON CONSTRAINT uq_wave_features_channel_ts ON wave_features
    IS '同一通道同一时刻仅一条波形特征：防止周期采集/WAL 重放产生重复行。';
