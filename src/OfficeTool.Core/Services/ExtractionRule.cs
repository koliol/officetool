using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OfficeTool.Core.Exceptions;

namespace OfficeTool.Core.Services;

/// <summary>
/// 提取规则（RuleJson）的解析与执行。
///
/// 设计取舍：**引擎只吃纯文本**，不认识 PDF，也不碰数据库。
/// 这样规则的行为可以在单元测试里用一段文本完整覆盖 —— 真实 PDF 的抽取
/// 只负责"把版面变成文本行"，一旦抽出来的行不对，问题定位非常明确。
///
/// 规则结构（示例见内置的不溶性微粒规则）：
/// <code>
/// {
///   "templateCode": "INSOLUBLE_PARTICLES",
///   "targets": [
///     { "field": "batchNo", "label": "批号",
///       "extractors": [ { "type": "labelValue", "labels": ["批号", "Batch No"] } ] },
///     { "field": "items", "kind": "table",
///       "extractors": [ { "type": "tableRows", "rowPattern": "...", "columns": {...} } ] }
///   ]
/// }
/// </code>
/// </summary>
public static partial class ExtractionRule
{
    public const int MaxTargets = 100;

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex KeyPattern();

    public static ExtractionRuleSet Parse(string? ruleJson)
    {
        if (string.IsNullOrWhiteSpace(ruleJson))
        {
            throw new InvalidRuleException("规则内容为空。");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(ruleJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidRuleException($"规则不是合法 JSON：{ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidRuleException("规则必须是 JSON 对象。");
            }

            if (!root.TryGetProperty("targets", out var targetsElement) ||
                targetsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidRuleException("规则缺少 targets 数组。");
            }

            var templateCode = root.TryGetProperty("templateCode", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? string.Empty
                : string.Empty;

            var targets = new List<ExtractionTarget>();
            var index = 0;

            foreach (var targetElement in targetsElement.EnumerateArray())
            {
                if (targets.Count >= MaxTargets)
                {
                    throw new InvalidRuleException($"规则目标数超过上限 {MaxTargets}。");
                }

                targets.Add(ReadTarget(targetElement, index++));
            }

            if (targets.Count == 0)
            {
                throw new InvalidRuleException("规则至少要有一个目标字段。");
            }

            return new ExtractionRuleSet(templateCode, targets);
        }
    }

    private static ExtractionTarget ReadTarget(JsonElement element, int index)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidRuleException($"targets[{index}] 必须是对象。");
        }

        var field = ReadString(element, "field");
        if (string.IsNullOrWhiteSpace(field) || !KeyPattern().IsMatch(field))
        {
            throw new InvalidRuleException($"targets[{index}].field 不合法：{field}");
        }

        var kind = ReadString(element, "kind");
        var isTable = string.Equals(kind, "table", StringComparison.OrdinalIgnoreCase);

        if (!element.TryGetProperty("extractors", out var extractorsElement) ||
            extractorsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidRuleException($"targets[{index}].extractors 必须是数组。");
        }

        var extractors = new List<FieldExtractor>();
        var extractorIndex = 0;

        foreach (var extractorElement in extractorsElement.EnumerateArray())
        {
            extractors.Add(ReadExtractor(extractorElement, $"targets[{index}].extractors[{extractorIndex}]", isTable));
            extractorIndex++;
        }

        if (extractors.Count == 0)
        {
            throw new InvalidRuleException($"targets[{index}]（{field}）至少需要一个抽取器。");
        }

        return new ExtractionTarget(field, ReadString(element, "label"), isTable, extractors);
    }

