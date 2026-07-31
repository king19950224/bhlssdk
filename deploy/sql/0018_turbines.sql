-- ============================================================================
-- 资产层级补“风机 / 机组”层
-- ----------------------------------------------------------------------------
-- 原资产层级 项目 → 监测组(部位) → 螺栓 缺少风机层；一个风场几十上百台风机时，
-- 操作员需先选风机再看其各部位。补 turbines 表，置于 项目 与 监测组 之间：
--   Project 风场 → Turbine 风机/机组 → MonitorGroup 部位/法兰 → Bolt 螺栓
-- ============================================================================

CREATE TABLE IF NOT EXISTS turbines (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id          bigint NOT NULL REFERENCES projects(id),
    code                varchar(64) NOT NULL,
    name                varchar(128) NOT NULL,
    model               varchar(64),                 -- 风机型号，如 "GW155-4.5MW"
    capacity_kw         integer,                     -- 额定功率 kW
    location            varchar(128),                -- 机位号 / 坐标
    commissioned_date   date,                        -- 投运日期
    enabled             boolean NOT NULL DEFAULT true,
    state               varchar(16) NOT NULL DEFAULT 'unknown',
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    deleted_at          timestamptz,
    UNIQUE (project_id, code)
);
CREATE INDEX IF NOT EXISTS idx_turbines_project ON turbines(project_id) WHERE deleted_at IS NULL;
COMMENT ON TABLE turbines IS '风机/机组：项目与监测组之间的资产层级';

ALTER TABLE monitor_groups
    ADD COLUMN IF NOT EXISTS turbine_id bigint REFERENCES turbines(id);
CREATE INDEX IF NOT EXISTS idx_monitor_groups_turbine
    ON monitor_groups(turbine_id) WHERE deleted_at IS NULL;
COMMENT ON COLUMN monitor_groups.turbine_id IS '所属风机；存量数据回填到“默认机组”';

-- 回填：为存在“无机组归属”部位的项目各建一台默认机组并挂接，保证资产树不断链。
-- 幂等：默认机组 code='DEFAULT'；仅处理 turbine_id 仍为空的部位。
DO $$
DECLARE p RECORD; t_id bigint;
BEGIN
    FOR p IN
        SELECT DISTINCT project_id
        FROM monitor_groups
        WHERE turbine_id IS NULL AND deleted_at IS NULL
    LOOP
        INSERT INTO turbines (project_id, code, name)
        VALUES (p.project_id, 'DEFAULT', '默认机组')
        ON CONFLICT (project_id, code) DO NOTHING;

        SELECT id INTO t_id FROM turbines WHERE project_id = p.project_id AND code = 'DEFAULT';

        UPDATE monitor_groups
        SET turbine_id = t_id
        WHERE project_id = p.project_id AND turbine_id IS NULL AND deleted_at IS NULL;
    END LOOP;
END $$;
