#!/usr/bin/env python3
"""把 coverlet 的 cobertura XML 汇总成"哪些代码从没被执行过"的清单。

`dotnet test --collect:"XPlat Code Coverage"` 生成的 XML 只有逐行命中数，
直接读没法回答"该先补哪里"。这里按未覆盖行数排序输出，让缺口自己冒头。

用法：coverage-report.py <coverage.cobertura.xml> [--min-lines N]
"""
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    path = sys.argv[1]
    min_lines = 20
    if "--min-lines" in sys.argv:
        min_lines = int(sys.argv[sys.argv.index("--min-lines") + 1])

    tree = ET.parse(path)
    root = tree.getroot()

    # 同一个源文件可能被拆在多个 <class> 里（嵌套类型、局部函数），按文件名合并
    files: dict[str, dict[int, int]] = defaultdict(dict)
    pkg_of: dict[str, str] = {}

    for pkg in root.iter("package"):
        pkg_name = pkg.get("name", "?")
        for cls in pkg.iter("class"):
            fname = cls.get("filename", "?")
            pkg_of[fname] = pkg_name
            for line in cls.iter("line"):
                num = int(line.get("number", "0"))
                hits = int(line.get("hits", "0"))
                # 取最大命中：同一行可能在多个 class 条目里各记一次
                files[fname][num] = max(files[fname].get(num, 0), hits)

    rows = []
    for fname, lines in files.items():
        total = len(lines)
        covered = sum(1 for h in lines.values() if h > 0)
        rows.append((fname, pkg_of[fname], total, covered, total - covered))

    grand_total = sum(r[2] for r in rows)
    grand_covered = sum(r[3] for r in rows)
    print(f"整体行覆盖：{grand_covered}/{grand_total} = {grand_covered / grand_total:.1%}\n")

    by_pkg: dict[str, list[int]] = defaultdict(lambda: [0, 0])
    for _, pkg, total, covered, _ in rows:
        by_pkg[pkg][0] += total
        by_pkg[pkg][1] += covered
    print("按程序集：")
    for pkg, (total, covered) in sorted(by_pkg.items(), key=lambda kv: kv[1][0] - kv[1][1], reverse=True):
        print(f"  {covered / total:6.1%}  {covered:5d}/{total:-5d}  缺 {total - covered:5d}  {pkg}")

    zero = [r for r in rows if r[3] == 0 and r[2] >= min_lines]
    print(f"\n完全没被执行过的文件（>= {min_lines} 行，共 {len(zero)} 个）：")
    for fname, pkg, total, _, missed in sorted(zero, key=lambda r: -r[4]):
        print(f"  {missed:5d} 行  {short(fname)}")

    partial = [r for r in rows if r[3] > 0 and r[4] >= min_lines]
    print(f"\n部分覆盖、缺口最大的文件（缺 >= {min_lines} 行，共 {len(partial)} 个）：")
    for fname, pkg, total, covered, missed in sorted(partial, key=lambda r: -r[4])[:40]:
        print(f"  {covered / total:6.1%}  缺 {missed:5d} / {total:-5d}  {short(fname)}")
    return 0


def short(path: str) -> str:
    marker = "/bhlssdk/"
    return path[path.index(marker) + len(marker):] if marker in path else path


if __name__ == "__main__":
    sys.exit(main())
