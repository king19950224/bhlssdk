-- 0034: 材料库与规格预设（material-library-design.md）
-- 三维拆分：材料（声学/弹性，可维护）· 性能等级（GB/T 3098.1，代码表）· 螺纹（GB/T 16823.1，代码表）。
-- bolts 表不加列、历史螺栓不追溯——库只是建档/改规格时的参数来源，落行的仍是展开后的具体数值。

CREATE TABLE IF NOT EXISTS material_specs (
    id                      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code                    varchar(32) NOT NULL UNIQUE,
    name                    varchar(64) NOT NULL,
    sound_velocity_ms       numeric(8,2) NOT NULL,
    temp_coefficient_per_c  numeric(12,10) NOT NULL,
    -- 声弹综合系数 k：理论 K_L ≈ k·t/(E·A) 的经验因子（弹性伸长 + 声弹效应合并），
    -- 在役锚定的理论估算用它；载荷标定得到的实测 K_L 永远优先
    acoustic_elastic_factor numeric(6,3) NOT NULL DEFAULT 2.5,
    elastic_modulus_gpa     numeric(6,2) NOT NULL,
    note                    text,
    enabled                 boolean NOT NULL DEFAULT true,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE material_specs IS
    '材料声学库（对标厂家 u_spec）：声速/温补/声弹/弹模按材料预设，现场可用实测修正（审计）。不追溯已建档螺栓。';

CREATE TABLE IF NOT EXISTS bolt_spec_presets (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name                varchar(64) NOT NULL UNIQUE,
    nominal_diam        varchar(8) NOT NULL,
    grade               varchar(8) NOT NULL,
    material_code       varchar(32) NOT NULL REFERENCES material_specs(code),
    cross_section_mm2   numeric(8,2),      -- 空 = 按 GB/T 16823.1 标准应力截面积
    rp02_mpa            numeric(8,2),      -- 空 = 按 GB/T 3098.1 标准值
    effective_length_mm numeric(8,2) NOT NULL,
    total_length_mm     numeric(8,2) NOT NULL,
    target_preload_n    numeric(12,2),
    min_preload_n       numeric(12,2),
    note                text,
    enabled             boolean NOT NULL DEFAULT true,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE bolt_spec_presets IS
    '整颗螺栓建档模板（对标厂家 u_bolt_info）：规格+等级+材料+长度+目标力一键带出，项目沉淀。';

-- 种子材料：行业常用值，note 注明以实测/载荷标定为准。temp_coefficient 与全系统现值一致（SDK 默认）。
INSERT INTO material_specs (code, name, sound_velocity_ms, temp_coefficient_per_c, acoustic_elastic_factor, elastic_modulus_gpa, note)
VALUES
    ('42CrMo',    '42CrMo 合金结构钢',   5920, 0.00010269, 2.5, 206, '风电高强螺栓主力牌号；声速/声弹为行业常用值，以同批实测为准'),
    ('35CrMo',    '35CrMo 合金结构钢',   5900, 0.00010269, 2.5, 206, NULL),
    ('40Cr',      '40Cr 合金结构钢',     5900, 0.00010269, 2.5, 206, NULL),
    ('ML20MnTiB', 'ML20MnTiB 冷镦钢',    5890, 0.00010269, 2.5, 206, '8.8 级冷镦常用'),
    ('Q355',      'Q355 低合金结构钢',   5890, 0.00010269, 2.5, 206, NULL),
    ('304',       '304 (A2) 奥氏体不锈钢', 5790, 0.00012000, 2.6, 193, '不锈钢温度系数偏大，建议实测'),
    ('316',       '316 (A4) 奥氏体不锈钢', 5720, 0.00012000, 2.6, 193, NULL)
ON CONFLICT (code) DO NOTHING;
