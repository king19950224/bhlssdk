-- ----------------------------------------------------------------------------
-- 0032: 螺栓档案两级模型 — 占位档(provisional) / 资产档(confirmed)
-- ----------------------------------------------------------------------------
-- 设计:资料/工程文档/bolt-lifecycle-design.md。
-- 背景:系统物理上无法确认"回波属于哪颗资产螺栓"(同规格螺栓声时差 < 温漂),
-- "通道↔螺栓"绑定本质是人工声明。auto-enroll(检测即螺栓)建的档案在被人确认前
-- 只是"发现性占位":不出正式结论(未标定/未验收已天然如此),且应当可以被系统对称收回
-- (探头离位满 N 天自动软删)与在迁移时被自动吸收。confirmed_at 是"人工确认"的显式标记:
--   NULL      = 占位档(自动建档且无人工痕迹)
--   非 NULL   = 资产档(标定激活/通道验收/人工改规格/人工绑定迁移/人工建档,任一动作盖章)
-- 资产档拔探头 = 失联告警 + 永久留存(资产语义,不变);占位档离位满 N 天自动回收。
-- ----------------------------------------------------------------------------

ALTER TABLE bolts
    ADD COLUMN IF NOT EXISTS confirmed_at timestamptz;

COMMENT ON COLUMN bolts.confirmed_at IS
    '人工确认时间。NULL=占位档(自动建档且无人工痕迹,离位满 N 天自动回收、迁移时可被吸收);非 NULL=资产档(永久留存,拔探头走失联告警)。盖章点:标定激活/通道验收/人工改规格/人工绑定迁移/人工建档。';

-- 存量回填:以下任一条件视为"被人确认过",盖章为资产档
--   1. 已标定(state=calibrated 或曾有标定记录)
--   2. 所绑通道已通过接入验收
--   3. audit_log 里有任何人工操作痕迹(user_id 非空)
--   4. 不是 auto-enroll 建的(建档审计 user_id 非空,或没有建档审计=迁移期老数据,按人工对待)
UPDATE bolts b
   SET confirmed_at = COALESCE(b.updated_at, b.created_at)
 WHERE b.confirmed_at IS NULL
   AND (
        b.state = 'calibrated'
     OR EXISTS (SELECT 1 FROM calibration_records cr WHERE cr.bolt_id = b.id)
     OR EXISTS (SELECT 1 FROM channels c
                 WHERE c.id = b.channel_id AND c.accepted_at IS NOT NULL)
     OR EXISTS (SELECT 1 FROM audit_log a
                 WHERE a.target_type = 'bolt' AND a.target_id = b.id AND a.user_id IS NOT NULL)
     OR NOT EXISTS (SELECT 1 FROM audit_log a
                 WHERE a.target_type = 'bolt' AND a.target_id = b.id
                   AND a.action = 'create_bolt' AND a.user_id IS NULL)
   );

-- 占位档集合极小,部分索引足够支撑回收任务与迁移吸收的查找
CREATE INDEX IF NOT EXISTS idx_bolts_unconfirmed
    ON bolts(channel_id)
    WHERE confirmed_at IS NULL AND deleted_at IS NULL;
