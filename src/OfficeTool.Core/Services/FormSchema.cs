using System.Text.Json;
using System.Text.RegularExpressions;

namespace OfficeTool.Core.Services;

/// <summary>
/// 表单定义（模板 schema）的解析与校验。
///
/// 为什么校验放在 Core 而不是只在前端：模板结构决定了记录能不能被正确渲染与校验，
/// 而接口是公开的（脚本、将来的导入工具都会调）。前端校验只是"少一次往返"，
/// 真正的闸门必须在这一层 —— 一份结构非法但存进了库的模板，
/// 会在**所有**引用它的记录上同时爆掉。
/// </summary>
public static partial class FormSchema
{
    public const int MaxSections = 50;

    public const int MaxFieldsPerSection = 200;

    public const int MaxTableColumns = 40;

    /// <summary>字段类型白名单。前端渲染器与这里一一对应。</summary>
    public static readonly string[] FieldTypes =
        ["text", "textarea", "number", "date", "datetime", "select", "checkbox", "table"];

    /// <summary>表格列可用的类型：表格里不再允许嵌套表格。</summary>
    public static readonly string[] ColumnTypes = ["text", "textarea", "number", "date", "select", "checkbox"];

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex KeyPattern();

    /// <summary>解析并校验。返回错误列表（空表示通过）。</summary>
    public static FormSchemaValidation Validate(string? schemaJson)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return new FormSchemaValidation(null, ["表单定义不能为空。"]);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(schemaJson);
        }
        catch (JsonException ex)
        {
            return new FormSchemaValidation(null, [$"表单定义不是合法 JSON：{ex.Message}"]);
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return new FormSchemaValidation(null, ["表单定义必须是 JSON 对象。"]);
            }

            if (!root.TryGetProperty("sections", out var sectionsElement) ||
                sectionsElement.ValueKind != JsonValueKind.Array)
            {
                return new FormSchemaValidation(null, ["表单定义缺少 sections 数组。"]);
            }

            var sections = new List<FormSection>();
            var sectionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fieldKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var sectionIndex = 0;
            foreach (var sectionElement in sectionsElement.EnumerateArray())
            {
                var sectionPath = $"sections[{sectionIndex}]";
                sectionIndex++;

                if (sections.Count >= MaxSections)
                {
                    errors.Add($"分区数量超过上限 {MaxSections}。");
                    break;
                }

                var section = ReadSection(sectionElement, sectionPath, errors);
                if (section is null)
                {
                    continue;
                }

                if (!sectionKeys.Add(section.Key))
                {
                    errors.Add($"{sectionPath}.key 重复：{section.Key}");
                    continue;
                }

                foreach (var field in section.Fields)
                {
                    if (!fieldKeys.Add(field.Key))
                    {
                        errors.Add($"{sectionPath} 中字段 key 重复：{field.Key}（字段 key 必须在整份模板内唯一）");
                    }
                }

                sections.Add(section);
            }

            if (sections.Count == 0)
            {
                errors.Add("表单至少需要一个分区。");
            }

            if (sections.Sum(s => s.Fields.Count) == 0)
            {
                errors.Add("表单至少需要一个字段。");
            }

            var schema = errors.Count == 0 ? new FormDefinition(sections, ReadTitle(root)) : null;

            return new FormSchemaValidation(schema, errors);
        }
    }

    private static string ReadTitle(JsonElement root) =>
        root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
            ? title.GetString() ?? string.Empty
            : string.Empty;

    private static FormSection? ReadSection(JsonElement element, string path, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path} 必须是对象。");
            return null;
        }

        var key = ReadString(element, "key");
        var title = ReadString(element, "title");

        if (string.IsNullOrWhiteSpace(key))
        {
            errors.Add($"{path}.key 不能为空。");
            return null;
        }

        if (!KeyPattern().IsMatch(key))
        {
            errors.Add($"{path}.key 不合法：{key}（只允许字母开头，字母/数字/下划线）");
            return null;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            errors.Add($"{path}.title 不能为空。");
            return null;
        }

        var fields = new List<FormField>();

        if (element.TryGetProperty("fields", out var fieldsElement) && fieldsElement.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var fieldElement in fieldsElement.EnumerateArray())
            {
                var fieldPath = $"{path}.fields[{index}]";
                index++;

                if (fields.Count >= MaxFieldsPerSection)
                {
                    errors.Add($"{path} 字段数超过上限 {MaxFieldsPerSection}。");
                    break;
                }

                var field = ReadField(fieldElement, fieldPath, errors);
                if (field is not null)
                {
                    fields.Add(field);
                }
            }
        }
        else
        {
            errors.Add($"{path}.fields 必须是数组。");
        }

        return new FormSection(key, title, fields);
    }

    private static FormField? ReadField(JsonElement element, string path, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path} 必须是对象。");
            return null;
        }

        var key = ReadString(element, "key");
        var label = ReadString(element, "label");
        var type = ReadString(element, "type").ToLowerInvariant();

        var failed = false;

        if (string.IsNullOrWhiteSpace(key) || !KeyPattern().IsMatch(key))
        {
            errors.Add($"{path}.key 不合法：{key}（只允许字母开头，字母/数字/下划线）");
            failed = true;
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            errors.Add($"{path}.label 不能为空。");
            failed = true;
        }
        else if (label.Length > 100)
        {
            errors.Add($"{path}.label 超过 100 字。");
            failed = true;
        }

        if (!FieldTypes.Contains(type))
        {
            errors.Add($"{path}.type 不支持：{type}（可选 {string.Join(" / ", FieldTypes)}）");
            failed = true;
        }

        if (failed)
        {
            return null;
        }

        var options = ReadStringArray(element, "options");
        var columns = new List<FormColumn>();

        if (type == "select" && options.Count == 0)
        {
            errors.Add($"{path}（{label}）是下拉字段，必须提供 options。");
            failed = true;
        }

        if (type == "table")
        {
            if (!element.TryGetProperty("columns", out var columnsElement) ||
                columnsElement.ValueKind != JsonValueKind.Array)
            {
                errors.Add($"{path}（{label}）是表格字段，必须提供 columns 数组。");
                failed = true;
            }
            else
            {
                var columnKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var columnIndex = 0;

                foreach (var columnElement in columnsElement.EnumerateArray())
                {
                    var columnPath = $"{path}.columns[{columnIndex}]";
                    columnIndex++;

                    if (columns.Count >= MaxTableColumns)
                    {
                        errors.Add($"{path} 列数超过上限 {MaxTableColumns}。");
                        break;
                    }

                    var column = ReadColumn(columnElement, columnPath, errors);
                    if (column is null)
                    {
                        failed = true;
                        continue;
                    }

                    if (!columnKeys.Add(column.Key))
                    {
                        errors.Add($"{columnPath}.key 重复：{column.Key}");
                        failed = true;
                        continue;
                    }

                    columns.Add(column);
                }

                if (columns.Count == 0)
                {
                    errors.Add($"{path}（{label}）至少需要一列。");
                    failed = true;
                }
            }
        }

        if (failed)
        {
            return null;
        }

        return new FormField(
            key,
            label,
            type,
            ReadBool(element, "required"),
            ReadString(element, "placeholder"),
            ReadString(element, "help"),
            options,
            columns,
            ReadInt(element, "minRows"),
            ReadInt(element, "maxLength"),
            type == "select" && ReadBool(element, "multiple"));
    }

    private static FormColumn? ReadColumn(JsonElement element, string path, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path} 必须是对象。");
            return null;
        }

        var key = ReadString(element, "key");
        var label = ReadString(element, "label");
        var type = ReadString(element, "type").ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(key) || !KeyPattern().IsMatch(key))
        {
            errors.Add($"{path}.key 不合法：{key}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            errors.Add($"{path}.label 不能为空。");
            return null;
        }

        if (!ColumnTypes.Contains(type))
        {
            errors.Add($"{path}.type 不支持：{type}（可选 {string.Join(" / ", ColumnTypes)}）");
            return null;
        }

        var options = ReadStringArray(element, "options");

        if (type == "select" && options.Count == 0)
        {
            errors.Add($"{path}（{label}）是下拉列，必须提供 options。");
            return null;
        }

        return new FormColumn(key, label, type, options, ReadString(element, "unit"), ReadInt(element, "precision"));
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

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
                result.Add(item.GetString()!.Trim());
            }
        }

        return result;
    }
}

