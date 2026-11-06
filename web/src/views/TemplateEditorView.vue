<script setup>
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { current, navigate } from '../router'
import { elnApi } from '../api'
import { useAuthStore } from '../auth'
import FormRenderer from '../components/eln/FormRenderer.vue'

/**
 * 表单设计器（在线创建 / 编辑模板）。
 *
 * 结构 = 分区 → 字段（→ 表格列）。左侧结构树 + 右侧属性面板 + 实时预览。
 *
 * 设计上刻意保守：不做拖拽排序（拖拽库又是一笔依赖），用上移/下移按钮代替 ——
 * 模板结构一年改不了几次，可读性比炫技重要。
 */
const auth = useAuthStore()

const templateId = computed(() => (current.query.id ? Number(current.query.id) : null))
const isEdit = computed(() => templateId.value !== null)

const meta = reactive({ code: '', name: '', category: '', description: '', isEnabled: true })
const sections = ref([])
const version = ref(1)
const builtIn = ref(false)

const selected = reactive({ sectionIndex: -1, fieldIndex: -1 })
const saving = ref(false)
const loading = ref(false)
const showPreview = ref(true)
const previewData = ref({})

const FIELD_TYPES = [
  { value: 'text', label: '单行文本' },
  { value: 'textarea', label: '多行文本' },
  { value: 'number', label: '数字' },
  { value: 'date', label: '日期' },
  { value: 'datetime', label: '日期时间' },
  { value: 'select', label: '下拉选择' },
  { value: 'checkbox', label: '勾选' },
  { value: 'table', label: '表格' },
]

const COLUMN_TYPES = ['text', 'textarea', 'number', 'date', 'select', 'checkbox']

const schema = computed(() => ({ sections: sections.value.map((s) => ({ ...s })) }))

const currentSection = computed(() => sections.value[selected.sectionIndex] ?? null)
const currentField = computed(() =>
  currentSection.value ? (currentSection.value.fields[selected.fieldIndex] ?? null) : null,
)

async function load() {
  if (!isEdit.value) {
    // 新建：给一个最小的起始结构，用户不至于面对一张白纸
    sections.value = [
      { key: 'basic', title: '基本信息', fields: [{ key: 'sampleName', label: '样品名称', type: 'text', required: true }] },
    ]
    selected.sectionIndex = 0
    selected.fieldIndex = -1
    return
  }

  loading.value = true
  try {
    const template = await elnApi.templates.get(templateId.value)
    meta.code = template.code
    meta.name = template.name
    meta.category = template.category
    meta.description = template.description
    meta.isEnabled = template.isEnabled
    version.value = template.version
    builtIn.value = template.isBuiltIn
    sections.value = JSON.parse(JSON.stringify(template.schema?.sections ?? []))
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '模板加载失败')
  } finally {
    loading.value = false
  }
}

function selectSection(index) {
  selected.sectionIndex = index
  selected.fieldIndex = -1
}

function selectField(sectionIndex, fieldIndex) {
  selected.sectionIndex = sectionIndex
  selected.fieldIndex = fieldIndex
}

function slugify(text, fallback) {
  const cleaned = String(text ?? '')
    .replace(/[^A-Za-z0-9_]/g, '')
    .replace(/^_+/, '')
    .slice(0, 40)

  return cleaned || fallback
}

function uniqueKey(base, owner, keyName = 'key') {
  const taken = new Set()
  if (owner === 'sections') {
    sections.value.forEach((s) => taken.add(s.key.toLowerCase()))
  } else {
    sections.value.forEach((s) => s.fields.forEach((f) => taken.add(f.key.toLowerCase())))
  }

  let key = base
  let index = 2
  while (taken.has(key.toLowerCase())) {
    key = `${base}_${index++}`
  }

  return key
}

function addSection() {
  const key = uniqueKey(`section_${sections.value.length + 1}`, 'sections')
  sections.value.push({ key, title: `新分区 ${sections.value.length + 1}`, fields: [] })
  selectSection(sections.value.length - 1)
}

function removeSection(index) {
  sections.value.splice(index, 1)
  selected.sectionIndex = -1
  selected.fieldIndex = -1
}

function moveSection(index, delta) {
  const target = index + delta
  if (target < 0 || target >= sections.value.length) {
    return
  }
  const [item] = sections.value.splice(index, 1)
  sections.value.splice(target, 0, item)
  selectSection(target)
}

