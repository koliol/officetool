# APSS PDF 数据提取（颗粒计数器 Series Average Data Report）

把 LiQuilaz 颗粒计数器导出的 PDF 报告批量提取成 Excel：**每个项目号一个 sheet**，
行是每次检测，列是 ≥2μm / ≥10μm / ≥25μm 及其向上取整修约值。

规则与解析实现**只有一份**（`apss_rules.json` + `apss_parser.py`），
独立脚本与 officetool 集成共用，不会出现两套实现对不上的情况。

---

## 一、独立脚本用法

### 1. 改路径（改这两行就够了）

编辑 `apss_extract.py` 顶部的四个常量：

```python
PDF_DIR   = r"D:\WorkBoddy\office-pdf\APSS data"     # 要扫描的 PDF 根目录（递归）
XLSX_PATH = r"D:\WorkBoddy\office-pdf\APSS汇总.xlsx"  # 生成的 Excel 路径
```

### 2. 运行

```bash
# 用上面的默认路径；首次统计全部，之后只追加 Excel 里没有的记录
python apss_extract.py

# 临时换个位置
python apss_extract.py --pdf-dir X:\pdfs --xlsx X:\out\汇总.xlsx

# 其他参数
python apss_extract.py --dry-run        # 只出分析报告，不写 Excel
python apss_extract.py --rebuild        # 忽略缓存，全部重新解析
python apss_extract.py --limit 60       # 只处理前 N 个 PDF（调试）
python apss_extract.py --workers 4      # 调并发数（默认 8）
python apss_extract.py --force          # 跳过空值率异常检查（不建议）
```

依赖：`pip install pdfplumber openpyxl`

### 3. 看结果

本机 stdout 回显不可靠，**运行详情一律看报告文件**：Excel 同目录下的 `_apss_report.txt`。

---

## 二、输出表结构

每个项目号一个 sheet，第 1 行表头，之后按**检测时间升序**：

| 检测时间 | 样品名 | ≥2μm | ≥10μm | ≥25μm | ≥2μm修约 | ≥10μm修约 | ≥25μm修约 | 来源文件 |
|---|---|---|---|---|---|---|---|---|

- **检测时间**取自 PDF 报告段的 `Series Date & Time`（真实检测开始时间），
  **不是** `Calibrated`（那是仪器校准时间，同一校准周期内长期不变，无法反映先后）。
- **修约列**默认直接写向上取整后的数值；改 `excel.rounding.mode = "formula"` 可写成 `=ROUNDUP(...,0)` 公式。
- 另有两个辅助 sheet：
  - **待复核** —— 被排除的记录，逐条带排除原因（数据不丢，便于人工决定是否纳入）
  - **提示** —— 已入库但有异常的记录（多项目号、文件夹不一致、疑似笔误）

---

## 三、规则：改 `apss_rules.json`，不改代码

| 想做的事 | 改哪里 |
|---|---|
| 增减统计的通道 | `channels.wanted`（默认 `[2,10,25]`） |
| 换列标题 | `channels.labels` / `excel.columns` |
| 修约写数值还是公式 | `excel.rounding.mode` |
| 改项目号前缀 | `project.number_pattern`（默认 `^(QL\|PSB\|PSSB)`，大小写不敏感） |
| 水样要不要入库 | `exclusions.water_sample.action`：`exclude` → 待复核；`warn` → 照常入库 |
| Blank / 空样品名同理 | `exclusions.blank_sample` / `empty_sample` |
| 文件夹不一致是否排除 | `exclusions.folder_mismatch.action` |

每条排除规则的 `action`：
- `exclude` → 不进项目 sheet，写入「待复核」
- `warn` → 照常入库，同时登记到「提示」

### 已纳入的判定（对应原始需求第 5 条）