/// <summary>校验结果。<see cref="Schema"/> 仅在无错误时非空。</summary>
public sealed record FormSchemaValidation(FormDefinition? Schema, IReadOnlyList<string> Errors)
{
    public bool IsValid => Schema is not null && Errors.Count == 0;
}

/// <summary>
/// 解析后的表单结构。命名上与静态校验器 <see cref="FormSchema"/> 区分开：
/// 一个是"规则"，一个是"数据"，混用同一个名字会让调用处读不出意图。
/// </summary>
public sealed record FormDefinition(IReadOnlyList<FormSection> Sections, string Title)
{
    /// <summary>字段 key → 字段定义。渲染与提取映射都靠它做交叉校验。</summary>
    public Dictionary<string, FormField> FieldIndex() =>
        Sections.SelectMany(s => s.Fields).ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
}

public sealed record FormSection(string Key, string Title, IReadOnlyList<FormField> Fields);

public sealed record FormField(
    string Key,
    string Label,
    string Type,
    bool Required,
    string Placeholder,
    string Help,
    IReadOnlyList<string> Options,
    IReadOnlyList<FormColumn> Columns,
    int MinRows,
    int MaxLength,
    bool Multiple = false)
{
    public bool IsTable => Type == "table";
}

public sealed record FormColumn(
    string Key,
    string Label,
    string Type,
    IReadOnlyList<string> Options,
    string Unit,
    int Precision);
