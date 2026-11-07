using System.Globalization;
using System.Text.Json;

namespace OfficeTool.Core.Services;

/// <summary>
/// 记录数据的服务端校验。
///
/// 前端也会校验一遍（即时反馈），但这一层才是准入门槛：接口是公开的，
/// 而一条"必填项为空却被提交"的记录一旦进库，后面的统计与追溯都建立在错数据上。
///
/// 草稿与提交的严格程度不同：草稿只校验"填了的部分必须合法"（能存半成品），
/// 提交则要求必填项齐全 —— 这也是 ELN 与在线表单的区别：中途必须能保存。
/// </summary>
public static class RecordDataValidator
{
    /// <summary>
    /// @param requireComplete true = 提交（必填齐全）；false = 草稿（只校验已填内容）
    /// </summary>
    public static IReadOnlyList<string> Validate(FormDefinition? schema, JsonElement data, bool requireComplete)
    {
        var errors = new List<string>();

        if (schema is null)
        {
            return ["记录缺少表单结构快照，无法校验。"];
        }

        if (data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return requireComplete ? ["记录内容为空。"] : [];
        }

        if (data.ValueKind != JsonValueKind.Object)
        {
            return ["记录内容必须是 JSON 对象。"];
        }

        foreach (var field in schema.Sections.SelectMany(s => s.Fields))
        {
            var present = data.TryGetProperty(field.Key, out var value) && IsMeaningful(value);

            if (!present)
            {
                if (requireComplete && field.Required)
                {
                    errors.Add($"「{field.Label}」为必填项。");
                }

                continue;
            }

            if (field.IsTable)
            {
                ValidateTable(field, value, requireComplete, errors);
                continue;
            }

            if (field.Type == "select" && field.Multiple)
            {
                ValidateMultiSelect(field, value, errors);
                continue;
            }

            ValidateScalar(field.Label, field.Type, field.Options, value, errors);
        }

        return errors;
    }

    private static void ValidateTable(
        FormField field,
        JsonElement value,
        bool requireComplete,
        List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"「{field.Label}」必须是表格（行数组）。");
            return;
        }

        var rows = value.GetArrayLength();

        if (requireComplete && field.MinRows > 0 && rows < field.MinRows)
        {
            errors.Add($"「{field.Label}」至少需要 {field.MinRows} 行，当前 {rows} 行。");
        }

        var rowIndex = 0;
        foreach (var row in value.EnumerateArray())
        {
            rowIndex++;

            if (row.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"「{field.Label}」第 {rowIndex} 行格式不正确。");
                continue;
            }

            foreach (var column in field.Columns)
            {
                if (!row.TryGetProperty(column.Key, out var cell) || !IsMeaningful(cell))
                {
                    continue;
                }

                ValidateScalar($"{field.Label} 第 {rowIndex} 行的「{column.Label}」", column.Type, column.Options, cell, errors);
            }
        }
    }

    private static void ValidateScalar(
        string label,
        string type,
        IReadOnlyList<string> options,
        JsonElement value,
        List<string> errors)
    {
        switch (type)
        {
            case "number":
                if (!TryGetNumber(value, out _))
                {
                    errors.Add($"「{label}」必须是数字。");
                }

                break;

            case "checkbox":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    errors.Add($"「{label}」必须是布尔值。");
                }

                break;

            case "select":
                if (options.Count > 0)
                {
                    var text = AsText(value);
                    if (!options.Contains(text, StringComparer.Ordinal))
                    {
                        errors.Add($"「{label}」的取值「{text}」不在可选项内。");
                    }
                }

                break;

            case "date":
                if (!DateTime.TryParse(AsText(value), CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    errors.Add($"「{label}」不是合法日期。");
                }

                break;

            case "datetime":
                if (!DateTime.TryParse(AsText(value), CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    errors.Add($"「{label}」不是合法时间。");
                }

                break;
        }
    }

    /// <summary>多选下拉：取值是数组，每一项都必须在可选项内。</summary>
    private static void ValidateMultiSelect(FormField field, JsonElement value, List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"「{field.Label}」是多选字段，取值必须是选项数组。");
            return;
        }

        if (field.Options.Count == 0)
        {
            return;
        }

        foreach (var item in value.EnumerateArray())
        {
            var text = AsText(item);
            if (!field.Options.Contains(text, StringComparer.Ordinal))
            {
                errors.Add($"「{field.Label}」的取值「{text}」不在可选项内。");
            }
        }
    }

    private static bool TryGetNumber(JsonElement value, out decimal number)
    {
        number = 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDecimal(out number),
            JsonValueKind.String => decimal.TryParse(
                value.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out number),
            _ => false,
        };
    }

    /// <summary>视为"没填"的情况：null、空串、空数组、只有空白。</summary>
    private static bool IsMeaningful(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Array => value.GetArrayLength() > 0,
        _ => true,
    };

    private static string AsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => string.Empty,
    };
}
