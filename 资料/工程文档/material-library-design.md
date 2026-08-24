# 材料库与规格预设设计

> 状态：设计稿（2026-08-18）。对标厂家 iFastApp 的 `u_spec`（材料声学库）与 `u_bolt_info`（螺栓规格预设），
> 见 `vendor-benchmark-ifastapp.md` §2 采纳清单第 1 位。
> 动机：现状每颗螺栓的声速/弹模/屈服/截面积全靠手填，auto-enroll 又给一套写死的缺省（42CrMo/M20/8.8）——
> 规格配错的直接后果就是 GB 应力门误判（本周 ch2/ch5/ch8 的"超范围拒算"根源）。
> 选材料、选规格，参数自动带出，错误从源头消掉。

## 0. 概念划分（比厂家的单表更准确）

厂家 `u_spec` 把屈服强度挂在材料上，这在螺栓领域是不准确的简化——**材料**（42CrMo）决定声学与弹性属性，
**性能等级**（8.8/10.9）决定强度属性（GB/T 3098.1 标准定义，与用什么钢无关），**规格**（M20）决定几何。
三个维度分开建：

| 维度 | 载体 | 属性 | 来源 |
|---|---|---|---|
| 材料 | `material_specs` 表（可维护） | 纵波声速、温度系数、声弹综合系数、弹性模量 | 现场可实测修正，入库 |
| 性能等级 | 静态代码表（不进库） | Rp0.2（含 d≤16/d>16 分档）| GB/T 3098.1 标准值，不是现场数据 |
| 螺纹规格 | 静态代码表（不进库） | 应力截面积 As、螺距 | GB/T 16823.1 标准值 |
| 整颗模板 | `bolt_spec_presets` 表（可维护） | 组合以上三维 + 长度/目标力/下限 | 现场按项目沉淀 |

标准值做成代码常量表的理由：等级和螺纹是国标定义，让人在库里改它们只会造出"自定义的 8.8"这种事故；
真需要非标件时用 preset 覆盖字段即可。

## 1. 数据模型（迁移 0034）

```sql
-- 材料声学库（对标 u_spec；声学参数现场可用实测值修正，改动走审计）
CREATE TABLE material_specs (
    id                      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code                    varchar(32) NOT NULL UNIQUE,     -- 42CrMo / 35CrMo / 304 ...
    name                    varchar(64) NOT NULL,
    sound_velocity_ms       numeric(8,2) NOT NULL,           -- 纵波声速
    temp_coefficient_per_c  numeric(12,10) NOT NULL,         -- 声时温度系数
    acoustic_elastic_factor numeric(6,3) NOT NULL DEFAULT 2.5, -- 声弹综合系数 k（理论 K_L≈k·t/(E·A) 用）
    elastic_modulus_gpa     numeric(6,2) NOT NULL,
    note                    text,
    enabled                 boolean NOT NULL DEFAULT true,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now()
);

-- 整颗螺栓建档模板（对标 u_bolt_info）：项目沉淀，一键带出全参数
CREATE TABLE bolt_spec_presets (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name                varchar(64) NOT NULL UNIQUE,          -- 如"塔筒法兰 M36×3 10.9 42CrMo L=380"
    nominal_diam        varchar(8) NOT NULL,                  -- M20
    grade               varchar(8) NOT NULL,                  -- 8.8
    material_code       varchar(32) NOT NULL REFERENCES material_specs(code),
    cross_section_mm2   numeric(8,2),                         -- 空=按 GB/T 16823 标准 As
    rp02_mpa            numeric(8,2),                         -- 空=按 GB/T 3098.1 标准值
    effective_length_mm numeric(8,2) NOT NULL,
    total_length_mm     numeric(8,2) NOT NULL,
    target_preload_n    numeric(12,2),
    min_preload_n       numeric(12,2),
    note                text,
    enabled             boolean NOT NULL DEFAULT true,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now()
);
```

`bolts` 表**不加列**：material 字段已存在（继续存材料 code），预设/材料只是建档与改规格时的参数来源，
落到螺栓行的仍是展开后的具体数值——历史螺栓不受迁移影响，改材料库也不会追溯改动已建档螺栓（标定证据链不可变）。

预置种子（可改可补）：

