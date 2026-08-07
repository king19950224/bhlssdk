#!/usr/bin/env python3
"""代码审计:六类完备性门禁,G1-G4、G6 全部归零才退出 0,G5 为报告级(仅提示,不拦截)。

用法:
    python3 tools/code-audit.py            # 跑全部门禁
    python3 tools/code-audit.py --gate g3  # 只跑某一项

门禁:
    G1 后端注释覆盖   src/ 与 tools/ 下全部 public 类型必须有 /// XML 注释
    G2 前端头注释     web/src 下全部 .vue/.ts 文件首部必须有用途注释
    G3 文档引用完整   活文档中的相对链接与反引号源码文件引用必须指向存在的文件
    G4 遗留标记归零   TODO/FIXME/HACK/debugger/console.log 等开发残留必须为零
    G5 未引用导出     web/src 中疑似无人引用的 export(动态引用会误报,故仅报告)
    G6 文风统一       禁表情符号入库(状态用文字写);引号统一中文弯引号;
                      禁套话与给后果加戏的注释

所有豁免显式列在下方常量区并附理由,新增豁免必须写明原因。
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# ---------------- 豁免清单(新增必须附理由) ----------------

# G1:不扫描的目录
G1_EXCLUDE_DIRS = {
    "obj", "bin",             # 构建产物
    "BoltPreload.Tests",      # 测试项目:测试方法名自述,不强制 XML doc
}
# external/FxSDK 为厂家 SDK,不在 src/、tools/ 之下,天然不入扫描范围

# G2:豁免文件(相对 web/src)
G2_EXEMPT = {
    "vite-env.d.ts",          # Vite 生成的类型声明存根
}

# G3:参与检查的"活文档"(会随代码演进而维护的文档)
G3_LIVING_DOCS = [
    "README.md",
    "资料/工程文档/getting-started.md",
    "资料/工程文档/development-guide.md",
    "资料/工程文档/deployment-guide.md",
    "资料/工程文档/concurrency-model.md",
    "资料/工程文档/sdk-api-facts.md",
    "资料/工程文档/calibration-field-guide.md",
    "资料/工程文档/troubleshooting.md",
    "资料/工程文档/probe-scan-design.md",
    "资料/工程文档/torque-step-test-protocol.md",
    "资料/工程文档/product-design.md",
]
# 豁免的历史记录文档(带日期/阶段快照,保留当时原貌,不随重构改写):
#   资料/工程文档/plan.md                               原始方案快照(K_L 口径等以 sdk-api-facts.md §12 为准)
#   资料/工程文档/phase-1-week-1-acceptance.md          阶段验收记录
#   资料/工程文档/phase-1-week-2-acceptance.md          阶段验收记录
#   资料/工程文档/lab-torque-step-report-2026-05-22.md  实验记录
#   资料/工程文档/site-debug-2026-05-21.md              现场排障记录
#   资料/工程文档/sdk-changelog.md                      SDK 变更流水(按时间追加)

# G3:反引号引用只检查源码类扩展名(不查 license.json 等运行期生成文件)
G3_REF_EXTS = (".cs", ".ts", ".vue", ".sql", ".ps1", ".md")

# G4:标记规则。C# 端 Console.WriteLine 是 CLI 工具的正常输出,不在禁止之列
G4_CSHARP = re.compile(r"\b(TODO|FIXME|HACK|XXX)\b")
G4_WEB = re.compile(r"\b(TODO|FIXME|HACK|XXX)\b|@ts-ignore|@ts-nocheck|eslint-disable|\bdebugger\b|console\.log")

# 仓库文件索引要跳过的目录。资料/ 不全跳——只扫 资料/工程文档/（内部工程文档仍进 git）
INDEX_SKIP = {".git", "node_modules", "bin", "obj", "dist", "logs", "data", ".vs", ".idea"}
INDEX_资料_ONLY = "工程文档"


def iter_files(base: Path, suffixes: tuple[str, ...]) -> list[Path]:
    """按后缀收集文件;在目录层剪枝跳过 node_modules 等大目录,避免全树遍历。"""
    import os

    out = []
    materials = (ROOT / "资料").resolve()
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = [d for d in dirnames if d not in INDEX_SKIP]
        # 资料/ 下只进入工程文档，其余（软著/会议/现场照片）不扫
        if Path(dirpath).resolve() == materials:
            dirnames[:] = [d for d in dirnames if d == INDEX_资料_ONLY]
        for name in filenames:
            # macOS 卷拷到别的文件系统会在每个文件旁留一份同名 ._xxx 资源派生文件(AppleDouble,二进制)。
            # 后缀跟正文一样,不排掉就会被 read_text(utf-8) 撞成 UnicodeDecodeError:
            # G1/G2/G4/G7 没接这个异常,整个门禁直接 traceback 退出(只有 G6 接了)。
            # 与 Directory.Build.props 的 DefaultItemExcludes 同一个根因、同一条排除。
            if name.startswith("._"):
                continue
            p = Path(dirpath) / name
            if p.suffix in suffixes:
                out.append(p)
    return out


# ---------------- G1 后端 public 类型注释覆盖 ----------------

TYPE_DECL = re.compile(
    r"^\s*public\s+(?:(?:sealed|static|abstract|partial|readonly)\s+)*"
    r"(class|record(?:\s+struct)?|interface|enum|struct|delegate)\s+(\w+)"
)


def gate1() -> list[str]:
    violations = []
    for base in (ROOT / "src", ROOT / "tools"):
        for f in iter_files(base, (".cs",)):
            if any(part in G1_EXCLUDE_DIRS for part in f.parts):
                continue
            lines = f.read_text(encoding="utf-8").splitlines()
            for i, line in enumerate(lines):
                m = TYPE_DECL.match(line)
                if not m:
                    continue
                j = i - 1
                documented = False
                while j >= 0:
                    s = lines[j].strip()
                    if s.startswith("///"):
                        documented = True
                        break
                    if s.startswith("[") or s == "":
                        j -= 1
                        continue
                    break
                if not documented:
                    violations.append(f"{f.relative_to(ROOT)}:{i + 1}: {m.group(2)}")
    return violations


# ---------------- G2 前端头注释 ----------------

def _has_header_before_code(lines: list[str]) -> bool:
    """规则:用途注释必须出现在首个非 import 语句之前(允许注释在 import 块之后、正文之前)。"""
    in_import = False
    in_block_comment = False
    for raw in lines:
        s = raw.strip()
        if not s:
            continue
        if in_block_comment:
            if "*/" in s:
                in_block_comment = False
            return True  # 块注释本身就算头注释
        if in_import:
            if "from" in s or s.endswith(("'", '"', "';", '";')):
                in_import = False
            continue
        if s.startswith("//"):
            return True
        if s.startswith("/*"):
            return True
        if s.startswith("import"):
            if "from" not in s and not s.endswith(("'", '"', "';", '";')):
                in_import = True  # 多行 import { ... } from '...'
            continue
        return False  # 遇到第一条正文语句仍无注释
    return False


def gate2() -> list[str]:
    violations = []
    base = ROOT / "web" / "src"
    for f in iter_files(base, (".ts", ".vue")):
        rel = f.relative_to(base).as_posix()
        if rel in G2_EXEMPT:
            continue
        lines = f.read_text(encoding="utf-8").splitlines()
        if f.suffix == ".ts":
            ok = _has_header_before_code(lines)
        else:  # .vue:文件首行 <!-- 注释,或 <script> 块内正文前有注释
            first = next((l.strip() for l in lines if l.strip()), "")
            ok = first.startswith("<!--")
            if not ok:
                for i, line in enumerate(lines):
                    if line.lstrip().startswith("<script"):
                        body = []
                        for l in lines[i + 1:]:
                            if l.lstrip().startswith("</script"):
                                break
                            body.append(l)
                        ok = _has_header_before_code(body)
                        break
        if not ok:
            violations.append(f"web/src/{rel}")
    return violations


# ---------------- G3 文档引用完整性 ----------------

MD_LINK = re.compile(r"\[[^\]]*\]\(([^)\s]+)\)")
BACKTICK_REF = re.compile(r"`([A-Za-z0-9_\-./]+\.(?:%s))`" % "|".join(e.lstrip(".") for e in G3_REF_EXTS))


def gate3() -> list[str]:
    # 仓库内全部文件名索引(含 external:引用厂家文件也应真实存在)
    all_files = iter_files(ROOT, G3_REF_EXTS + (".json", ".yml", ".yaml", ".props", ".sln", ".csproj", ".sh", ".html"))
    basenames = {p.name for p in all_files}
    relpaths = {p.relative_to(ROOT).as_posix() for p in all_files}

    violations = []
    for doc in G3_LIVING_DOCS:
        path = ROOT / doc
        if not path.exists():
            violations.append(f"{doc}: 活文档清单中的文件不存在")
            continue
        text = path.read_text(encoding="utf-8")
        for n, line in enumerate(text.splitlines(), 1):
            for m in MD_LINK.finditer(line):
                target = m.group(1).split("#")[0]
                if not target or target.startswith(("http://", "https://", "mailto:")):
                    continue
                resolved = (path.parent / target).resolve()
                if not resolved.exists():
                    violations.append(f"{doc}:{n}: 链接目标不存在 -> {m.group(1)}")
            for m in BACKTICK_REF.finditer(line):
                ref = m.group(1)
                if "/" in ref:
                    # 路径式引用:仓库根相对或文档相对,两种写法都认
                    if ref.lstrip("./") in relpaths or (path.parent / ref).resolve().exists():
                        continue
                    # 再宽松一步:按文件名兜底(路径写法多样,文件存在即可)
                    if Path(ref).name in basenames:
                        continue
                    violations.append(f"{doc}:{n}: 引用文件不存在 -> {ref}")
                elif ref not in basenames:
                    violations.append(f"{doc}:{n}: 引用文件不存在 -> {ref}")
    return violations


# ---------------- G4 遗留标记 ----------------

def gate4() -> list[str]:
    violations = []
    for base, pattern in ((ROOT / "src", G4_CSHARP), (ROOT / "tools", G4_CSHARP),
                          (ROOT / "web" / "src", G4_WEB)):
        suffixes = (".cs",) if pattern is G4_CSHARP else (".ts", ".vue")
        for f in iter_files(base, suffixes):
            if any(part in G1_EXCLUDE_DIRS for part in f.parts):
                continue
            for n, line in enumerate(f.read_text(encoding="utf-8").splitlines(), 1):
                if pattern.search(line):
                    violations.append(f"{f.relative_to(ROOT)}:{n}: {line.strip()[:100]}")
    return violations


# ---------------- G5 未引用导出(报告级) ----------------

# 裁决记录(2026-07):interface/type 导出若在本文件签名中被引用,属 API 面类型
# (消费方经返回值类型推断获得,不必显式 import),不算死代码,不再报告;
# 仅报告【全局零引用】的类型,与【其它文件未引用】的值导出(function/const 等)。
EXPORT_DECL = re.compile(r"^export\s+(?:async\s+)?(function|const|let|class|interface|type|enum)\s+(\w+)", re.M)
TYPE_KINDS = {"interface", "type"}


def gate5() -> list[str]:
    base = ROOT / "web" / "src"
    files = iter_files(base, (".ts", ".vue"))
    contents = {f: f.read_text(encoding="utf-8") for f in files}
    report = []
    for f, text in contents.items():
        if f.suffix != ".ts":
            continue
        for kind, name in EXPORT_DECL.findall(text):
            word = re.compile(rf"\b{re.escape(name)}\b")
            used_elsewhere = any(word.search(other_text)
                                 for other, other_text in contents.items() if other != f)
            if used_elsewhere:
                continue
            if kind in TYPE_KINDS:
                decl_count = len(re.findall(rf"^export\s+(?:{kind})\s+{re.escape(name)}\b", text, re.M))
                if len(word.findall(text)) - decl_count > 0:
                    continue  # 本文件签名在用 → API 面类型,保留
            report.append(f"{f.relative_to(ROOT)}: export {kind} {name} 疑似无引用")
    return report


# ---------------- G6 文风统一 ----------------

# 脚本自己就写着这些被禁的样式(下面的正则),不自查
G6_SELF = Path(__file__).resolve()
# 表情符号的几个 Unicode 区段。
# 箭头 →、点 ·、度 °、上标 ² 这些常用工程符号不在区段里,不会被误伤。
G6_EMOJI = re.compile("[\u2600-\u27bf\u2b00-\u2bff\ufe0f\U0001F000-\U0001FAFF]")
# 直角引号统一换成中文弯引号(全库通行样式,两种引号混着用最难看)
G6_MARK = re.compile(r"「|」")
# 宣传稿腔的套话。写文档就正常说话,别起“一句话概览”这种标题,
# 也别用“提供…等核心能力”“赋能/助力”这类堆砌词——工程文档不是产品软文。
G6_CLICHE = re.compile(
    r"一句话(概览|说明|总结)|一图看懂|等核心能力|核心亮点|旨在为|赋能|助力|保驾护航|"
    r"无缝(对接|集成|切换)|开箱即用|极大地|显著提升|全方位|深度融合|综上所述|值得注意的是|"
    r"强大的|轻松实现|为您提供|一览无余")

# 注释里给后果加戏的写法。同一件事说一句就够,堆到三四句是为了渲染而不是为了讲清楚
# ——“大屏不弹窗,日志里一片安静,值班看到的就是没有告警”,后两句没带来新信息。
# 也别替读者下判断(“这正是最需要被看见的”)或先自证重要性(“这不是假想场景”)。
G6_DRAMA = re.compile(
    r"一片安静|安安静静|等于告诉|这不是假想|正是最需要|恰是最需要|这是最糟|"
    r"最需要被看见|值得单独留|无迹可查|悄悄地|静静地")
G6_SUFFIXES = (".cs", ".ts", ".vue", ".md", ".sql", ".ps1", ".sh", ".py",
               ".yml", ".yaml", ".json", ".html", ".css")

# 中文标题后面挂一串英文,基本都是把标题自己翻了一遍,人不这么写文档。
# 括号里放【标识符】是正常的,要留:授权(license)、设备适配层接口(IFxConnector)。
# 判据是"纯英文单词、不带数字、两个词起":单词一律放行(标识符),
# 带数字的放行(GB/T 41261-2022、IEC 62443 这类标准号),混中文的放行(Phase 2 必做)。
# 注意别用 \w —— Python 的 \w 把汉字也算词字符,会把"（Phase 2 必做）"误判成英文。
G6_HEADING_EN = re.compile(r"^#{1,6} .*（([A-Za-z][A-Za-z_&-]*(?: [A-Za-z_&-]+)+)）\s*$")
G6_HEADING_OK = {"TimescaleDB hypertable"}


def gate6() -> list[str]:
    violations = []
    for f in iter_files(ROOT, G6_SUFFIXES):
        if f == G6_SELF:
            continue
        try:
            text = f.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            continue
        for n, line in enumerate(text.splitlines(), 1):
            if G6_EMOJI.search(line):
                violations.append(f"{f.relative_to(ROOT)}:{n}: 表情符号 -> {line.strip()[:80]}")
            m = G6_MARK.search(line)
            if m:
                violations.append(f"{f.relative_to(ROOT)}:{n}: 直角引号 '{m.group(0)}' -> {line.strip()[:80]}")
            m = G6_CLICHE.search(line)
            if m:
                violations.append(f"{f.relative_to(ROOT)}:{n}: 套话 '{m.group(0)}' -> {line.strip()[:80]}")
            m = G6_DRAMA.search(line)
            if m:
                violations.append(f"{f.relative_to(ROOT)}:{n}: 注释加戏 '{m.group(0)}' -> {line.strip()[:80]}")
            if f.suffix == ".md":
                m = G6_HEADING_EN.match(line)
                if m and m.group(1) not in G6_HEADING_OK:
                    violations.append(
                        f"{f.relative_to(ROOT)}:{n}: 标题挂英文直译 '{m.group(1)}' -> {line.strip()[:80]}")
    return violations


# ---------------- G7 导出物中文覆盖 ----------------

# 报告和 CSV 是打印出来交给客户的,上面不能出现 load_calibrate_countersign 这种码。
# 中文名统一放在 DisplayLabels.cs,这里核对代码里写进 audit_log 的每个码都登记过了。
# 之所以要这道门:这套对照表原来前端后端各存一份,后端加动作没人记得补前端那份,
# 结果 user_change_password / wiring_import 直接以英文出现在审计页面上。
LABELS_FILE = ROOT / "src/BoltPreload.Api/Endpoints/DisplayLabels.cs"
API_DIR = ROOT / "src/BoltPreload.Api"

G7_DICT = re.compile(
    r"Dictionary<string, string>\s+(\w+)\s*=\s*new\(StringComparer\.Ordinal\)\s*\{(.*?)\n    \};",
    re.S)
G7_CS_ENTRY = re.compile(r'\["([^"]+)"\]\s*=\s*"([^"]*)"')
# 三种写法都会往 audit_log 落动作:辅助方法、参数绑定、SQL 里直接写死
G7_HELPER = re.compile(r"Write\w*AuditAsync\((.{0,200}?)\)\s*;", re.S)
G7_BIND = re.compile(r'AddWithValue\("action",\s*NpgsqlDbType\.\w+,\s*([^;]{0,120}?)\)\s*;', re.S)
# 落库有 VALUES (...) 和 INSERT...SELECT 两种写法,后者用在"顺带把刚插入的行 to_jsonb 存进
# after_jsonb"的场合(自动建档、点位表导入等)。只认 VALUES 会漏掉这些文件里新增的动作码。
G7_SQL = re.compile(
    r"INSERT INTO audit_log.{0,400}?(?:VALUES\s*\(|SELECT)\s*[^,]+,\s*'([a-z_]+)'\s*,\s*'([a-z_]+)'", re.S)
G7_LIT = re.compile(r'"([a-z][a-z_]{3,})"')


def _cs_label_tables() -> dict[str, dict[str, str]]:
    """后端 DisplayLabels.cs 里的各张词表,解析成 表名 -> {码: 中文名}。"""
    text = LABELS_FILE.read_text(encoding="utf-8")
    return {name: dict(G7_CS_ENTRY.findall(body)) for name, body in G7_DICT.findall(text)}


def _registered_labels() -> dict[str, set[str]]:
    return {name: set(table) for name, table in _cs_label_tables().items()}


# 告警类型、标定方法这两张表前后端各要一份:后端出报告和 CSV,前端出页面。
# 同一条记录在页面上和在导出件里必须是同一个叫法。以前漂过——标定方法在四个页面
# 分别写成"正式载荷标定 / 正式载荷""GB 单波法 / GB单波""快速标定 / 快速",
# 各自为政谁也没发现。前端收敛到 labels.ts 之后,靠这道门盯住两边不再各改各的。
FRONT_LABELS = ROOT / "web/src/utils/labels.ts"
LABEL_TABLE_PAIRS = {          # 前端常量名 -> 后端词表名
    "ALARM_TYPE_LABELS": "AlarmTypes",
    "CALIBRATION_METHOD_LABELS": "CalibrationMethods",
}
G7_TS_ENTRY = re.compile(r"(\w+):\s*'([^']*)'")


def _frontend_label_sync() -> list[str]:
    if not FRONT_LABELS.exists():
        return [f"{FRONT_LABELS.relative_to(ROOT)} 不见了,前端对照表没了"]
    ts_text = FRONT_LABELS.read_text(encoding="utf-8")
    cs_tables = _cs_label_tables()

    out = []
    for ts_name, cs_name in LABEL_TABLE_PAIRS.items():
        blk = re.search(rf"\bconst {ts_name}[^{{]*\{{(.*?)\n\}}", ts_text, re.S)
        if not blk:
            out.append(f"前端 {ts_name} 找不到了,与后端 {cs_name} 的一致性无法核对")
            continue
        back = cs_tables.get(cs_name)
        if back is None:
            out.append(f"后端 DisplayLabels.{cs_name} 找不到了,与前端 {ts_name} 的一致性无法核对")
            continue
        front = dict(G7_TS_ENTRY.findall(blk.group(1)))
        for code in sorted(set(front) | set(back)):
            f, b = front.get(code), back.get(code)
            if f is None:
                out.append(f"'{code}' 后端 {cs_name} 登记了 '{b}',前端 {ts_name} 没有,页面上会露出英文码")
            elif b is None:
                out.append(f"'{code}' 前端 {ts_name} 登记了 '{f}',后端 {cs_name} 没有,导出件里会露出英文码")
            elif f != b:
                out.append(f"'{code}' 两边叫法不一致:页面写 '{f}',导出件写 '{b}'")
    return out


# 点位表要能导出→改→回导。表头在 C# 里写死,认表头的别名表在 Vue 里,
# 中间没有编译期约束:改了一边忘了另一边,现场导回来就是"缺通道号列"。
WIRING_EXPORT = ROOT / "src/BoltPreload.Api/Endpoints/WiringTableEndpoints.cs"
WIRING_IMPORT = ROOT / "web/src/views/ChannelIdentify.vue"
WIRING_FIELDS = ["channelNo", "positionLabel", "boltCode",
                 "nominalDiam", "grade", "targetPreloadN", "minPreloadN"]
# 按结构认表头行:非插值的 AppendLine、至少 7 列、不是 # 开头的说明行。
# 别拿某个中文词当锚点——表头一改就连行都找不到,门禁会报成"文件缺失"而不是"列对不上"。
G7_HDR = re.compile(r'csv\.AppendLine\("(?!#)([^"{}]*(?:,[^"{},]*){6,})"\);')
G7_ALIAS_BLOCK = re.compile(r"const HEADER_ALIASES[^{]*\{(.*?)\n\}", re.S)
G7_ALIAS = re.compile(r"([^\s,{]+):\s*'(\w+)'")


def _wiring_roundtrip() -> list[str]:
    if not (WIRING_EXPORT.exists() and WIRING_IMPORT.exists()):
        return []
    hdr_m = G7_HDR.search(WIRING_EXPORT.read_text(encoding="utf-8"))
    blk_m = G7_ALIAS_BLOCK.search(WIRING_IMPORT.read_text(encoding="utf-8"))
    if not hdr_m or not blk_m:
        return ["点位表导出表头或导入别名表找不到了,往返契约无法核对"]
    headers = hdr_m.group(1).split(",")
    alias = dict(G7_ALIAS.findall(blk_m.group(1)))

    out = []
    for i, field in enumerate(WIRING_FIELDS):
        if i >= len(headers):
            out.append(f"点位表导出少了第 {i + 1} 列(应为 {field}),导回去会认不出")
            continue
        h = headers[i]
        got = alias.get(h.lower() if h.isascii() else h)
        if got != field:
            out.append(f"点位表第 {i + 1} 列表头 '{h}' 在导入别名表里指向 {got or '无'},应为 {field}")
    # 只读列不能撞上可编辑字段,否则导入会拿错列覆盖数据
    for i, h in enumerate(headers[len(WIRING_FIELDS):], start=len(WIRING_FIELDS) + 1):
        got = alias.get(h.lower() if h.isascii() else h)
        if got is not None:
            out.append(f"点位表第 {i} 列 '{h}' 是只读信息列,却被导入别名表映射到 {got}")
    # 客户手里存着旧的英文表头文件,别名表不能把这些入口删掉
    legacy = ["channel_no", "position_label", "bolt_code",
              "nominal_diam", "grade", "target_preload_n", "min_preload_n"]
    for h, field in zip(legacy, WIRING_FIELDS):
        if alias.get(h) != field:
            out.append(f"旧英文表头 '{h}' 的兼容入口没了,客户存量点位表会导不进来")
    return out


def gate7() -> list[str]:
    if not LABELS_FILE.exists():
        return [f"{LABELS_FILE.relative_to(ROOT)} 不见了,导出物的中文对照表没了"]
    tables = _registered_labels()
    actions = tables.get("AuditActions", set())
    targets = tables.get("AuditTargetTypes", set())

    violations = []
    for f in sorted(API_DIR.rglob("*.cs")):
        # 本门禁不走 iter_files,AppleDouble(._xxx)要在这里单独排掉,理由同 iter_files
        if f == LABELS_FILE or f.name.startswith("._"):
            continue
        text = f.read_text(encoding="utf-8")
        rel = f.relative_to(ROOT)
        found_a, found_t = set(), set()
        for m in G7_HELPER.finditer(text):
            found_a.update(G7_LIT.findall(m.group(1)))
        for m in G7_BIND.finditer(text):
            found_a.update(G7_LIT.findall(m.group(1)))
        for act, tgt in G7_SQL.findall(text):
            found_a.add(act)
            found_t.add(tgt)
        # 辅助方法/参数绑定那两种取法会连带扫到同一处的 target_type、备注等字面量,
        # 凡是已登记为对象类型的就不再当动作追究,避免误报。
        for code in sorted(found_a - actions - targets):
            violations.append(f"{rel}: 审计动作 '{code}' 没在 DisplayLabels.AuditActions 里登记中文名")
        for code in sorted(found_t - targets):
            violations.append(f"{rel}: 审计对象类型 '{code}' 没在 DisplayLabels.AuditTargetTypes 里登记中文名")
    violations.extend(_wiring_roundtrip())
    violations.extend(_frontend_label_sync())
    return violations


# ---------------- 主入口 ----------------

GATES = {
    "g1": ("后端 public 类型注释覆盖", gate1, True),
    "g2": ("前端文件头注释", gate2, True),
    "g3": ("活文档引用完整性", gate3, True),
    "g4": ("遗留标记归零", gate4, True),
    "g5": ("前端未引用导出(报告级,不拦截)", gate5, False),
    "g6": ("文风统一(表情/引号/套话)", gate6, True),
    "g7": ("导出物中文覆盖(审计动作/对象类型)", gate7, True),
}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--gate", choices=sorted(GATES), help="只跑指定门禁")
    args = ap.parse_args()

    failed = False
    for key, (title, fn, blocking) in GATES.items():
        if args.gate and key != args.gate:
            continue
        items = fn()
        tag = "门禁" if blocking else "报告"
        print(f"== {key.upper()} {title} [{tag}] : {len(items)} 项")
        for it in items:
            print(f"   {it}")
        if items and blocking:
            failed = True
    print("== 结果:", "存在未归零门禁" if failed else "全部门禁归零")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
