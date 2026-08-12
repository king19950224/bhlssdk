-- ============================================================================
-- sdk16-preflight.sql — SDK 1.6 升级前的现场事实采集
-- ----------------------------------------------------------------------------
-- 这些数字没法从源码推出来，必须查现场库（sdk-1.6-upgrade-assessment §8）。
-- 查出来的结果直接决定升级落在哪一档：
--   全部通道 meas_mode=2（阈值法）→ 声时提取实质不变，同源比对通过即可换；
--   存在 meas_mode=0/1（自动/动态特征）→ 这些通道必须重新置零 + 重新全量标定。
--
-- 用法（工控机或任一能连库的机器）：
--   psql "host=127.0.0.1 dbname=boltpreload user=boltpreload" -f sdk16-preflight.sql
-- 输出整份贴进升级检查单存档。
-- ============================================================================

\echo ''
\echo '===== 1. meas_mode 分布（决定整个升级是两天适配还是一轮重标定）====='
SELECT meas_mode,
       threshold,
       count(*)                                          AS 通道数,
       count(*) FILTER (WHERE sampling_enabled)          AS 在采,
       count(*) FILTER (WHERE ref_temp_c IS NULL)        AS ref_temp为空,
       count(*) FILTER (WHERE ref_time_ns IS NOT NULL)   AS 已置零
FROM channels
WHERE deleted_at IS NULL
GROUP BY meas_mode, threshold
ORDER BY meas_mode, threshold;

\echo ''
\echo '===== 2. ref_temp 为空但已置零的通道（1.6 会拿 22.22°C 顶上，静默出假声时差）====='
SELECT c.device_id, c.channel_no, c.meas_mode, c.ref_time_ns, c.ref_temp_c,
       b.code AS 螺栓编号
FROM channels c
LEFT JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
WHERE c.deleted_at IS NULL
  AND c.ref_time_ns IS NOT NULL
  AND c.ref_temp_c IS NULL
ORDER BY c.device_id, c.channel_no;

\echo ''
\echo '===== 3. k_l 量级（把“1 个载波周期跳变”换算成现场的假预紧力）====='
-- 2MHz 探头周期约 500ns：跳一个周期的假预紧力 = 500 / k_l（牛）。
-- k_l 全系统单位 ns/N（sdk-api-facts §12），量级约 1e-4~1e-3。
SELECT c.device_id, c.channel_no, c.k_l,
       CASE WHEN c.k_l > 0
            THEN round(500.0 / c.k_l / 1000.0, 1)
       END AS 周期跳变假预紧力_kN,
       c.probe_freq_mhz
FROM channels c
WHERE c.deleted_at IS NULL AND c.k_l IS NOT NULL
ORDER BY c.device_id, c.channel_no;

\echo ''
\echo '===== 4. 实际上报间隔（决定 1.6 温度平滑 τ=180s 相比 1.4 中值-10 是更滞后还是更灵敏，临界约 40s）====='
WITH gaps AS (
    SELECT channel_id,
           ts - lag(ts) OVER (PARTITION BY channel_id ORDER BY ts) AS gap
    FROM measurements
    WHERE ts > now() - interval '24 hours'
)
SELECT c.device_id, c.channel_no,
       count(*)                                            AS 样本数,
       round(EXTRACT(EPOCH FROM avg(g.gap))::numeric, 1)   AS 平均间隔秒,
       round(EXTRACT(EPOCH FROM percentile_cont(0.5) WITHIN GROUP (ORDER BY g.gap))::numeric, 1) AS 中位间隔秒
FROM gaps g
JOIN channels c ON c.id = g.channel_id
WHERE g.gap IS NOT NULL
GROUP BY c.device_id, c.channel_no
ORDER BY c.device_id, c.channel_no;

\echo ''
\echo '===== 5. 探头中心频率分布（评估按 2MHz/500ns 周期估算，5MHz 探头要按 200ns 重算）====='
SELECT probe_freq_mhz, count(*) AS 通道数
FROM channels
WHERE deleted_at IS NULL
GROUP BY probe_freq_mhz
ORDER BY probe_freq_mhz;

\echo ''
\echo '===== 6. 标定档案的版本留痕现状（0031 加列后新标定才会写，存量为空属预期）====='
SELECT coalesce(sdk_version, '(未记录)') AS sdk版本, review_state, count(*) AS 份数
FROM calibration_records
GROUP BY sdk_version, review_state
ORDER BY sdk_version, review_state;

\echo ''
\echo '采集完毕。第 1 项里只要出现 meas_mode=0 或 1 的在采通道，升级方案就按“重新置零+重新标定”准备。'