function addField(section) {
  const field = {
    key: uniqueKey('field', 'fields'),
    label: '新字段',
    type: 'text',
    required: false,
    placeholder: '',
    help: '',
    options: [],
    columns: [],
    minRows: 0,
    maxLength: 0,
  }

  section.fields.push(field)
  selectField(sections.value.indexOf(section), section.fields.length - 1)
}

function removeField(section, index) {
  section.fields.splice(index, 1)
  selected.fieldIndex = -1
}

function moveField(section, index, delta) {
  const target = index + delta
  if (target < 0 || target >= section.fields.length) {
    return
  }
  const [item] = section.fields.splice(index, 1)
  section.fields.splice(target, 0, item)
  selected.fieldIndex = target
}

function onTypeChange(field, type) {
  field.type = type

  if (type === 'select' && (!Array.isArray(field.options) || field.options.length === 0)) {
    field.options = ['选项一', '选项二']
  }

  if (type === 'table' && (!Array.isArray(field.columns) || field.columns.length === 0)) {
    field.columns = [
      { key: 'col1', label: '列一', type: 'text' },
      { key: 'col2', label: '列二', type: 'number' },
    ]
    field.minRows = 1
  }
}

function addColumn(field) {
  field.columns.push({ key: uniqueKey('col', 'fields'), label: '新列', type: 'text' })
}

function removeColumn(field, index) {
  field.columns.splice(index, 1)
}

function clientValidate() {
  const errors = []
  const keys = new Set()

  if (!meta.name.trim()) {
    errors.push('模板名称不能为空。')
  }

  if (isEdit.value && !meta.code) {
    errors.push('模板编码缺失。')
  }

  if (!sections.value.length) {
    errors.push('至少需要一个分区。')
  }

  for (const [si, section] of sections.value.entries()) {
    if (!section.key.trim() || !section.title.trim()) {
      errors.push(`分区 ${si + 1} 的 key / 标题不能为空。`)
    }

    for (const [fi, field] of section.fields.entries()) {
      if (!field.key.trim() || !field.label.trim()) {
        errors.push(`分区「${section.title}」第 ${fi + 1} 个字段的 key / 名称不能为空。`)
      }

      if (keys.has(field.key.toLowerCase())) {
        errors.push(`字段 key 重复：${field.key}（必须全模板唯一）`)
      }
      keys.add(field.key.toLowerCase())

      if (field.type === 'select' && (!field.options || field.options.length === 0)) {
        errors.push(`字段「${field.label}」是下拉，必须配置选项。`)
      }

      if (field.type === 'table') {
        if (!field.columns || field.columns.length === 0) {
          errors.push(`字段「${field.label}」是表格，必须配置列。`)
        } else {
          const columnKeys = new Set()
          for (const column of field.columns) {
            if (columnKeys.has(column.key.toLowerCase())) {
              errors.push(`字段「${field.label}」存在重复列 key：${column.key}`)
            }
            columnKeys.add(column.key.toLowerCase())

            if (column.type === 'select' && (!column.options || column.options.length === 0)) {
              errors.push(`字段「${field.label}」的列「${column.label}」是下拉，必须配置选项。`)
            }
          }
        }
      }
    }
  }

  return errors
}

async function save() {
  const errors = clientValidate()

  if (errors.length) {
    ElMessage.error(errors[0])
    return
  }

  const payload = {
    code: isEdit.value ? meta.code : meta.code.trim(),
    name: meta.name.trim(),
    category: meta.category.trim(),
    description: meta.description.trim(),
    schema: { sections: sections.value },
    isEnabled: meta.isEnabled,
  }

  saving.value = true
  try {
    const saved = isEdit.value
      ? await elnApi.templates.update(templateId.value, payload)
      : await elnApi.templates.create(payload)

    if (!isEdit.value) {
      navigate('/template-editor', { query: { id: saved.id }, replace: true })
    } else {
      version.value = saved.version
      meta.code = saved.code
    }

    ElMessage.success(
      saved.version > version.value || !isEdit.value
        ? '已保存'
        : `已保存（结构变更，版本 → v${saved.version}）`,
    )
    version.value = saved.version
  } catch (error) {
    // 后端会指出具体哪一条不合法（如重复 key），比前端更权威
    ElMessage.error(error.friendlyMessage || '保存失败')
  } finally {
    saving.value = false
  }
}

onMounted(load)

watch(templateId, load)
</script>

