# -*- coding: utf-8 -*-
"""APSS (LiQuilaz 颗粒计数器) Series Average Data Report 解析器。

规则全部外置在 apss_rules.json，本模块只负责执行，不含业务判断的硬编码。
独立脚本与 officetool 侧的集成共用这一个模块，避免两套实现对不上。

用法:
    from apss_parser import load_rules, parse_pdf
    rules = load_rules("apss_rules.json")
    r = parse_pdf(r"D:\\...\\xxx.pdf", rules)

返回的每条 record 形如:
    {
      "project": "QL2107",           # 该文件归属的项目（sheet 名）
      "raw_project": "QL2107",       # 本段自己写的 Project name
      "sample": "20240302 0h",
      "series_dt": "4/18/2024 1:19:14PM",   # 检测时间（Series Date & Time）
      "series_dt_key": "2024-04-18 13:19:14",  # 排序用的规范化时间
      "calibrated": "10/27/2022 3:33:47PM",
      "values": {2.0: 425.83, 10.0: 3.47, 25.0: 0.0},
      "channels": [2.0, 5.0, 10.0, 25.0],
    }
"""
from __future__ import annotations

import json
import os
import re
import sys
from datetime import datetime
from pathlib import Path

try:
    sys.stdout.reconfigure(errors="replace")
    sys.stderr.reconfigure(errors="replace")
except Exception:
    pass

HERE = Path(__file__).resolve().parent
DEFAULT_RULES = HERE / "apss_rules.json"


# ---------------------------------------------------------------- 规则加载
def load_rules(rules_path: str | os.PathLike | None = None) -> dict:
    p = Path(rules_path) if rules_path else DEFAULT_RULES
    with open(p, "r", encoding="utf-8") as f:
        return json.load(f)


def _num(s):
    """把 '1,131.61' 之类带千分位的字符串转成 float，失败返回 None。"""
    try:
        return float(str(s).replace(",", "").strip())
    except Exception:
        return None


def _norm_dt(s: str, rules: dict):
    """把 '4/18/2024 1:19:14PM' 规范化成 '2024-04-18 13:19:14'。失败返回 None。"""
    s = (s or "").strip()
    if not s:
        return None
    for fmt in rules["datetime"]["input_formats"]:
        try:
            return datetime.strptime(s, fmt).strftime("%Y-%m-%d %H:%M:%S")
        except ValueError:
            continue
    return None


_DATETIME_HEAD = re.compile(
    r"^([0-9]{1,2}/[0-9]{1,2}/[0-9]{2,4})[ \t]+"
    r"([0-9]{1,2}:[0-9]{2}:[0-9]{2}[ \t]*[AP]M)",
    re.I,
)


def _leading_datetime(s: str):
    m = _DATETIME_HEAD.match((s or "").strip())
    return (m.group(1) + " " + m.group(2)).strip() if m else None


# ---------------------------------------------------------------- PDF 文本
def extract_text(pdf_path: str | os.PathLike, rules: dict) -> tuple[str, int]:
    """返回 (全文, 页数)。

    注意（实测教训）：**不能用 pypdfium2 的 get_text_range()**。
    它是按内容流顺序输出，会把标签和值拆到不同行 —— 同一份文件里
    'Project name' 出现在第 10 行，而它的值 'QLS5314' 却在第 5 行。
    pdfplumber（底层 pdfminer）按坐标排序，才是正确的视觉阅读顺序。
    """
    import pdfplumber

    path = str(pdf_path)
    with pdfplumber.open(path) as pdf:
        pages = []
        for pg in pdf.pages:
            try:
                pages.append(pg.extract_text() or "")
            except Exception:
                pages.append("")
        return "\n".join(pages), len(pages)


# ---------------------------------------------------------------- 项目判定
def _dedupe_consecutive(values):
    """跨页会重复打印同一个 'Project name'，这里把连续重复的折叠成一个。"""
    out = []
    for v in values:
        if not out or v != out[-1]:
            out.append(v)
    return out


def canonical_project(v, rules: dict) -> str:
    """按 project.aliases 把「同一项目的不同写法」换成正确写法。

    映射由业务确认（如 QLLF32101 → QLF32101）。键与值都按大写匹配，
    所以 'qllf32101' / 'QLLF32101' 都会命中。未命中则原样（大写）返回。
    """
    v = (v or "").strip().upper()
    if not v:
        return v
    return (rules.get("project", {}).get("aliases") or {}).get(v, v)