    private static FieldExtractor ReadExtractor(JsonElement element, string path, bool isTable)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidRuleException($"{path} 必须是对象。");
        }

        var type = ReadString(element, "type").ToLowerInvariant();

        switch (type)
        {
            case "labelvalue":
            {
                var labels = ReadStringArray(element, "labels");

                if (labels.Count == 0)
                {
                    throw new InvalidRuleException($"{path}（labelValue）必须提供 labels。");
                }

                return new LabelValueExtractor(
                    labels,
                    ReadBool(element, "takeNextLine", true),
                    ReadString(element, "stripSuffix"));
            }

            case "regex":
            {
                var patterns = ReadStringArray(element, "patterns");

                if (patterns.Count == 0)
                {
                    throw new InvalidRuleException($"{path}（regex）必须提供 patterns。");
                }

                foreach (var pattern in patterns)
                {
                    Compile(pattern, path);
                }

                return new RegexExtractor(patterns, ReadStringMap(element, "map"), ReadString(element, "group"));
            }

            case "tablerows":
            {
                if (!isTable)
                {
                    throw new InvalidRuleException($"{path}（tableRows）只能用于 kind=table 的目标。");
                }

                var rowPattern = ReadString(element, "rowPattern");

                if (string.IsNullOrWhiteSpace(rowPattern))
                {
                    throw new InvalidRuleException($"{path}（tableRows）必须提供 rowPattern。");
                }

                Compile(rowPattern, path);

                var columns = ReadStringMap(element, "columns");

                if (columns.Count == 0)
                {
                    throw new InvalidRuleException($"{path}（tableRows）必须提供 columns 映射。");
                }

                return new TableRowsExtractor(
                    rowPattern,
                    columns,
                    ReadStringArray(element, "stopPatterns"),
                    ReadInt(element, "minRows"));
            }

            default:
                throw new InvalidRuleException($"{path}.type 不支持：{type}（可选 labelValue / regex / tableRows）");
        }
    }

    private static void Compile(string pattern, string path)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidRuleException($"{path} 的正则非法：{ex.Message}");
        }
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool ReadBool(JsonElement element, string name, bool fallback)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static int ReadInt(JsonElement element, string name, int fallback = 0)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback,
        };
    }

    private static List<string> ReadStringArray(JsonElement element, string name)
    {
        var result = new List<string>();

        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                result.Add(item.GetString()!);
            }
        }

        return result;
    }

    private static Dictionary<string, string> ReadStringMap(JsonElement element, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                result[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return result;
    }
}

/// <summary>整份规则。</summary>
public sealed record ExtractionRuleSet(string TemplateCode, IReadOnlyList<ExtractionTarget> Targets);

/// <summary>一个目标字段：要抽哪个字段、用哪几种方式抽。</summary>
public sealed record ExtractionTarget(
    string Field,
    string Label,
    bool IsTable,
    IReadOnlyList<FieldExtractor> Extractors);

public abstract record FieldExtractor;

/// <summary>
/// 「标签：值」型。最常见的一行一值（批号、样品名称、检验日期……）。
///
/// 值在标签同一行取不到时，<paramref name="TakeNextLine"/> 决定是否取下一行 ——
/// 仪器导出的报告里"批号"与数值分两行非常常见。
/// </summary>
public sealed record LabelValueExtractor(
    IReadOnlyList<string> Labels,
    bool TakeNextLine,
    string StripSuffix) : FieldExtractor;

/// <summary>正则抽取，取名为 <c>v</c> 的捕获组（没有命名组时取第 1 个捕获组）。</summary>
public sealed record RegexExtractor(
    IReadOnlyList<string> Patterns,
    IReadOnlyDictionary<string, string> Map,
    string Group) : FieldExtractor;

/// <summary>
/// 表格行抽取：逐行套 <paramref name="RowPattern"/>，把命名组映射到列 key。
/// </summary>
public sealed record TableRowsExtractor(
    string RowPattern,
    IReadOnlyDictionary<string, string> Columns,
    IReadOnlyList<string> StopPatterns,
    int MinRows) : FieldExtractor;

/// <summary>一个字段的抽取结果。</summary>
public sealed record ExtractedField(
    string FieldKey,
    string FieldLabel,
    JsonElement Value,
    string Snippet,
    double Confidence);

/// <summary>一次规则执行的结果。</summary>
public sealed record ExtractionOutcome(
    IReadOnlyList<ExtractedField> Fields,
    IReadOnlyList<string> Warnings)
{
    public bool HasResult => Fields.Count > 0;
}