| 现象 | 处理 |
|---|---|
| 项目名不是标准项目号（H2O、Calibration、Container cleaning verification、BLANK of sample…） | 排除 → 待复核（333 条） |
| 样品名是水（Water / ddH2O / H2O / kongpingshui…） | 排除 → 待复核（1348 条） |
| 样品名是 Blank / 空 | 排除 → 待复核（319 / 269 条） |
| 文件内出现多个项目号 | **不排除**（规则已裁定取 QL/PSB 开头或出现最多者），登记到提示（951 条） |
| 所属文件夹与项目号不一致 | **不排除**，登记到提示（183 条） |
| 项目号与路径中项目号差 1~2 字符，疑似笔误 | **不自动纠正**，登记到提示并给出候选 |
| PDF 为 0 字节 / 解析失败 | 记入报告，不入库（10 个） |

> **为什么不自动纠正笔误**：实测 15 组候选中多数其实是真实存在的不同项目
> （QL1209A / QL1209、QL1101A / QL1101、QLS31904 / QLS31901……），自动归并会串数据。

---

## 四、PDF 结构的几个坑（改解析前务必知道）

1. **不能用 pypdfium2 的 `get_text_range()`**。它按内容流顺序输出，会把标签和值拆到不同行
   ——实测同一份文件里 `Project name` 在第 10 行，而它的值 `QLS5314` 却在第 5 行。
   **必须用 pdfplumber**（底层 pdfminer 按坐标排序）。
2. **一个 PDF 可以是几十个报告段拼接**（最多见过 51 页 / 62 段）。
   每个段 = `Project name` → `Sample name` → 若干原始 Sample → **`Series Averages`** 汇总块。
   汇总块才是最终结果；配对必须取「Series Averages 前面最近」的那个 Sample name，
   因为跨页时 `Project name`/`Sample name` 会被重打一遍。
3. **通道行数不固定**：2/5/10/25、2/5/10/15/25、2/5/7.5/8/10/12 都见过，必须按规则筛选。
4. **Series Averages 第 2 列才是 `Average Cumulative Counts`**（第 3 列是 Differential，不能取）。
5. **`Sample name` 可能整行为空**（操作员没填），此时**不能**取下一行当值
   ——会抓到 `Containers Pooled 1 Container Volume 25.00 milliliter`。
6. **JSON 缓存会让 `values` 的 float 键变成字符串**（Object key 只能是 string），
   取值必须按数值比较而非 `.get(2.0)`。已在 `channel_value` 处理。
7. **openpyxl 对大小写同名的 sheet 会静默追加序号**（`QL1101` → `ql11011` → `ql11012`），
   所以项目号判定必须大小写不敏感并统一成大写，否则 sheet 名每次运行都漂移、增量去重失效。

---

## 五、集成到 officetool

推荐形态（规则已抽成配置，双端共用，算法只有一份）：

1. 本目录整体放进仓库 `tools/apss-extract/`，规则文件路径随包发布。
2. officetool 后端在「上传 PDF → 提取到模板 Excel」的动作里，以进程方式调用 `apss_extract.py`，
   用 `--pdf-dir` 指向临时上传目录、`--xlsx` 指向模板/输出文件。
3. 容器内需装 Python 3 与 `pdfplumber` + `openpyxl`；若不想引入 Python 依赖，
   则在 .NET 侧用 PdfPig 实现同样的**遍历规则**，但规则文件仍读同一份 `apss_rules.json`。

> 无论哪条路，**不要另写一套硬编码的通道/项目判定**——那正是规则文件存在的意义。

---

## 六、本次跑批结果（2026-09-29）

| 项 | 值 |
|---|---|
| PDF 文件 | 1864（其中 10 个 0 字节/解析失败） |
| 报告段（Series Averages 块） | 8655 |
| 正常入库 | 6634 条，分 90 个项目 sheet |
| 待复核（已排除） | 2020 条 |
| 提示（已入库但异常） | 1041 条 |
| 全量解析耗时 | 约 150 秒（8 并发）；命中缓存后约 5 秒 |

独立校验（不复用解析代码，直接回读 PDF 原文比对）5 项全部通过：
检测时间升序 / 修约=向上取整 / 随机抽样 40 条与原文一致 / 人工锚点 4 条 /
每条报告段在 Excel 中恰好出现一次（双向差集为 0）。
