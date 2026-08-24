-- 0035: 告警处置归因（闭环第一环）
-- 闭合告警时记录结构化归因：区分"真松动/真过载"与四类误报根因（基线、探头、配置、试验操作）。
-- 归因数据反哺报警合理化评审（KPI 页误报率与根因分布），这是 GB/T 41261 报警管理闭环的落地载体。
-- cleared_reason（自由文本）保留作补充说明，resolution 是可统计的枚举。

ALTER TABLE alarms
    ADD COLUMN IF NOT EXISTS resolution varchar(32);

-- 白名单约束：拼错的归因会让统计口径悄悄碎掉，入库即挡。
-- NULL = 自动恢复闭合（系统清除）或旧数据（0035 之前的人工闭合）。
ALTER TABLE alarms
    DROP CONSTRAINT IF EXISTS chk_alarms_resolution;
ALTER TABLE alarms
    ADD CONSTRAINT chk_alarms_resolution CHECK (
        resolution IS NULL OR resolution IN (
            'real_loosening',      -- 真实松动（已复拧/处理）
            'real_overload',       -- 真实过载/超张（已处理）
            'baseline_error',      -- 基线错误（已重标定/锚定）
            'sensor_issue',        -- 探头耦合/信号问题（已处理）
            'config_error',        -- 目标/规格配置错误（已修正）
            'external_operation',  -- 试验/检修等人为操作产生（预期内）
            'other'                -- 其他（详见 cleared_reason）
        )
    );

COMMENT ON COLUMN alarms.resolution IS
    '人工闭合时的结构化归因（NULL=自动恢复或旧数据）。real_* 为真实告警，其余为误报根因，供报警合理化统计。';