| code | 声速 m/s | 温度系数 /°C | 声弹系数 | E GPa | 备注 |
|---|---|---|---|---|---|
| 42CrMo | 5920 | 1.0269e-4 | 2.5 | 206 | 风电高强螺栓主力牌号 |
| 35CrMo | 5900 | 1.0269e-4 | 2.5 | 206 | |
| 40Cr | 5900 | 1.0269e-4 | 2.5 | 206 | |
| ML20MnTiB | 5890 | 1.0269e-4 | 2.5 | 206 | 8.8 冷镦常用 |
| Q355 | 5890 | 1.0269e-4 | 2.5 | 206 | 结构钢 |
| 304 (A2) | 5790 | 1.2e-4 | 2.6 | 193 | 奥氏体不锈钢 |
| 316 (A4) | 5720 | 1.2e-4 | 2.6 | 193 | |

温度系数默认沿用全系统现值（`BoltPhysicsDefaults.SdkTempCoefficientPerC`），声速/声弹给行业常用值，
表内 note 注明"以同批实测/载荷标定为准"。

静态代码表（`BoltStandards`，进 Domain 项目）：

- 等级 → Rp0.2（GB/T 3098.1）：4.6→240、5.6→300、6.8→480、8.8→(d≤16: 640, d>16: 660)、
  9.8→720、10.9→940、12.9→1080；不锈钢 A2-70/A4-70→450、A2-80/A4-80→600
- 螺纹 → 应力截面积 As mm²（GB/T 16823.1，粗牙）：M10=58、M12=84.3、M16=157、M20=245、
  M24=353、M27=459、M30=561、M33=694、M36=817、M39=976、M42=1120、M48=1470、M56=2030、M64=2680

## 2. API

| 端点 | 权限 | 说明 |
|---|---|---|
| `GET /api/spec-library/materials` | 登录 | 材料下拉数据 |
| `POST/PATCH /api/spec-library/materials(/{id})` | admin | 维护材料（审计 `material_spec_update`）；禁删，只 enabled=false |
| `GET /api/spec-library/presets` | 登录 | 预设下拉数据 |
| `POST/PATCH /api/spec-library/presets(/{id})` | reviewer+ | 维护预设（审计） |
| `GET /api/spec-library/resolve?nominalDiam=M20&grade=8.8&materialCode=42CrMo` | 登录 | **展开器**：返回 As/Rp0.2/E/声速/温补/声弹系数一揽子推导值。建档、改规格、验收共用同一展开逻辑，防止三处各算一套 |

resolve 是纯函数式查询：标准值查代码表、材料值查库，不落任何数据。

## 3. 应用点（这次设计的全部价值所在）

| 应用点 | 现状 | 改后 |
|---|---|---|
| 建档对话框（BoltCreateDialog） | 全手填 | 顶部选预设一键带出；或选 材料+等级+规格 三个下拉自动回填（可改） |
| 改规格（BoltDetail 设置） | 全手填 | 材料/等级/规格下拉联动回填 E、声速、温补、Rp0.2、As |
| 点位表导入（WiringTable） | 逐列填 | 支持列写预设名，导入时展开 |
| GB 应力门（30%~90% Rp0.2×As） | 规格错 → outofrange 误拒（ch2/5/8 实证） | 标准值兜底，参数从源头准确 |
| 在役锚定理论 K_L（anchor-defaults） | 声弹系数写死 2.5 | 用材料的 acoustic_elastic_factor |
| 温补系数 | 多数螺栓空 → 回退 SDK 默认 | 建档即从材料带出 |
| auto-enroll 缺省规格 | 硬编码 42CrMo/M20/8.8 | P2：设备/组可配缺省预设；本期不动 |

## 4. 前端

- 新页面「参数字典」（/spec-library，admin 可写、其他角色只读）：材料库 + 规格预设两个 tab 的简单 CRUD 表格
- BoltCreateDialog、BoltDetail 设置对话框接 resolve 联动（选完自动填，字段仍可手改——预设是起点不是枷锁）

## 5. 分期与工作量

| 期 | 内容 | 量级 |
|---|---|---|
| P1 | 迁移 0034 + 种子、BoltStandards 代码表、spec-library 端点（materials/presets/resolve）、参数字典页、两个对话框联动 | 1 天内 |
| P2 | 点位表导入列支持预设名；设备/组级缺省预设接 auto-enroll；anchor-defaults 接声弹系数 | 半天 |

不做：材料库不追溯修改已建档螺栓（证据链不可变）；不允许在库里改国标等级/螺纹标准值。