/// <summary>
/// 规则执行器。输入是 PDF 抽出来的纯文本，输出是字段候选值。
///
/// 所有抽取都基于**行**：先按行切、归一化全角字符，再逐行匹配。
/// 之所以不做坐标系/版面分析，是因为规则要能被人读懂、能手写维护；
/// 一旦引入坐标，规则就从"配置"变成"程序"，也就没法交给业务侧维护了。
/// </summary>
public static class ExtractionEngine
{
    /// <summary>全文超过这个长度就不再往下跑：真实报告极少超过，超了多半是附件里混了别的东西。</summary>
    private const int MaxTextLength = 400_000;

    public static ExtractionOutcome Run(ExtractionRuleSet rule, string? text)
    {
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return new ExtractionOutcome([], ["PDF 里没有可读文本（可能是扫描件，需要 OCR）。"]);
        }

        if (text.Length > MaxTextLength)
        {
            text = text[..MaxTextLength];
            warnings.Add($"文本超过 {MaxTextLength} 字符，已截断后解析。");
        }

        var lines = TextNormalizer.ToLines(text);
        var fields = new List<ExtractedField>();

        foreach (var target in rule.Targets)
        {
            var extracted = target.IsTable
                ? ExtractTable(target, lines, warnings)
                : ExtractScalar(target, lines, warnings);

            if (extracted is not null)
            {
                fields.Add(extracted);
            }
            else
            {
                warnings.Add($"未匹配到字段「{target.Label}」（{target.Field}）。");
            }
        }

