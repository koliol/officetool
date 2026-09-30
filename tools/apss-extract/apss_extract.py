# -*- coding: utf-8 -*-
"""APSS PDF 数据提取 -> xlsx（增量）。

【这是你要改的地方】下面这四个常量：
    PDF_DIR    要扫描的 PDF 根目录（递归）
    XLSX_PATH  生成的 Excel 文件路径
    其余两个可以保持默认

命令行亦可覆盖：
    python apss_extract.py                       # 用上面的默认路径，增量运行
    python apss_extract.py --pdf-dir X --xlsx Y  # 临时换个位置
    python apss_extract.py --dry-run             # 只出分析报告，不写 Excel
    python apss_extract.py --rebuild             # 忽略缓存，全部重新解析
    python apss_extract.py --workers 4           # 调并发数

抽取规则全部在 apss_rules.json，改规则不用动这里。
首次运行统计全部 PDF；之后再运行只把 Excel 里没有的记录追加进去。
运行详情写入 REPORT_PATH（本机 stdout 回显不可靠，一律看报告文件）。
"""

# ============================ 路径配置 ============================
PDF_DIR = r"D:\WorkBoddy\office-pdf\APSS data"
XLSX_PATH = r"D:\WorkBoddy\office-pdf\APSS汇总.xlsx"
# =================================================================

import argparse
import json
import logging
import math
import os
import re
import sys
import time
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path

try:
    sys.stdout.reconfigure(errors="replace")
    sys.stderr.reconfigure(errors="replace")
except Exception:
    pass

HERE = Path(__file__).resolve().parent
DEFAULT_RULES = HERE / "apss_rules.json"

sys.path.insert(0, str(HERE))
import apss_parser as P  # noqa: E402

from openpyxl import Workbook, load_workbook                      # noqa: E402
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side  # noqa: E402
from openpyxl.utils import get_column_letter                      # noqa: E402

logging.getLogger("pdfminer").setLevel(logging.ERROR)
logging.getLogger("pdfplumber").setLevel(logging.ERROR)

INVALID_SHEET_CHARS = re.compile(r"[\[\]:*?/\\]")
def _worker(args):
    path, rules, root = args
    try:
        return P.parse_pdf(path, rules, root=root)
    except Exception as e:
        p = Path(path)
        return {"file": str(p.relative_to(root)) if root else p.name, "abs_path": str(p),
                "ok": False, "error": "%s: %s" % (type(e).__name__, e),
                "pages": 0, "text_len": 0, "projects_distinct": [],
                "project": "", "conflict": False, "records": []}


def sheet_name(project: str, rules: dict) -> str:
    name = (project or rules["excel"].get("default_sheet", "未归类")).strip()
    name = INVALID_SHEET_CHARS.sub("_", name) or "未归类"
    return name[:31]


def _norm_key(v, rules):
    """把检测时间统一成 'YYYY-MM-DD HH:MM:SS'，作为去重键的一部分。

    两侧必须同格式，否则增量去重永远失配（实测踩过）：
    记录里 series_dt 是 PDF 原文 '4/18/2024 1:19:14PM'，
    而 Excel 里存的是 datetime 单元格（读回来是 datetime 对象）。
    """
    if isinstance(v, datetime):
        return v.strftime("%Y-%m-%d %H:%M:%S")
    return P._norm_dt(str(v or ""), rules) or ""


def _dt_key(s):
    """把单元格里的检测时间转成可排序的值。"""
    if isinstance(s, datetime):
        return s
    s = (s or "").strip()
    if s:
        k = P._norm_dt(s, _RULES)
        if k:
            return datetime.strptime(k, "%Y-%m-%d %H:%M:%S")
    return datetime.max


def _cell_dt(s):
    """能解析就写成 datetime 单元格（便于排序），否则保留原字符串。"""
    k = P._norm_dt(s, _RULES) if isinstance(s, str) else None
    if k:
        return datetime.strptime(k, "%Y-%m-%d %H:%M:%S")
    return s if s else ""


def _roundup(v):
    return int(math.ceil(round(float(v), 9)))


_THIN = Side(style="thin")
BORDER = Border(left=_THIN, right=_THIN, top=_THIN, bottom=_THIN)
CENTER = Alignment(horizontal="center", vertical="center")
HEAD_FONT = Font(name="微软雅黑", bold=True, size=10)
HEAD_FILL = PatternFill(start_color="D9E1F2", end_color="D9E1F2", fill_type="solid")