def determine_project(raw_values, rules: dict) -> tuple[str, list, bool]:
    """按规则裁定文件归属的项目号。返回的大写形式为规范写法。

    返回 (project, distinct_values, conflict)
      project         裁定出的项目号（已归一化为大写）
      distinct_values 折叠后仍不同的取值（大小写不敏感）
      conflict        True 表示文件内存在多个互不相同的项目号
    """
    pc = rules["project"]
    vals = [v.strip() for v in raw_values if v and v.strip()]

    # 大小写不敏感地折叠：'ql1101' 与 'QL1101' 是同一个项目。
    # 不折叠会把一个项目的记录拆成两个 sheet，且 openpyxl 会对大小写同名的
    # sheet 静默追加序号（QL1101 -> ql11011 -> ql11012），导致每次运行 sheet 名漂移、
    # 增量去重失效。统一成大写作为规范形式。
    collapsed = _dedupe_consecutive([canonical_project(v, rules) for v in vals])
    distinct = list(dict.fromkeys(collapsed))

    pattern = re.compile(pc["number_pattern"],
                         re.I if "I" in pc.get("number_pattern_flags", "") else 0)
    prefixed = [v for v in collapsed if pattern.match(v)]

    pool = prefixed if (pc.get("prefer_prefixed") and prefixed) else collapsed
    if not pool:
        return "", distinct, len(distinct) > 1

    # tie_break: most_frequent_then_first_seen
    order = list(dict.fromkeys(pool))
    best = max(order, key=lambda v: pool.count(v))
    return best, distinct, len(distinct) > 1


# ---------------------------------------------------------------- 核心解析
def _label_value(lines, i, rest, rules):
    """取 'Project name XXX' 这类标签行的值。

    三种情况：
      1) 值与标签同行（主流）         → 直接用
      2) 标签行就是空值（操作员留空） → 返回 ''，**不得**取下一行，
         否则会抓到 'Containers Pooled 1 Container Volume 25.00 milliliter'
      3) 值确实换行到下一行           → 只有在下一行不像另一个标签时才采用
    """
    rest = (rest or "").strip()
    if rest:
        return rest, i
    nxt = lines[i + 1].strip() if i + 1 < len(lines) else ""
    if not nxt:
        return "", i
    for bad in rules["anchors"].get("value_not_labels", []):
        if nxt.startswith(bad):
            return "", i
    return nxt, i + 1


def parse_text(text: str, rules: dict) -> list[dict]:
    """把整份 PDF 的文本摊平成若干条 record（每个 Series Averages 块一条）。"""
    A = rules["anchors"]
    CH = rules["channels"]
    wanted = [round(float(x), 6) for x in CH["wanted"]]
    val_idx = int(CH.get("value_column_index", 2))

    lines = text.split("\n")
    n = len(lines)

    cur_proj = cur_samp = cur_cal = cur_series = ""
    records = []

    i = 0
    while i < n:
        ln = lines[i].strip()

        # --- Project name / Sample name（值与标签同行；极端情况下在下一行）
        if ln.startswith(A["project_label"]):
            val, i = _label_value(lines, i, ln[len(A["project_label"]):], rules)
            cur_proj = val
            i += 1
            continue

        if ln.startswith(A["sample_label"]):
            val, i = _label_value(lines, i, ln[len(A["sample_label"]):], rules)
            cur_samp = val
            i += 1
            continue

        # --- Calibrated：仪器校准时间
        m = re.search(A["calibrated_regex"], ln, re.I)
        if m:
            cur_cal = m.group(1).strip()
            i += 1
            continue

        # --- 报告段头部：Series Date & Time Configuration User / <datetime> ...
        if ln.startswith(A["series_header"]):
            dt = _leading_datetime(ln[len(A["series_header"]):])
            if dt is None and i + 1 < n:
                dt = _leading_datetime(lines[i + 1])
                if dt:
                    i += 1  # 该行只作为时间行被消费
            if dt:
                cur_series = dt
            i += 1
            continue

        # --- Series Averages 汇总块（本段的最终结果）
        if ln.startswith(A["average_block"]):
            head = None
            for j in range(i + 1, min(n, i + 14)):
                s = lines[j].strip()
                if s.startswith(A["average_column_header_starts_with"]) and \
                        A["average_column_header_must_contain"] in s:
                    head = j
                    break
            if head is not None:
                values, channels = {}, []
                k = head + 1
                while k < n and k < head + 40:
                    parts = lines[k].split()
                    if len(parts) < val_idx:
                        break
                    ch = _num(parts[0])
                    if ch is None:
                        break
                    ch = round(ch, 6)
                    channels.append(ch)
                    if ch in wanted:
                        v = _num(parts[val_idx - 1])
                        if v is not None:
                            values[ch] = v
                    k += 1
                records.append({
                    "raw_project": cur_proj,
                    "sample": cur_samp,
                    "series_dt": cur_series,
                    "series_dt_key": _norm_dt(cur_series, rules),
                    "calibrated": cur_cal,
                    "values": values,
                    "channels": channels,
                })
                i = head + 1
                continue
        i += 1

    return records