        return new ExtractionOutcome(fields, warnings);
    }

    private static ExtractedField? ExtractScalar(
        ExtractionTarget target,
        IReadOnlyList<string> lines,
        List<string> warnings)
    {
        foreach (var extractor in target.Extractors)
        {
            switch (extractor)
            {
                case LabelValueExtractor labelValue:
                {
                    var hit = FindLabelValue(labelValue, lines);
                    if (hit is { } value)
                    {
                        var text = Clean(value.Value, labelValue.StripSuffix);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return new ExtractedField(
                                target.Field,
                                string.IsNullOrWhiteSpace(target.Label) ? target.Field : target.Label,
                                JsonSerializer.SerializeToElement(text),
                                value.Snippet,
                                value.Confidence);
                        }
                    }

                    break;
                }

                case RegexExtractor regex:
                {
                    foreach (var pattern in regex.Patterns)
                    {
                        foreach (var line in lines)
                        {
                            var match = Regex.Match(line, pattern, RegexOptions.CultureInvariant);
                            if (!match.Success)
                            {
                                continue;
                            }

                            var groupName = string.IsNullOrWhiteSpace(regex.Group) ? "v" : regex.Group;
                            var group = match.Groups[groupName];

                            if (!group.Success && match.Groups.Count > 1)
                            {
                                group = match.Groups[1];
                            }

                            if (!group.Success)
                            {
                                continue;
                            }

                            var value = group.Value.Trim();
                            if (value.Length == 0)
                            {
                                continue;
                            }

                            if (regex.Map.TryGetValue(value, out var mapped))
                            {
                                value = mapped;
                            }

                            return new ExtractedField(
                                target.Field,
                                string.IsNullOrWhiteSpace(target.Label) ? target.Field : target.Label,
                                JsonSerializer.SerializeToElement(value),
                                line.Trim(),
                                0.9);
                        }
                    }

                    break;
                }
            }
        }

        return null;
    }

    private static ExtractedField? ExtractTable(
        ExtractionTarget target,
        IReadOnlyList<string> lines,
        List<string> warnings)
    {
        foreach (var extractor in target.Extractors.OfType<TableRowsExtractor>())
        {
            var rows = new List<Dictionary<string, string>>();
            var snippets = new List<string>();
            var stopped = false;
            var regex = new Regex(extractor.RowPattern, RegexOptions.CultureInvariant);

            foreach (var line in lines)
            {
                if (extractor.StopPatterns.Count > 0 &&
                    extractor.StopPatterns.Any(p => Regex.IsMatch(line, p, RegexOptions.CultureInvariant)))
                {
                    stopped = true;
                    break;
                }

                var match = regex.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var filled = false;

                foreach (var (columnKey, groupName) in extractor.Columns)
                {
                    var group = match.Groups[groupName];
                    var value = group.Success ? group.Value.Trim() : string.Empty;
                    row[columnKey] = value;

                    if (value.Length > 0)
                    {
                        filled = true;
                    }
                }

                if (filled)
                {
                    rows.Add(row);
                    snippets.Add(line.Trim());
                }
            }

            if (rows.Count == 0)
            {
                continue;
            }

            if (extractor.MinRows > 0 && rows.Count < extractor.MinRows)
            {
                warnings.Add(
                    $"字段「{target.Label}」只匹配到 {rows.Count} 行，少于规则要求的最小行数 {extractor.MinRows}，请核对原文。");
            }

            _ = stopped;

            return new ExtractedField(
                target.Field,
                string.IsNullOrWhiteSpace(target.Label) ? target.Field : target.Label,
                JsonSerializer.SerializeToElement(rows),
                string.Join("\n", snippets.Take(10)),
                rows.Count >= Math.Max(1, extractor.MinRows) ? 0.9 : 0.6);
        }

        return null;
    }

    /// <summary>「标签：值」查找。返回值和命中的那一行（用于给用户看依据）。</summary>
    private static (string Value, string Snippet, double Confidence)? FindLabelValue(
        LabelValueExtractor extractor,
        IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            foreach (var label in extractor.Labels)
            {
                if (string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                var position = line.IndexOf(label, StringComparison.OrdinalIgnoreCase);
                if (position < 0)
                {
                    continue;
                }

                var rest = line[(position + label.Length)..].TrimStart(' ', '\t', ':', '：', '=', '＝', '-', '—');

                if (!string.IsNullOrWhiteSpace(rest))
                {
                    // 同一行取到值：置信度高。但如果剩下的还是"标签式"内容（含冒号），
                    // 多半是抓到了错误的行，降一档置信度让用户留意。
                    var confidence = rest.Contains(':') || rest.Contains('：') ? 0.6 : 0.9;
                    return (rest, line.Trim(), confidence);
                }

                if (!extractor.TakeNextLine)
                {
                    continue;
                }

                for (var j = i + 1; j < lines.Count; j++)
                {
                    if (!string.IsNullOrWhiteSpace(lines[j]))
                    {
                        return (lines[j].Trim(), line.Trim(), 0.7);
                    }
                }
            }
        }

        return null;
    }

    private static string Clean(string value, string stripSuffix)
    {
        var result = value.Trim().TrimEnd('|', '｜', ';', '；');

        if (!string.IsNullOrWhiteSpace(stripSuffix) && result.EndsWith(stripSuffix, StringComparison.Ordinal))
        {
            result = result[..^stripSuffix.Length].Trim();
        }

        return result;
    }
}

/// <summary>
/// 文本归一化。PDF 抽出来的文本里全角数字/标点很常见（尤其国内仪器软件），
/// 不归一化就会让"批号：１２３"这种值带着全角字符进记录。
/// </summary>
public static class TextNormalizer
{
    public static IReadOnlyList<string> ToLines(string text)
    {
        var normalized = Normalize(text);

        return normalized
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .ToList();
    }

    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var ch in text)
        {
            builder.Append(ch switch
            {
                >= '０' and <= '９' => (char)('0' + (ch - '０')),   // 全角数字
                >= 'Ａ' and <= 'Ｚ' => (char)('A' + (ch - 'Ａ')),   // 全角大写
                >= 'ａ' and <= 'ｚ' => (char)('a' + (ch - 'ａ')),   // 全角小写
                '：' => ':',
                '＝' => '=',
                '　' => ' ',                                        // 全角空格
                '（' => '(',
                '）' => ')',
                '％' => '%',
                '．' => '.',
                '，' => ',',
                _ => ch,
            });
        }

        return builder.ToString();
    }

    /// <summary>把提取到的文本转成数字（去掉千分位、单位、空白）。失败返回 null。</summary>
    public static decimal? TryParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = new string(value.Where(c => char.IsDigit(c) || c is '.' or '-' or '+').ToArray());

        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }
}