def _style_header(ws, cols):
    """写表头并保证样式。注意：必须先写值再设样式，否则只会得到一格空的带底色的单元格。"""
    for c, name in enumerate(cols, 1):
        cell = ws.cell(row=1, column=c, value=name)
        cell.font = HEAD_FONT
        cell.fill = HEAD_FILL
        cell.border = BORDER
        cell.alignment = CENTER


def _data_row(ws, r, values, rounded_cols=None):
    for c, v in enumerate(values, 1):
        cell = ws.cell(row=r, column=c, value=v)
        cell.border = BORDER
        cell.alignment = CENTER
    if rounded_cols:
        for c in rounded_cols:
            ws.cell(row=r, column=c).number_format = "0"


# ---------------------------------------------------------------- 主流程
_RULES = None


def main() -> int:
    global _RULES
    ap = argparse.ArgumentParser()
    ap.add_argument("--pdf-dir", default=PDF_DIR)
    ap.add_argument("--xlsx", default=XLSX_PATH)
    ap.add_argument("--rules", default=str(DEFAULT_RULES))
    ap.add_argument("--cache", default="", help="默认与 Excel 同目录下的 _apss_cache.json")
    ap.add_argument("--report", default="")
    ap.add_argument("--workers", type=int, default=8)
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--rebuild", action="store_true")
    ap.add_argument("--fresh", action="store_true",
                    help="不读旧 Excel，从空表全量重写（改了项目号/别名规则、sheet 划分会变时必须用）")
    ap.add_argument("--limit", type=int, default=0, help="只处理前 N 个 PDF（调试用）")
    ap.add_argument("--force", action="store_true", help="跳过空值率异常检查（不建议）")
    a = ap.parse_args()

    rules = _RULES = P.load_rules(a.rules)
    EX = rules["excel"]
    COLS = EX["columns"]
    RVW = EX["review_columns"]
    HINT = EX["hint_columns"]
    REVIEW_SHEET = EX.get("review_sheet", "待复核")
    HINT_SHEET = EX.get("hint_sheet", "提示")
    CH = rules["channels"]
    CHS = [round(float(x), 6) for x in CH["wanted"]]

    pdf_dir = Path(a.pdf_dir)
    xlsx = Path(a.xlsx)
    cache_p = Path(a.cache) if a.cache else xlsx.with_name("_apss_cache.json")
    report_p = Path(a.report) if a.report else xlsx.with_name("_apss_report.txt")

    t0 = time.time()
    lines = []
    say = lambda *t: (print(*t), lines.append(" ".join(str(x) for x in t)))

    say("PDF 目录 :", pdf_dir)
    say("输出 Excel:", xlsx)
    say("规则文件 :", a.rules)

    files = sorted((str(p) for p in pdf_dir.rglob("*.pdf")), key=lambda s: s.lower())
    if a.limit:
        files = files[: a.limit]
    say("发现 PDF  :", len(files))
    if not files:
        say("没有 PDF，结束。")
        _write_report(report_p, lines)
        return 0

    # ---- 缓存：避免每次都重新解析全部 PDF
    cache = {}
    if not a.rebuild and cache_p.is_file():
        try:
            cache = json.loads(cache_p.read_text(encoding="utf-8"))
        except Exception:
            cache = {}

    todo, cached, need = [], 0, []
    for f in files:
        p = Path(f)
        rel = str(p.relative_to(pdf_dir))
        try:
            st = p.stat()
        except Exception:
            continue
        hit = cache.get(rel)
        if hit and hit.get("size") == st.st_size and hit.get("mtime") == int(st.st_mtime):
            cached += 1
            need.append(hit["res"])
        else:
            todo.append((f, rules, str(pdf_dir)))
    say("缓存命中  :", cached, " 需解析:", len(todo))

    results = list(need)
    if todo:
        from concurrent.futures import ProcessPoolExecutor, as_completed
        done = 0
        with ProcessPoolExecutor(max_workers=max(1, a.workers)) as ex:
            futs = [ex.submit(_worker, arg) for arg in todo]
            for fu in as_completed(futs):
                results.append(fu.result())
                done += 1
                if done % 200 == 0:
                    print("  解析 %d/%d" % (done, len(todo)))
    say("解析完成  : %.1fs" % (time.time() - t0))

    # 重建缓存（按相对路径）
    new_cache = {}
    for res in results:
        rel = res["file"]
        p = pdf_dir / rel
        if p.is_file():
            st = p.stat()
            new_cache[rel] = {"size": st.st_size, "mtime": int(st.st_mtime), "res": res}
    try:
        cache_p.parent.mkdir(parents=True, exist_ok=True)
        cache_p.write_text(json.dumps(new_cache, ensure_ascii=False), encoding="utf-8")
    except Exception as e:
        say("缓存写入失败（不影响结果）:", e)

    # ---- 归类
    kept = defaultdict(list)      # sheet -> [record]
    warnings = []                 # [(warn_reasons, record)]  仍入库，另登记到「提示」
    review = []                   # [(exc_reasons, record)]   排除，写入「待复核」
    n_rec = 0
    fail = [r for r in results if not r["ok"] and r["error"]]
    for res in results:
        for rec in res["records"]:
            n_rec += 1
            # 缓存里存的是**上次运行时**裁定出的项目号，改了 project.aliases 后它仍是旧写法。
            # 这里再过一遍别名，保证增删别名只须 --fresh 重跑，不必 --rebuild 全量重解析。
            # （raw_project 保持原样，供「提示」sheet 留痕用。）
            if rec.get("project"):
                rec["project"] = P.canonical_project(rec["project"], rules)
            exc_r, warn_r = P.classify(rec, res, rules)
            if exc_r:
                review.append((exc_r, rec))
            else:
                kept[sheet_name(rec.get("project"), rules)].append(rec)
                if warn_r:
                    warnings.append((warn_r, rec))

    # ---- 空值率自检：曾经因为 values 的 float 键过不了 JSON 缓存，
    #      导致 80% 的记录三个通道全空——写出来的表「跑通了但全是空的」。这道闸门必须留着。
    if n_rec:
        no_val = sum(1 for res in results for rec in res["records"]
                     if not (rec.get("values") or {}))
        ratio = no_val / n_rec
        say("三个通道全空的记录数  :", no_val, "(%.1f%%)" % (ratio * 100))
        if ratio > 0.20 and not a.force:
            say("")
            say("!! FATAL: 空值率 %.0f%% 异常偏高（>20%%），拒绝写入 Excel。" % (ratio * 100))
            say("   最常见原因是记录经 JSON 缓存往返后取值键失效；请检查 apss_parser.channel_value。")
            say("   确认数据确实如此才加 --force。")
            _write_report(report_p, lines)
            return 1

    say("")
    say("报告段总数:", n_rec)
    say("正常入库  :", sum(len(v) for v in kept.values()), "（分", len(kept), "个项目 sheet）")
    say("仅提示    :", len(warnings), "（已入库，另登记到「提示」sheet）")
    say("待复核    :", len(review), "（已排除出项目 sheet）")
    say("解析失败  :", len(fail))
    reason_counter = Counter()
    for rs, _ in review:
        for r in rs:
            reason_counter["[排除] " + re.sub(r"（路径：.*）$", "", r)] += 1
    for rs, _ in warnings:
        for r in rs:
            reason_counter["[提示] " + re.sub(r"（路径：.*）$", "", r)] += 1
    if reason_counter:
        say("")
        say("=== 归类原因分布 ===")
        for r, c in reason_counter.most_common(12):
            say("   %-44s %d" % (r, c))

    if a.dry_run:
        say("")
        say("=== dry-run：未写 Excel ===")
        for k in sorted(kept):
            say("   sheet %-30s %d 行" % (k, len(kept[k])))
        for f in fail[:20]:
            say("   FAIL %s  %s" % (f["file"], f["error"][:70]))
        _write_report(report_p, lines)
        return 0

    # ---- 读已有 Excel，构造去重键
    # 注意：增量是按 sheet 名去重的。一旦规则变化导致项目号改名/合并（例如加了别名），
    # 旧 sheet 名在 Excel 里仍然存在且不会被清理，同名记录会被当成"新 sheet 里没有"
    # 再写一遍 -> 旧 sheet 残留 + 新 sheet 重复。这类情况必须加 --fresh 从空表重写。
    existing_keys = defaultdict(set)
    if a.fresh:
        say("")
        say("=== --fresh：忽略旧 Excel，从空表全量重写 ===")
        wb = Workbook()
        wb.remove(wb.active)
    elif xlsx.is_file():
        wb = load_workbook(xlsx)
        for ws in wb.worksheets:
            if ws.max_row < 2:
                continue
            hdr = [c.value for c in ws[1]]
            try:
                i_dt = hdr.index("检测时间")
                i_smp = hdr.index("样品名")
                i_file = hdr.index("来源文件")
            except ValueError:
                continue
            for row in ws.iter_rows(min_row=2, values_only=True):
                v_dt, v_smp, v_f = row[i_dt], row[i_smp], row[i_file]
                existing_keys[ws.title].add(
                    (str(v_f or ""), str(v_smp or ""), _norm_key(v_dt, rules)))
        wb.close()
    else:
        wb = Workbook()
        wb.remove(wb.active)

    # ---- 新行
    def key_of(rec):
        return (rec.get("file", ""), rec.get("sample", ""),
                _norm_key(rec.get("series_dt", ""), rules))

    new_rows = defaultdict(list)
    for sh, recs in kept.items():
        seen = set()
        for rec in recs:
            if key_of(rec) in seen:
                continue                       # 同一次运行内的重复
            seen.add(key_of(rec))
            if key_of(rec) in existing_keys[sh]:
                continue                       # Excel 里已有，跳过
            new_rows[sh].append(rec)

    new_review = []
    seen_r = set()
    for rs, rec in review:
        k = (tuple(rs),) + key_of(rec)
        if k in seen_r or key_of(rec) in existing_keys[REVIEW_SHEET]:
            continue
        seen_r.add(k)
        new_review.append((rs, rec))

    new_hint = []
    seen_h = set()
    for rs, rec in warnings:
        k = (tuple(rs),) + key_of(rec)
        if k in seen_h or key_of(rec) in existing_keys[HINT_SHEET]:
            continue
        seen_h.add(k)
        new_hint.append((rs, rec))

    n_new = sum(len(v) for v in new_rows.values())
    say("")
    say("已有记录  :", sum(len(v) for v in existing_keys.values()))
    say("本次新增  :", n_new, "（待复核新增", len(new_review), "，提示新增", len(new_hint), "）")

    rounding_mode = EX["rounding"]["mode"]
    decimals = int(EX["rounding"]["decimals"])

    def build_data(rec, with_project=False):
        vals = [P.channel_value(rec, c, rules) for c in CHS]
        row = [_cell_dt(rec.get("series_dt", "")), rec.get("sample", "")] + vals
        if rounding_mode == "formula":
            row += [""] * len(CHS)
        else:
            row += [_roundup(v) if v != "" else "" for v in vals]
        return row

    # ---- 写项目 sheet
    for sh in sorted(set(list(new_rows.keys()) + list(kept.keys()))):
        ws = wb[sh] if sh in wb.sheetnames else wb.create_sheet(sh)
        if ws.max_row < 1:
            _style_header(ws, COLS)
        start = ws.max_row + 1 if ws.max_row >= 1 else 2
        rows = []
        # 已存在的行也要读进来一起重排（保证按检测时间有序）
        for row in ws.iter_rows(min_row=2, values_only=True):
            if any(v is not None and v != "" for v in row):
                rows.append((_dt_key(row[0]), None, row))
        for rec in new_rows.get(sh, []):
            rows.append((_dt_key(rec.get("series_dt", "")), rec, None))
        rows.sort(key=lambda t: (t[0], str(t[2][8] if t[2] else t[1].get("file", ""))))
        r = 2
        for _, rec, old in rows:
            if rec is not None:
                data = build_data(rec) + [rec.get("file", "")]
                for c, v in enumerate(data, 1):
                    cell = ws.cell(row=r, column=c, value=v)
                    cell.border = BORDER
                    cell.alignment = CENTER
                    if c == 1 and isinstance(v, datetime):
                        cell.number_format = "m/d/yyyy h:mm:ss AM/PM"
                if rounding_mode == "formula":
                    for i, _ in enumerate(CHS):
                        col = get_column_letter(3 + len(CHS) + i)
                        ws.cell(row=r, column=3 + len(CHS) + i,
                                value="=ROUNDUP(%s%d,%d)" % (get_column_letter(3 + i), r, decimals))
                        ws.cell(row=r, column=3 + len(CHS) + i).border = BORDER
                        ws.cell(row=r, column=3 + len(CHS) + i).alignment = CENTER
            else:
                for c, v in enumerate(old[:len(COLS)], 1):
                    cell = ws.cell(row=r, column=c, value=v)
                    cell.border = BORDER
                    cell.alignment = CENTER
                    if c == 1 and isinstance(v, datetime):
                        cell.number_format = "m/d/yyyy h:mm:ss AM/PM"
            r += 1
        # 清掉尾部多余行
        while ws.max_row >= r:
            ws.delete_rows(ws.max_row)
        _style_header(ws, COLS)
        for i, w in enumerate([20, 34, 12, 12, 12, 12, 12, 12, 46], 1):
            ws.column_dimensions[get_column_letter(i)].width = w
        if EX.get("freeze_header"):
            ws.freeze_panes = "A2"
        if EX.get("auto_filter") and ws.max_row > 1:
            ws.auto_filter.ref = "A1:%s%d" % (get_column_letter(len(COLS)), ws.max_row)

    # ---- 写提示 sheet（这类记录已进项目 sheet，这里只做异常登记）
    if new_hint or HINT_SHEET in existing_keys or warnings:
        sh = HINT_SHEET
        ws = wb[sh] if sh in wb.sheetnames else wb.create_sheet(sh)
        _style_header(ws, HINT)
        r = ws.max_row + 1 if ws.max_row >= 1 else 2
        for rs, rec in new_hint:
            data = ["; ".join(rs), _cell_dt(rec.get("series_dt", "")), rec.get("sample", ""),
                    rec.get("project", ""), rec.get("file", "")]
            for c, v in enumerate(data, 1):
                cell = ws.cell(row=r, column=c, value=v)
                cell.border = BORDER
                cell.alignment = CENTER
                if c == 2 and isinstance(v, datetime):
                    cell.number_format = "m/d/yyyy h:mm:ss AM/PM"
            r += 1
        for i, w in enumerate([34, 20, 34, 16, 46], 1):
            ws.column_dimensions[get_column_letter(i)].width = w
        if EX.get("freeze_header"):
            ws.freeze_panes = "A2"
        if EX.get("auto_filter") and ws.max_row > 1:
            ws.auto_filter.ref = "A1:%s%d" % (get_column_letter(len(HINT)), ws.max_row)

    # ---- 写待复核 sheet（真正被排除的记录，数据不丢，便于人工决定是否纳入）
    if new_review or REVIEW_SHEET in existing_keys or review:
        sh = REVIEW_SHEET
        ws = wb[sh] if sh in wb.sheetnames else wb.create_sheet(sh)
        _style_header(ws, RVW)
        r = ws.max_row + 1 if ws.max_row >= 1 else 2
        for rs, rec in new_review:
            vals = [P.channel_value(rec, c, rules) for c in CHS]
            top = (rec.get("file") or "").split(os.sep)[0]
            data = ["; ".join(rs), _cell_dt(rec.get("series_dt", "")), rec.get("sample", ""),
                    rec.get("project", ""), top] + vals + [rec.get("file", "")]
            for c, v in enumerate(data, 1):
                cell = ws.cell(row=r, column=c, value=v)
                cell.border = BORDER
                cell.alignment = CENTER
                if c == 2 and isinstance(v, datetime):
                    cell.number_format = "m/d/yyyy h:mm:ss AM/PM"
            r += 1
        for i, w in enumerate([30, 20, 34, 16, 16, 12, 12, 12, 46], 1):
            ws.column_dimensions[get_column_letter(i)].width = w
        if EX.get("freeze_header"):
            ws.freeze_panes = "A2"
        if EX.get("auto_filter") and ws.max_row > 1:
            ws.auto_filter.ref = "A1:%s%d" % (get_column_letter(len(RVW)), ws.max_row)

    xlsx.parent.mkdir(parents=True, exist_ok=True)
    wb.save(xlsx)
    say("")
    say("已保存    :", xlsx)
    say("总耗时    : %.1fs" % (time.time() - t0))

    _write_report(report_p, lines)
    return 0


def _write_report(p: Path, lines):
    try:
        if isinstance(p, Path):
            p.write_text("\n".join(lines), encoding="utf-8")
    except Exception as e:
        print("报告写入失败:", e)


if __name__ == "__main__":
    sys.exit(main())
