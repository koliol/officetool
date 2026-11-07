<script setup>
import { computed } from 'vue'

/**
 * 表单渲染器：按模板 schema 渲染可编辑表单。
 *
 * 单向数据流：任何修改都通过 `update:modelValue` 抛出**整份新对象**，
 * 不在组件内改 props。表单数据天然是"整体保存"的语义，这样父组件
 * （记录页）只需要持有一份 data，保存/解析填入都简单直接。
 *
 * 表格字段：行内直接编辑（el-table + 单元格内嵌控件），而不是弹窗编辑 ——
 * 检验数据是"一行一个样品"，逐行敲比逐个弹窗快得多。
 */
const props = defineProps({
  schema: { type: Object, required: true },
  modelValue: { type: Object, default: () => ({}) },
  disabled: { type: Boolean, default: false },
})

const emit = defineEmits(['update:modelValue'])

const sections = computed(() => props.schema?.sections ?? [])

function valueOf(key, fallback = '') {
  const value = props.modelValue?.[key]
  return value === undefined || value === null ? fallback : value
}

function setField(key, value) {
  emit('update:modelValue', { ...props.modelValue, [key]: value })
}

function rowsOf(fieldKey) {
  const rows = props.modelValue?.[fieldKey]
  return Array.isArray(rows) ? rows : []
}

function setRows(fieldKey, rows) {
  emit('update:modelValue', { ...props.modelValue, [fieldKey]: rows })
}

function addRow(field) {
  const row = {}
  for (const column of field.columns) {
    row[column.key] = column.type === 'checkbox' ? false : ''
  }
  setRows(field.key, [...rowsOf(field.key), row])
}

function removeRow(fieldKey, index) {
  const rows = [...rowsOf(fieldKey)]
  rows.splice(index, 1)
  setRows(fieldKey, rows)
}

function setCell(fieldKey, index, columnKey, value) {
  const rows = [...rowsOf(fieldKey)]
  rows[index] = { ...rows[index], [columnKey]: value }
  setRows(fieldKey, rows)
}

function dateValue(key, withTime) {
  const raw = valueOf(key)
  return raw ? String(raw) : ''
}

/** 多选下拉的取值统一成数组（历史数据可能是单个字符串，包一层） */
function toList(value) {
  if (Array.isArray(value)) {
    return value
  }

  return value === '' || value === undefined || value === null ? [] : [value]
}

function onDateChange(key, withTime) {
  return (value) => {
    if (!value) {
      setField(key, '')
      return
    }

    setField(key, withTime ? value : String(value).slice(0, 10))
  }
}

const DATE_FORMAT = 'YYYY-MM-DD'
const DATETIME_FORMAT = 'YYYY-MM-DDTHH:mm:ss'
</script>