def parse_pdf(pdf_path, rules: dict, root: str | os.PathLike | None = None) -> dict:
    """解析单个 PDF，返回含 file / project / records 等字段的结果字典。"""
    p = Path(pdf_path)
    rel = str(p.relative_to(root)) if root else p.name
    res = {
        "file": rel,
        "abs_path": str(p),
        "ok": False,
        "error": "",
        "pages": 0,
        "text_len": 0,
        "projects_distinct": [],
        "project": "",
        "conflict": False,
        "records": [],
    }
    try:
        if not p.is_file() or p.stat().st_size == 0:
            res["error"] = "文件为空（0 字节）或损坏，不是有效 PDF"
            return res
    except Exception as e:
        res["error"] = "无法读取文件：%s" % e
        return res
    try:
        text, pages = extract_text(p, rules)
        res["pages"] = pages
        res["text_len"] = len(text)
        if not text.strip():
            res["error"] = "提不到文本（可能是扫描件/图片版 PDF，或损坏）"
            return res
    except Exception as e:
        res["error"] = "%s: %s" % (type(e).__name__, e)
        return res

    A = rules["anchors"]
    raw_projects = [
        ln[len(A["project_label"]):].strip()
        for ln in text.split("\n")
        if ln.strip().startswith(A["project_label"])
    ]
    proj, distinct, conflict = determine_project(raw_projects, rules)

    recs = parse_text(text, rules)
    for r in recs:
        r["file"] = rel
        r["project"] = proj

    res.update(ok=True, projects_distinct=distinct, project=proj,
               conflict=conflict, records=recs)
    return res


# ---------------------------------------------------------------- 牵手中：归类
def _cs(pattern: str):
    return re.compile(pattern)


def _levenshtein(a: str, b: str) -> int:
    while a and b and a[0] == b[0]:
        a, b = a[1:], b[1:]
    if a == b:
        return 0
    if not a:
        return len(b)
    if not b:
        return len(a)
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (ca != cb)))
        prev = cur
    return prev[-1]


def _is_suffix_variant(a: str, b: str, tp: dict) -> bool:
    """a/b 是否只差一个字母后缀 —— 有意的子项目编号，不是笔误。

    实测 QL1209A 与 QL1209 是两个真实存在的不同项目（A 版是独立编号），
    编辑距离 1 会被误判成笔误刷进「提示」sheet。这里按规则里的
    suffix_not_typo.pattern 判定：长的那个 = 短的 + 一个字母后缀 -> 豁免。
    """
    cfg = tp.get("suffix_not_typo", {})
    if not cfg.get("enabled", True):
        return False
    try:
        pat = re.compile(cfg.get("pattern", "^[A-Z]{1,3}[0-9]?$"))
    except re.error:
        return False
    for x, y in ((a, b), (b, a)):
        if x.startswith(y) and pat.match(x[len(y):]):
            return True
    return False


def _is_confirmed_distinct(a: str, b: str, tp: dict) -> bool:
    """a/b 是否属于「业务已确认不是同一个项目」的组合。顺序无关、大小写不敏感。"""
    for pair in tp.get("confirmed_distinct", []):
        if not isinstance(pair, (list, tuple)) or len(pair) != 2:
            continue
        x, y = str(pair[0]).strip().upper(), str(pair[1]).strip().upper()
        if {a.upper(), b.upper()} == {x, y}:
            return True
    return False