<template>
  <div class="page designer-page">
    <div class="page-head">
      <div>
        <h1 class="page-head__title">
          {{ isEdit ? '编辑模板' : '新建模板' }}
          <el-tag v-if="isEdit" size="small" effect="plain">v{{ version }}</el-tag>
          <el-tag v-if="builtIn" size="small" type="warning" effect="plain">内置</el-tag>
        </h1>
        <p class="page-head__sub">
          结构保存进数据库并版本化；已创建的记录继续使用创建时的快照，不会跟着变。
        </p>
      </div>

      <div class="page-head__actions">
        <el-button @click="navigate('/templates')">返回模板库</el-button>
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
      </div>
    </div>

    <div v-loading="loading" class="designer-body">
      <div class="designer-left">
        <div class="ot-panel meta-panel">
          <div class="ot-panel__head">
            <h3 class="ot-panel__title">基本信息</h3>
          </div>
          <div class="ot-panel__body meta-form">
            <el-form label-width="70px" size="small" @submit.prevent>
              <el-form-item label="编码">
                <el-input
                  v-model="meta.code"
                  :disabled="isEdit"
                  placeholder="大写字母开头，如 INSOLUBLE_PARTICLES"
                />
              </el-form-item>
              <el-form-item label="名称" required>
                <el-input v-model="meta.name" placeholder="如 不溶性微粒检查记录" />
              </el-form-item>
              <el-form-item label="分类">
                <el-input v-model="meta.category" placeholder="如 理化检验" />
              </el-form-item>
              <el-form-item label="说明">
                <el-input v-model="meta.description" type="textarea" :rows="2" />
              </el-form-item>
            </el-form>
          </div>
        </div>

        <div class="ot-panel structure-panel">
          <div class="ot-panel__head">
            <h3 class="ot-panel__title">结构</h3>
            <el-button size="small" type="primary" plain @click="addSection">
              <el-icon><Plus /></el-icon>&nbsp;加分区
            </el-button>
          </div>

          <div class="ot-panel__body structure-body">
            <div v-for="(section, si) in sections" :key="section.key" class="structure-section">
              <div class="section-row" :class="{ 'is-selected': selected.sectionIndex === si && selected.fieldIndex < 0 }">
                <el-icon><Folder /></el-icon>
                <el-input v-model="section.title" size="small" class="section-title-input" />
                <span class="ot-mono ot-faint key-chip">{{ section.key }}</span>
                <span class="row-actions">
                  <el-button link size="small" @click="moveSection(si, -1)">↑</el-button>
                  <el-button link size="small" @click="moveSection(si, 1)">↓</el-button>
                  <el-button link type="danger" size="small" @click="removeSection(si)">删</el-button>
                </span>
              </div>

              <div class="field-list">
                <div
                  v-for="(field, fi) in section.fields"
                  :key="field.key"
                  class="field-row"
                  :class="{ 'is-selected': selected.sectionIndex === si && selected.fieldIndex === fi }"
                  @click="selectField(si, fi)"
                >
                  <span class="field-type-chip">{{ field.type }}</span>
                  <span class="field-label">{{ field.label }}</span>
                  <span v-if="field.required" class="required">*</span>
                  <span class="ot-mono ot-faint key-chip">{{ field.key }}</span>
                  <span class="row-actions" @click.stop>
                    <el-button link size="small" @click="moveField(section, fi, -1)">↑</el-button>
                    <el-button link size="small" @click="moveField(section, fi, 1)">↓</el-button>
                    <el-button link type="danger" size="small" @click="removeField(section, fi)">删</el-button>
                  </span>
                </div>

                <el-button
                  size="small"
                  text
                  type="primary"
                  class="add-field"
                  @click="addField(section)"
                >
                  + 加字段
                </el-button>
              </div>
            </div>
          </div>
        </div>
      </div>

      <div class="designer-right">
        <div class="ot-panel props-panel">
          <div class="ot-panel__head">
            <h3 class="ot-panel__title">字段属性</h3>
          </div>

          <div class="ot-panel__body props-body">
            <p v-if="!currentField" class="empty-tip">在左侧选择一个字段进行编辑</p>

            <el-form v-else label-width="76px" size="small" @submit.prevent>
              <el-form-item label="key">
                <el-input v-model="currentField.key" />
              </el-form-item>
              <el-form-item label="名称" required>
                <el-input v-model="currentField.label" />
              </el-form-item>
              <el-form-item label="类型">
                <el-select :model-value="currentField.type" @update:model-value="(v) => onTypeChange(currentField, v)">
                  <el-option v-for="t in FIELD_TYPES" :key="t.value" :label="t.label" :value="t.value" />
                </el-select>
              </el-form-item>
              <el-form-item label="必填">
                <el-switch v-model="currentField.required" />
              </el-form-item>

              <template v-if="currentField.type === 'text' || currentField.type === 'textarea'">
                <el-form-item label="占位">
                  <el-input v-model="currentField.placeholder" />
                </el-form-item>
                <el-form-item label="长度上限">
                  <el-input-number v-model="currentField.maxLength" :min="0" :max="4000" :controls="false" class="w-full" />
                </el-form-item>
              </template>

              <template v-if="currentField.type === 'select'">
                <el-form-item label="选项">
                  <el-input
                    :model-value="(currentField.options || []).join('\n')"
                    type="textarea"
                    :rows="4"
                    placeholder="每行一个选项"
                    @update:model-value="(v) => (currentField.options = v.split('\n').map((s) => s.trim()).filter(Boolean))"
                  />
                </el-form-item>
              </template>

              <template v-if="currentField.type === 'table'">
                <el-form-item label="最小行数">
                  <el-input-number v-model="currentField.minRows" :min="0" :max="50" :controls="false" class="w-full" />
                </el-form-item>

                <el-form-item label="表格列">
                  <div class="column-editor">
                    <div v-for="(column, ci) in currentField.columns" :key="ci" class="column-row">
                      <el-input v-model="column.key" placeholder="key" size="small" class="col-key" />
                      <el-input v-model="column.label" placeholder="列名" size="small" class="col-label" />
                      <el-select v-model="column.type" size="small" class="col-type">
                        <el-option v-for="t in COLUMN_TYPES" :key="t" :label="t" :value="t" />
                      </el-select>
                      <el-button link type="danger" size="small" @click="removeColumn(currentField, ci)">
                        删
                      </el-button>
                    </div>
                    <el-button size="small" text type="primary" @click="addColumn(currentField)">+ 加列</el-button>
                  </div>
                </el-form-item>
              </template>

              <el-form-item label="帮助文字">
                <el-input v-model="currentField.help" type="textarea" :rows="2" />
              </el-form-item>
            </el-form>
          </div>
        </div>

        <div class="ot-panel preview-panel">
          <div class="ot-panel__head">
            <h3 class="ot-panel__title">实时预览</h3>
            <el-button link size="small" @click="showPreview = !showPreview">
              {{ showPreview ? '收起' : '展开' }}
            </el-button>
          </div>

          <div v-show="showPreview" class="ot-panel__body preview-body">
            <FormRenderer v-model="previewData" :schema="schema" />
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.designer-page {
  height: 100%;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.designer-body {
  flex: 1;
  min-height: 0;
  display: flex;
  gap: 14px;
}

.designer-left {
  width: 380px;
  flex: none;
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-height: 0;
}

.meta-panel {
  flex: none;
}

.structure-panel {
  flex: 1;
  min-height: 0;
}

.structure-body {
  overflow: auto;
}

.designer-right {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-height: 0;
}

.props-panel {
  flex: none;
  max-height: 55%;
  display: flex;
  flex-direction: column;
}

.props-body {
  overflow: auto;
}

.preview-panel {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.preview-body {
  overflow: auto;
}

.structure-section {
  margin-bottom: 12px;
}

.section-row {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 6px;
  border-radius: 6px;
}

.section-row.is-selected {
  background: var(--ot-primary-soft);
}

.section-title-input {
  width: 140px;
}

.key-chip {
  font-size: 10.5px;
}

.row-actions {
  margin-left: auto;
  display: inline-flex;
  gap: 2px;
  flex: none;
}

.field-list {
  margin: 4px 0 0 18px;
  border-left: 1px solid var(--ot-border);
  padding-left: 8px;
}

.field-row {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 3px 6px;
  border-radius: 6px;
  cursor: pointer;
}

.field-row:hover {
  background: var(--ot-surface-2);
}

.field-row.is-selected {
  background: var(--ot-primary-soft);
}

.field-type-chip {
  flex: none;
  font-size: 10px;
  font-weight: 700;
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--ot-surface-3);
  color: var(--ot-text-muted);
}

.field-label {
  font-size: 12.5px;
  color: var(--ot-text);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.required {
  color: var(--el-color-danger);
}

.add-field {
  margin-top: 2px;
}

.column-editor {
  width: 100%;
}

.column-row {
  display: flex;
  gap: 6px;
  margin-bottom: 4px;
}

.col-key {
  width: 110px;
}

.col-label {
  flex: 1;
}

.col-type {
  width: 100px;
}

.w-full {
  width: 100%;
}
</style>