<template>
  <div class="form-renderer">
    <section v-for="section in sections" :key="section.key" class="form-section">
      <h3 class="section-title">{{ section.title }}</h3>

      <div class="section-fields">
        <div
          v-for="field in section.fields"
          :key="field.key"
          class="form-field"
          :class="{ 'is-full': field.type === 'table' || field.type === 'textarea' }"
        >
          <label class="field-label">
            {{ field.label }}
            <span v-if="field.required" class="required">*</span>
          </label>

          <div class="field-control">
            <el-input
              v-if="field.type === 'text'"
              :model-value="String(valueOf(field.key))"
              :placeholder="field.placeholder || ''"
              :disabled="disabled"
              :maxlength="field.maxLength || 500"
              @update:model-value="(v) => setField(field.key, v)"
            />

            <el-input
              v-else-if="field.type === 'textarea'"
              :model-value="String(valueOf(field.key))"
              type="textarea"
              :rows="3"
              :placeholder="field.placeholder || ''"
              :disabled="disabled"
              :maxlength="field.maxLength || 2000"
              show-word-limit
              @update:model-value="(v) => setField(field.key, v)"
            />

            <el-input-number
              v-else-if="field.type === 'number'"
              :model-value="valueOf(field.key) === '' ? undefined : Number(valueOf(field.key))"
              :disabled="disabled"
              :precision="2"
              :controls="false"
              class="w-number"
              @update:model-value="(v) => setField(field.key, v === undefined ? '' : v)"
            />

            <el-date-picker
              v-else-if="field.type === 'date'"
              :model-value="dateValue(field.key, false)"
              type="date"
              :format="DATE_FORMAT"
              :value-format="DATE_FORMAT"
              :disabled="disabled"
              class="w-date"
              @update:model-value="onDateChange(field.key, false)"
            />

            <el-date-picker
              v-else-if="field.type === 'datetime'"
              :model-value="dateValue(field.key, true)"
              type="datetime"
              :format="DATETIME_FORMAT"
              :value-format="DATETIME_FORMAT"
              :disabled="disabled"
              class="w-date"
              @update:model-value="onDateChange(field.key, true)"
            />

            <el-select
              v-else-if="field.type === 'select'"
              :model-value="field.multiple ? toList(valueOf(field.key, [])) : String(valueOf(field.key))"
              :placeholder="'请选择'"
              :multiple="Boolean(field.multiple)"
              :collapse-tags="Boolean(field.multiple)"
              :collapse-tags-tooltip="Boolean(field.multiple)"
              :disabled="disabled"
              clearable
              class="w-select"
              @update:model-value="(v) => setField(field.key, field.multiple ? (v ?? []) : (v ?? ''))"
            >
              <el-option v-for="option in field.options" :key="option" :label="option" :value="option" />
            </el-select>

            <el-checkbox
              v-else-if="field.type === 'checkbox'"
              :model-value="Boolean(valueOf(field.key, false))"
              :disabled="disabled"
              @update:model-value="(v) => setField(field.key, v)"
            >
              是
            </el-checkbox>

            <div v-else-if="field.type === 'table'" class="table-field">
              <el-table :data="rowsOf(field.key)" border size="small" empty-text="暂无数据行">
                <el-table-column type="index" label="#" width="46" />

                <el-table-column
                  v-for="column in field.columns"
                  :key="column.key"
                  :min-width="column.type === 'text' ? 140 : 120"
                >
                  <template #header>
                    {{ column.label }}
                    <span v-if="column.unit" class="cell-unit">({{ column.unit }})</span>
                  </template>

                  <template #default="{ $index }">
                    <el-input-number
                      v-if="column.type === 'number'"
                      :model-value="
                        rowsOf(field.key)[$index][column.key] === ''
                          ? undefined
                          : Number(rowsOf(field.key)[$index][column.key])
                      "
                      :disabled="disabled"
                      :precision="column.precision || undefined"
                      :controls="false"
                      class="w-number"
                      @update:model-value="(v) => setCell(field.key, $index, column.key, v === undefined ? '' : v)"
                    />

                    <el-select
                      v-else-if="column.type === 'select'"
                      :model-value="String(rowsOf(field.key)[$index][column.key] ?? '')"
                      :disabled="disabled"
                      clearable
                      :placeholder="'—'"
                      @update:model-value="(v) => setCell(field.key, $index, column.key, v ?? '')"
                    >
                      <el-option v-for="option in column.options" :key="option" :label="option" :value="option" />
                    </el-select>

                    <el-input
                      v-else-if="column.type === 'textarea'"
                      :model-value="String(rowsOf(field.key)[$index][column.key] ?? '')"
                      type="textarea"
                      :rows="1"
                      :disabled="disabled"
                      @update:model-value="(v) => setCell(field.key, $index, column.key, v)"
                    />

                    <el-checkbox
                      v-else-if="column.type === 'checkbox'"
                      :model-value="Boolean(rowsOf(field.key)[$index][column.key])"
                      :disabled="disabled"
                      @update:model-value="(v) => setCell(field.key, $index, column.key, v)"
                    />

                    <el-input
                      v-else
                      :model-value="String(rowsOf(field.key)[$index][column.key] ?? '')"
                      :disabled="disabled"
                      @update:model-value="(v) => setCell(field.key, $index, column.key, v)"
                    />
                  </template>
                </el-table-column>

                <el-table-column v-if="!disabled" label="操作" width="70" align="center">
                  <template #default="{ $index }">
                    <el-button link type="danger" size="small" @click="removeRow(field.key, $index)">
                      删除
                    </el-button>
                  </template>
                </el-table-column>
              </el-table>

              <div class="table-help">
                <span v-if="field.help">{{ field.help }}</span>
                <el-button
                  v-if="!disabled"
                  size="small"
                  type="primary"
                  plain
                  @click="addRow(field)"
                >
                  <el-icon><Plus /></el-icon>&nbsp;加一行
                </el-button>
              </div>
            </div>
          </div>

          <p v-if="field.help && field.type !== 'table'" class="field-help">{{ field.help }}</p>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.form-section {
  margin-bottom: 18px;
}

.section-title {
  margin: 0 0 10px;
  padding-left: 8px;
  border-left: 4px solid var(--el-color-primary);
  font-size: 14px;
  font-weight: 700;
  color: var(--ot-text);
}

.section-fields {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px 24px;
}

.form-field.is-full {
  grid-column: 1 / -1;
}

.field-label {
  display: block;
  margin-bottom: 4px;
  font-size: 12.5px;
  font-weight: 600;
  color: var(--ot-text-muted);
}

.required {
  color: var(--el-color-danger);
}

.field-help {
  margin: 4px 0 0;
  font-size: 11.5px;
  color: var(--ot-text-faint);
}

.w-number,
.w-select {
  width: 100%;
}

.w-date {
  width: 100%;
}

.table-help {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-top: 6px;
  font-size: 11.5px;
  color: var(--ot-text-faint);
}

@media (max-width: 900px) {
  .section-fields {
    grid-template-columns: 1fr;
  }
}
</style>
