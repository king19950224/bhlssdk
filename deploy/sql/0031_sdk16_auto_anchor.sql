-- ============================================================================
-- 0031_sdk16_auto_anchor.sql
-- SDK 1.6 升级：自动法置零锚点 + 参考波形持久化 + 算法版本追溯
-- ----------------------------------------------------------------------------
-- 背景（sdk-1.6-upgrade-assessment §4.2 / sdk-1.6-upgrade-plan §5）：
-- 1.6 把自动法（meas_mode=0）从无状态改成有状态，靠 RefEnvTime + RefPeriodNs 两个锚点
-- 配着置零时的参考波形跨重启锁住载波周期。库里缺这三样的话，进程每次重启自动法都重新
-- 捕获，过零点可能落到相邻周期上——按现场 k_l 换算，一个 500ns 的周期跳变就是几百 kN
-- 的假预紧力，而且照常入库、照常不告警。
--
-- sdk_version 解决的是另一件事：标定系数和置零基线都是在某个 SDK 版本的算法下取的，
-- 1.6 的声时提取对自动法/动态特征法有系统性偏移（评估 §4.1），新算法配旧系数等于
-- 拿错尺子量。落一列版本号，混用时才拦得住、追溯时才说得清。
--
-- 本迁移只加列不改行为：1.4 在跑的现场照常，新列全 NULL；1.6 适配代码上线后，
-- /recalibrate 置零与各标定端点才开始写。回滚代码不需要回滚本迁移。
-- 升级前的现场事实采集另见 deploy/scripts/sdk16-preflight.sql。
-- ============================================================================

ALTER TABLE channels
    ADD COLUMN IF NOT EXISTS ref_env_time_ns numeric(12,2),
    ADD COLUMN IF NOT EXISTS ref_period_ns   numeric(8,2),
    ADD COLUMN IF NOT EXISTS ref_echo_wave   bytea,
    ADD COLUMN IF NOT EXISTS sdk_version     text;

COMMENT ON COLUMN channels.ref_env_time_ns IS
    'SDK 1.6 自动法的包络锚点（ns，绝对时间），与 ref_time_ns 同帧配对。置零时由 SDK 给出，'
    '开机随 ChannelConfig 回灌会话，缺失时自动法每次重启重新捕获、有整周期跳变风险。1.4 不产生也不使用本列。';
COMMENT ON COLUMN channels.ref_period_ns IS
    'SDK 1.6 自动法的载波周期（ns，2MHz 探头约 500）。与 ref_env_time_ns 同批写入、同批回灌。';
COMMENT ON COLUMN channels.ref_echo_wave IS
    '置零基线参考波形(LEchoWave)，FxRefWaveCodec 打包（[ver][EchoStart u32][ClockInt u16][count i32][i16...]，小端）。'
    '锚点只有配着参考波形回灌才起作用，坏数据解包返回 null 走首帧捕获。';
COMMENT ON COLUMN channels.sdk_version IS
    '本通道 ref_time_ns/ref_temp_c 基线是在哪个 SDK 版本下置的（如 1.6.0，NULL=1.6 升级前的存量基线）。'
    '声时提取算法换代后，旧基线配新算法会出系统性偏差，入库前应比对本列拦截混用。';

ALTER TABLE calibration_records
    ADD COLUMN IF NOT EXISTS sdk_version text;

COMMENT ON COLUMN calibration_records.sdk_version IS
    '标定采点时在用的 SDK 版本。k_l 是对着该版本的声时提取算法拟合的，跨版本沿用需先过同源比对。';

DO $$ BEGIN RAISE NOTICE '0031_sdk16_auto_anchor.sql 完成'; END $$;
