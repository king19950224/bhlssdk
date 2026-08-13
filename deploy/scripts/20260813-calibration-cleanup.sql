-- ============================================================================
-- 2026-08-13 存量坏标定清理（calibration-hardening-design §5，一次性运维脚本）
--
-- 背景：08-12 真机标定时采样窗口混入加载前数据，CH1/CH2/CH8 标出的 K_L 偏小
-- 5~18 倍、预紧力读数虚高（CH2 甚至折算 411kN 超屈服被过滤成无数据）；
-- CH3 的 K_L 量级正确，但 08:55 那次带载重设基线把零点抬高了 11.6ns。
--
-- 处置：
--   1) CH1/CH2/CH8 在产 torque_estimate 全部作废（rejected），通道回"未标定"——
--      界面读数从"虚高数值"变成"未标定"，这是变诚实不是变坏；
--   2) CH3 用 08-12 实测数据写一版自洽的临时标定（快速标定语义）：
--      真零载 32593.6ns（17:18 拧松稳态实测）+ 200N·m 稳态 32661.7ns
--      → K_L = 68.1ns / 50000N = 1.362e-3 ns/N，不确定度按扭矩法惯例 30%；
--      设备上线后应按阶梯向导重做正式标定替换（本记录 7 天过期兜底）。
--   全部操作留审计（audit_log，user_id=1 管理员，note 标明 [运维直改]）。
-- ============================================================================

BEGIN;

-- ---------- 1. 作废 CH1 / CH2 / CH8 的在产坏标定 ----------
UPDATE calibration_records SET
    review_state = 'rejected',
    notes = COALESCE(notes, '') ||
        '；[2026-08-13 运维作废] 标定采样窗口混入加载前数据，K_L 偏小一个数量级（读数虚高），依据 calibration-hardening-design §0/§5'
WHERE id IN (
    '443d1dc1-69d6-40a8-b9be-1ecb4a5b30a0',  -- 通道1-1号螺栓 K_L=8.4e-5
    '32a2e6ce-2f61-44a7-8110-bac213ac9fc2',  -- 通道1-2号螺栓 K_L=7.4e-5
    'a976e5ed-5873-4ac0-8569-a3a028f3ad19'   -- 通道1-8号螺栓 K_L=2.9e-4
);

UPDATE channels SET
    k_l = NULL, k_l_uncertainty = NULL, k_l_intercept_ns = NULL,
    active_calibration_id = NULL, updated_at = now()
WHERE device_id = 1 AND channel_no IN (1, 2, 8) AND deleted_at IS NULL;

UPDATE bolts b SET state = 'uncalibrated', updated_at = now()
FROM channels c
WHERE c.id = b.channel_id AND c.device_id = 1 AND c.channel_no IN (1, 2, 8)
  AND b.deleted_at IS NULL;

INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
SELECT 1, 'calibration_revoke', 'channel', c.id,
       jsonb_build_object('calibrationId', cr.id, 'kL', cr.k_l),
       jsonb_build_object('reviewState', 'rejected'),
       '[运维直改] 作废 ' || b.code || ' 的扭矩法标定（K_L=' || cr.k_l || '，采样窗口污染致偏小一个数量级），通道回未标定',
       now()
FROM calibration_records cr
JOIN bolts b ON b.id = cr.bolt_id
JOIN channels c ON c.id = b.channel_id
WHERE cr.id IN ('443d1dc1-69d6-40a8-b9be-1ecb4a5b30a0',
                '32a2e6ce-2f61-44a7-8110-bac213ac9fc2',
                'a976e5ed-5873-4ac0-8569-a3a028f3ad19');

-- ---------- 2. CH3：用 08-12 实测数据写自洽临时标定 ----------
-- 旧 active（K_L=1.769e-3，配套基线 32595.49 已被 08:55 的带载重设覆盖）标记被顶替
WITH new_calib AS (
    INSERT INTO calibration_records (
        id, bolt_id, method, operator_user_id, reviewer_user_id,
        loading_device, points_jsonb, k_l, k_l_uncertainty, r_squared, outliers_removed,
        fitter_version, ref_time_ns, ref_temp_c, passed, notes, review_state,
        operator_signature_at, reviewer_signature_at, expires_at, created_at, sdk_version
    )
    SELECT gen_random_uuid(), b.id, 'quick', 1, 1,
           'torque wrench',
           '[{"type":"field_measured","forceN":50000,"deltaTofNs":68.1,"note":"2026-08-12 实测：真零载 32593.6ns（17:18 拧松稳态），200N·m（K=0.2,d=20mm→50kN）稳态 32661.7ns"}]'::jsonb,
           0.001362, 0.000409, 0, 0,
           'manual:v1', 32593.6, 32.0, true,
           '[2026-08-13 运维直改] 由 08-12 实测数据重建：旧基线在带载状态被重设（偏高 11.6ns）导致读数虚低。本记录为临时标定，请按阶梯向导重做正式标定替换。',
           'self_signed', now(), now(), CURRENT_DATE + 7, now(), '1.6.0'
    FROM bolts b JOIN channels c ON c.id = b.channel_id
    WHERE c.device_id = 1 AND c.channel_no = 3 AND b.deleted_at IS NULL
    RETURNING id, bolt_id
),
supersede AS (
    UPDATE calibration_records cr SET superseded_by = nc.id
    FROM new_calib nc
    WHERE cr.bolt_id = nc.bolt_id AND cr.id <> nc.id AND cr.superseded_by IS NULL
    RETURNING cr.id
),
upd_channel AS (
    UPDATE channels c SET
        k_l = 0.001362, k_l_uncertainty = 0.000409, k_l_intercept_ns = NULL,
        ref_time_ns = 32593.6, ref_temp_c = 32.0,
        active_calibration_id = nc.id, updated_at = now()
    FROM new_calib nc, bolts b
    WHERE b.id = nc.bolt_id AND c.id = b.channel_id
    RETURNING c.id
)
INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
SELECT 1, 'quick_calibrate', 'channel', uc.id,
       jsonb_build_object('refTimeNs', 32605.18, 'kL', 0.001769055122358),
       jsonb_build_object('refTimeNs', 32593.6, 'kL', 0.001362, 'calibrationId', nc.id),
       '[运维直改] 通道1-3号螺栓临时标定重建：真零载基线 32593.6ns + K_L=1.362e-3（08-12 实测），修正带载重设基线造成的 11.6ns 零点偏移',
       now()
FROM new_calib nc CROSS JOIN upd_channel uc;

COMMIT;

-- ---------- 3. 验证 ----------
SELECT b.code, c.channel_no, b.state, c.k_l, c.ref_time_ns, c.active_calibration_id IS NOT NULL AS has_active
FROM bolts b JOIN channels c ON c.id = b.channel_id
WHERE b.deleted_at IS NULL ORDER BY c.channel_no;

SELECT cr.review_state, count(*) FROM calibration_records cr GROUP BY 1 ORDER BY 1;
