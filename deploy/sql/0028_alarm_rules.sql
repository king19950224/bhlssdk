-- ============================================================================
-- 0028_alarm_rules.sql
-- 告警阈值进库：三级 scope（global/group/bolt）+ 两步异人复核
-- ----------------------------------------------------------------------------
-- 此前静态阈值八个参数全在 appsettings 的 Alarm:StaticThreshold 节：全局一套、
-- 改配置要重启、审计不留痕。这张表把阈值搬进库，生效顺序 bolt > group > global >
-- appsettings 出厂兜底（表里一条 active 都没有时行为与进库前完全相同）。
-- 修改走提案(pending) → 异人批准(active) / 驳回(rejected)，新 active 落地时
-- 同目标旧 active 置 superseded。评估器经 IAlarmRuleProvider 读内存快照，不查库。
-- ============================================================================

CREATE TABLE IF NOT EXISTS alarm_rules (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    scope               varchar(8)   NOT NULL,   -- 'global' / 'group' / 'bolt'
    group_id            bigint REFERENCES monitor_groups(id),
    bolt_id             bigint REFERENCES bolts(id),

    -- 与 StaticThresholdAlarmOptions 一一对应的八个参数
    trigger_low_ratio   numeric(6,4) NOT NULL,
    clear_low_ratio     numeric(6,4) NOT NULL,
    trigger_high_ratio  numeric(6,4) NOT NULL,
    clear_high_ratio    numeric(6,4) NOT NULL,
    trigger_consecutive integer      NOT NULL,
    clear_consecutive   integer      NOT NULL,
    critical_deviation  numeric(6,4) NOT NULL,
    preload_loss_ratio  numeric(6,4) NOT NULL,

    reason              text         NOT NULL,   -- 修改依据：国标条款/合同条款/标定记录编号，强制填
    review_state        varchar(16)  NOT NULL DEFAULT 'pending',  -- pending/active/rejected/superseded
    created_by          bigint       NOT NULL REFERENCES users(id),
    created_at          timestamptz  NOT NULL DEFAULT now(),
    approved_by         bigint REFERENCES users(id),
    approved_at         timestamptz,

    -- 回差方向不许配反：恢复限必须在触发限的"安全侧"，否则状态机在边界抖动
    CHECK (clear_low_ratio  > trigger_low_ratio),
    CHECK (clear_high_ratio < trigger_high_ratio),
    CHECK (trigger_consecutive >= 1 AND clear_consecutive >= 1),
    CHECK (preload_loss_ratio >= 0 AND trigger_low_ratio > 0),
    -- scope 与目标列必须成套：global 两列全空，group/bolt 各只填自己那列
    CHECK ((scope = 'global' AND group_id IS NULL     AND bolt_id IS NULL)
        OR (scope = 'group'  AND group_id IS NOT NULL AND bolt_id IS NULL)
        OR (scope = 'bolt'   AND bolt_id  IS NOT NULL AND group_id IS NULL))
);

-- 同一目标同时只允许一条生效规则（active）和一条在审提案（pending）：
-- 没有 pending 唯一约束的话，同目标多条提案会在批准时互相竞争，先批的赢、后批的把前者 supersede，
-- 复核人看到的"当前提案"和实际生效的可能不是同一条。
CREATE UNIQUE INDEX IF NOT EXISTS ux_alarm_rules_active_global  ON alarm_rules((1))      WHERE review_state = 'active'  AND scope = 'global';
CREATE UNIQUE INDEX IF NOT EXISTS ux_alarm_rules_active_group   ON alarm_rules(group_id) WHERE review_state = 'active'  AND scope = 'group';
CREATE UNIQUE INDEX IF NOT EXISTS ux_alarm_rules_active_bolt    ON alarm_rules(bolt_id)  WHERE review_state = 'active'  AND scope = 'bolt';
CREATE UNIQUE INDEX IF NOT EXISTS ux_alarm_rules_pending_global ON alarm_rules((1))      WHERE review_state = 'pending' AND scope = 'global';
CREATE UNIQUE INDEX IF NOT EXISTS ux_alarm_rules_pending_group  ON alarm_rules(group_id) WHERE review_state = 'pending' AND scope = 'group';
CREATE UNIQUE INDEX IF NOT EXISTS ux_alarm_rules_pending_bolt   ON alarm_rules(bolt_id)  WHERE review_state = 'pending' AND scope = 'bolt';

COMMENT ON TABLE alarm_rules IS
    '静态阈值告警规则（三级 scope）。生效顺序 bolt > group > global > appsettings 兜底；修改走两步异人复核。';
COMMENT ON COLUMN alarm_rules.reason IS
    '修改依据（国标条款/合同条款/标定记录编号），提案时强制填，跟着规则一起归档。';
COMMENT ON COLUMN alarm_rules.approved_by IS
    '复核人（批准或驳回的操作者，与 approved_at 成对）。提案人不得复核自己的提案，端点强制。';

DO $$ BEGIN RAISE NOTICE '0028_alarm_rules.sql 完成'; END $$;