def classify(rec: dict, fileinfo: dict, rules: dict) -> tuple[list[str], list[str]]:
    """判断一条 record 的归类处理。

    返回 (exclude_reasons, warn_reasons)
      exclude_reasons 非空 -> 不进项目 sheet，写入「待复核」
      warn_reasons    非空 -> 照常入库，同时登记到「提示」sheet
    """
    exc, warn = [], []
    EX = rules["exclusions"]
    append = lambda lst, reason: lst.append(reason)

    if EX.get("project_not_a_number", {}).get("enabled"):
        pc = rules["project"]
        pattern = re.compile(pc["number_pattern"],
                             re.I if "I" in pc.get("number_pattern_flags", "") else 0)
        if not rec.get("project") or not pattern.match(rec["project"]):
            (exc if EX["project_not_a_number"].get("action") == "exclude" else warn) \
                .append(EX["project_not_a_number"]["reason"])

    for key in ("water_sample", "blank_sample"):
        r = EX.get(key, {})
        if r.get("enabled") and re.match(r["pattern"], (rec.get("sample") or "").strip()):
            (exc if r.get("action") == "exclude" else warn).append(r["reason"])

    if EX.get("empty_sample", {}).get("enabled") and not (rec.get("sample") or "").strip():
        rs = EX["empty_sample"]
        (exc if rs.get("action") == "exclude" else warn).append(rs["reason"])

    # 项目号别名纠正：原始写法与规范写法不同 -> 照常入库，登记到提示备查
    ap = rules.get("project", {}).get("alias_applied", {})
    if ap.get("enabled"):
        raw = (rec.get("raw_project") or "").strip()
        cp = canonical_project(raw, rules)
        if raw and cp != raw.upper():
            (exc if ap.get("action") == "exclude" else warn).append(
                "%s：%s → %s" % (ap["reason"], raw, cp))

    fm = EX.get("folder_mismatch", {})
    if fm.get("enabled") and rec.get("project"):
        segs = (rec.get("file") or "").split(os.sep)[:-1]   # 去掉文件名
        # 路径里的目录名也要过一遍别名，否则 QLF32101 放在 QLLF32101 目录下会误报
        hit = any(canonical_project(s, rules) == canonical_project(rec["project"], rules)
                  for s in segs)
        if segs and not hit:
            msg = "%s（路径：%s）" % (fm["reason"], "/".join(segs))
            (exc if fm.get("action") == "exclude" else warn).append(msg)

            # 疑似笔误：与路径中某个项目号编辑距离很近。只提示，不自动纠正。
            tp = EX.get("project_typo", {})
            if tp.get("enabled"):
                pc = rules["project"]
                pat = re.compile(pc["number_pattern"], re.I)
                maxd = int(tp.get("max_edit_distance", 2))
                best, bd = None, 99
                for s in segs:
                    su = canonical_project(s, rules)
                    if not pat.match(su):
                        continue
                    d = _levenshtein(rec["project"].upper(), su)
                    if d < bd:
                        bd, best = d, su
                if best is not None and 1 <= bd <= maxd:
                    a = rec["project"].upper()
                    if _is_suffix_variant(a, best, tp):
                        pass   # 只差字母后缀 = 有意的子项目编号（QL1209A vs QL1209），不报笔误
                    elif _is_confirmed_distinct(a, best, tp):
                        pass   # 业务已确认这两个是不同的项目（QL1205 vs QL0605），不报笔误
                    else:
                        (exc if tp.get("action") == "exclude" else warn).append(
                            "%s：%s vs 路径中的 %s（差 %d 个字符）"
                            % (tp["reason"], rec["project"], best, bd))

    cf = EX.get("project_conflict", {})
    if cf.get("enabled") and fileinfo.get("conflict"):
        (exc if cf.get("action") == "exclude" else warn).append(cf["reason"])

    return exc, warn


def channel_value(rec: dict, ch: float, rules: dict):
    """取某通道的 Average Cumulative Counts；缺失按 missing_policy 处理。

    键的容忍处理很重要：values 的键是 float，但记录一旦经过 JSON（片），
    Object key 会被强制转成字符串 '2.0'，直接 .get(2.0) 会全部落空。
    因此这里按数值比较，字符串键和浮点键都能命中。
    """
    want = float(ch)
    for k, v in (rec.get("values") or {}).items():
        try:
            if abs(float(k) - want) < 1e-9:
                return v
        except (TypeError, ValueError):
            continue
    return "" if rules["channels"].get("missing_policy") == "blank" else 0.0


if __name__ == "__main__":
    import pprint

    rules = load_rules()
    target = sys.argv[1] if len(sys.argv) > 1 else None
    if not target:
        print("usage: python apss_parser.py <some.pdf>")
        sys.exit(1)
    pprint.pprint(parse_pdf(target, rules), width=100, sort_dicts=False)
