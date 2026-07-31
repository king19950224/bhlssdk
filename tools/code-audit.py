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
                      禁辅助工具路径/署名字样混入代码与文档

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
    "资料/工程文档/delivery-master-plan.md",
    "资料/工程文档/troubleshooting.md",
    "资料/工程文档/probe-scan-design.md",
    "资料/工程文档/torque-step-test-protocol.md",
    "资料/工程文档/commercialization-roadmap.md",
    "资料/工程文档/phase2-backlog.md",
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
# 直角引号统一换成中文弯引号;工具路径与署名字样不入库
G6_MARK = re.compile(r"「|」|\.cursor\b|generated by|chatgpt|claude|copilot|ai ?生成|ai ?辅助", re.I)
G6_SUFFIXES = (".cs", ".ts", ".vue", ".md", ".sql", ".ps1", ".sh", ".py",
               ".yml", ".yaml", ".json", ".html", ".css")


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
                violations.append(f"{f.relative_to(ROOT)}:{n}: 禁用字样 '{m.group(0)}' -> {line.strip()[:80]}")
    return violations


# ---------------- 主入口 ----------------

GATES = {
    "g1": ("后端 public 类型注释覆盖", gate1, True),
    "g2": ("前端文件头注释", gate2, True),
    "g3": ("活文档引用完整性", gate3, True),
    "g4": ("遗留标记归零", gate4, True),
    "g5": ("前端未引用导出(报告级,不拦截)", gate5, False),
    "g6": ("文风统一(表情/引号/工具痕迹)", gate6, True),
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
