<script setup>
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { current, navigate } from '../router'
import { elnApi } from '../api'
import { useAuthStore } from '../auth'
import { formatTime } from '../ui/format'
import FormRenderer from '../components/eln/FormRenderer.vue'

/**
 * 记录填写页（#/record?id=1）。
 *
 * 三块内容：工具条（状态/操作）、表单（schema 快照渲染）、原始文件与解析面板。
 *
 * 提交后整页只读 —— 这是 ELN 的硬约束（提交即冻结），前端只做即时反馈，
 * 真正的拦截在服务端（Submit 后 PATCH 会 409）。
 */
const auth = useAuthStore()

const record = ref(null)
const data = ref({})
const loading = ref(false)
const saving = ref(false)
const submitting = ref(false)
const error = ref('')

const files = computed(() => record.value?.files ?? [])
const isDraft = computed(() => record.value?.status === 'Draft')
const isVoided = computed(() => record.value?.status === 'Voided')
const readOnly = computed(() => !record.value || !isDraft.value)
const schema = computed(() => record.value?.schema ?? { sections: [] })

const fileUploading = ref(false)
const extractPanel = reactive({ visible: false, loading: false, ruleId: null, result: null, checked: {} })
const rules = ref([])
const runs = ref([])
const runsVisible = ref(false)

const statusTag = computed(() => {
  switch (record.value?.status) {
    case 'Draft':
      return { text: '草稿', type: 'info' }
    case 'Submitted':
      return { text: '已提交', type: 'success' }
    case 'Voided':
      return { text: '已作废', type: 'danger' }
    default:
      return { text: record.value?.status ?? '-', type: 'info' }
  }
})

async function load() {
  const id = Number(current.query.id)
  if (!id) {
    navigate('/', { replace: true })
    return
  }

  loading.value = true
  error.value = ''

  try {
    record.value = await elnApi.records.get(id)
    data.value = record.value.data ?? {}
    await loadRuns()
  } catch (e) {
    error.value = e.friendlyMessage || '记录加载失败'
  } finally {
    loading.value = false
  }
}

async function loadRuns() {
  try {
    runs.value = await elnApi.records.runs(record.value.id)
  } catch {
    runs.value = []
  }
}

function requireDraft() {
  if (!isDraft.value) {
    ElMessage.warning('只有草稿可以修改。已提交的记录请走作废重开流程。')
    return false
  }
  return true
}

async function saveDraft({ silent = false } = {}) {
  if (!requireDraft()) {
    return
  }

  saving.value = true
  try {
    record.value = await elnApi.records.saveDraft(record.value.id, data.value)
    if (!silent) {
      ElMessage.success('草稿已保存')
    }
  } catch (e) {
    ElMessage.error(e.friendlyMessage || '保存失败')
  } finally {
    saving.value = false
  }
}

async function submit() {
  if (!requireDraft()) {
    return
  }

  try {
    await ElMessageBox.confirm(
      '提交后数据将冻结，不能修改（如需更正只能作废重开）。确认提交？',
      '提交记录',
      { type: 'warning', confirmButtonText: '提交', cancelButtonText: '再改改' },
    )
  } catch {
    return
  }

  submitting.value = true
  try {
    // 先把当前编辑内容存上去，再提交：用户点提交时期望的是"提交我现在看到的"
    await elnApi.records.saveDraft(record.value.id, data.value)
    record.value = await elnApi.records.submit(record.value.id)
    ElMessage.success('已提交')
  } catch (e) {
    ElMessage.error(e.friendlyMessage || '提交失败')
  } finally {
    submitting.value = false
  }
}

async function voidRecord() {
  let reason
  try {
    const result = await ElMessageBox.prompt('作废后记录保留留痕、不可恢复为草稿。请填写作废原因：', '作废记录', {
      type: 'warning',
      confirmButtonText: '作废',
      cancelButtonText: '取消',
      inputValidator: (v) => (v && v.trim() ? true : '必须填写原因'),
    })
    reason = result.value.trim()
  } catch {
    return
  }

  try {
    record.value = await elnApi.records.void(record.value.id, reason)
    ElMessage.success('已作废')
  } catch (e) {
    ElMessage.error(e.friendlyMessage || '作废失败')
  }
}

// ── 原始文件 ────────────────────────────────────────────────────────

async function onFilePicked(upload) {
  fileUploading.value = true
  try {
    await elnApi.records.uploadFile(record.value.id, upload.file)
    ElMessage.success('已上传')
    await load()
  } catch (e) {
    ElMessage.error(e.friendlyMessage || '上传失败')
  } finally {
    fileUploading.value = false
  }
}

async function removeFile(file) {
  try {
    await ElMessageBox.confirm(`删除原始文件「${file.fileName}」？`, '删除', { type: 'warning' })
  } catch {
    return
  }

  try {
    await elnApi.records.removeFile(record.value.id, file.id)
    await load()
  } catch (e) {
    ElMessage.error(e.friendlyMessage || '删除失败')
  }
}

// ── 解析 ────────────────────────────────────────────────────────────

async function openExtractPanel() {
  extractPanel.visible = true
  extractPanel.result = null
  extractPanel.checked = {}

  try {
    rules.value = await elnApi.records.rules(record.value.id)

    if (!rules.value.length) {
      ElMessage.warning('当前项目/检项还没有可用的提取规则，请联系管理员在「提取规则」页设置生效范围。')
    } else if (rules.value.length === 1) {
      extractPanel.ruleId = rules.value[0].id
    }
  } catch (e) {
    ElMessage.error(e.friendlyMessage || '规则加载失败')
  }
}

async function runExtract() {
  if (!extractPanel.ruleId) {
    ElMessage.warning('请先选择提取规则')
    return
  }

  if (!files.value.length) {
    ElMessage.warning('请先上传原始 PDF 报告')
    return
  }

  extractPanel.loading = true
  try {
    extractPanel.result = await elnApi.records.extract(record.value.id, { ruleId: extractPanel.ruleId })
    extractPanel.checked = {}
    await loadRuns()
  } catch (e) {
    ElMessage.error(e.friendlyMessage || '解析失败')
  } finally {
    extractPanel.loading = false
  }
}

function isCellArray(field) {
  return Array.isArray(field.value)
}

function applyChecked() {
  const picked = Object.entries(extractPanel.checked).filter(([, on]) => on)
  if (!picked.length) {
    ElMessage.warning('请先勾选要填入的字段')
    return
  }

  const next = { ...data.value }

  for (const [fieldKey] of picked) {
    const field = extractPanel.result.fields.find((f) => f.field === fieldKey)
    if (!field) {
      continue
    }

    if (Array.isArray(field.value)) {
      // 表格：解析值与已有行合并（按 sampleNo 去重，抽到的值覆盖旧值）
      const existing = Array.isArray(next[fieldKey]) ? [...next[fieldKey]] : []
      for (const row of field.value) {
        const key = row.sampleNo
        const hit = key ? existing.findIndex((r) => String(r.sampleNo ?? '') === String(key)) : -1

        if (hit >= 0) {
          existing[hit] = { ...existing[hit], ...row }
        } else {
          existing.push({ ...row })
        }
      }
      next[fieldKey] = existing
    } else {
      next[fieldKey] = field.value
    }
  }

  data.value = next
  extractPanel.visible = false
  ElMessage.success(`已填入 ${picked.length} 个字段，请核对后保存`)
}

onMounted(load)

// 同一页面内换 id（列表点另一条记录）也要重新加载
watch(
  () => [current.query.id, current.version],
  () => load(),
)
</script>

<template>
  <div class="record-page">
    <div v-loading="loading" class="record-loading">
      <el-result v-if="error" icon="error" title="记录加载失败" :sub-title="error">
        <template #extra>
          <el-button type="primary" @click="navigate('/')">回到记录中心</el-button>
        </template>
      </el-result>

      <template v-else-if="record">
        <div class="page-head">
          <div>
            <h1 class="page-head__title record-title">
              {{ record.title }}
              <el-tag :type="statusTag.type" size="small" effect="dark">{{ statusTag.text }}</el-tag>
            </h1>
            <p class="page-head__sub record-meta">
              <span class="ot-mono">{{ record.recordNo }}</span>
              <span class="sep">·</span>{{ record.project }} / {{ record.check }}
              <span class="sep">·</span>{{ record.templateName }} v{{ record.templateVersion }}
              <span class="sep">·</span>创建于 {{ formatTime(record.createdAt) }}（{{ record.createdBy || '匿名' }}）
              <template v-if="record.submittedAt">
                <span class="sep">·</span>提交于 {{ formatTime(record.submittedAt) }}（{{ record.submittedBy }}）
              </template>
            </p>
            <p v-if="isVoided" class="void-reason">作废原因：{{ record.voidReason }}</p>
          </div>

          <div class="page-head__actions">
            <el-button @click="navigate('/')">返回列表</el-button>

            <template v-if="isDraft">
              <el-button :loading="saving" @click="saveDraft()">保存草稿</el-button>
              <el-button type="warning" plain @click="openExtractPanel">
                <el-icon><MagicStick /></el-icon>&nbsp;从 PDF 提取
              </el-button>
              <el-button type="primary" :loading="submitting" @click="submit">提交</el-button>
            </template>

            <el-button
              v-else-if="!isVoided"
              type="danger"
              plain
              :disabled="!auth.canAnyManage"
              @click="voidRecord"
            >
              作废
            </el-button>
          </div>
        </div>

        <div class="record-body">
          <div class="record-form">
            <div class="ot-panel">
              <div class="ot-panel__body form-body">
                <FormRenderer v-model="data" :schema="schema" :disabled="readOnly" />
              </div>
            </div>
          </div>

          <aside class="record-aside">
            <div class="ot-panel aside-block">
              <div class="ot-panel__head">
                <h3 class="ot-panel__title">原始文件</h3>
                <el-upload
                  v-if="isDraft"
                  :show-file-list="false"
                  :http-request="onFilePicked"
                  accept=".pdf,.jpg,.jpeg,.png,.tif,.tiff"
                  :disabled="fileUploading"
                >
                  <el-button size="small" type="primary" plain :loading="fileUploading">上传</el-button>
                </el-upload>
              </div>

              <div class="ot-panel__body aside-body">
                <p v-if="!files.length" class="empty-tip">还没有上传原始报告（PDF）</p>

                <div v-for="file in files" :key="file.id" class="file-row">
                  <el-icon class="file-icon"><Document /></el-icon>
                  <div class="file-info">
                    <p class="file-name" :title="file.fileName">{{ file.fileName }}</p>
                    <p class="file-meta">
                      {{ formatTime(file.uploadedAt) }}（{{ file.uploadedBy || '匿名' }}）
                    </p>
                  </div>
                  <el-button
                    v-if="isDraft"
                    link
                    type="danger"
                    size="small"
                    @click="removeFile(file)"
                  >
                    删除
                  </el-button>
                </div>
              </div>
            </div>

            <div class="ot-panel aside-block">
              <div class="ot-panel__head">
                <h3 class="ot-panel__title">解析历史</h3>
                <el-button link size="small" @click="runsVisible = !runsVisible">
                  {{ runsVisible ? '收起' : '展开' }}
                </el-button>
              </div>

              <div v-show="runsVisible" class="ot-panel__body aside-body">
                <p v-if="!runs.length" class="empty-tip">还没有解析过</p>
                <div v-for="run in runs" :key="run.id" class="run-row">
                  <span class="run-status" :class="`is-${run.status.toLowerCase()}`">{{ run.status }}</span>
                  <span class="run-meta">
                    {{ run.ruleCode }} v{{ run.ruleVersion }} · 命中 {{ run.matchedFieldCount }} ·
                    {{ formatTime(run.createdAt) }}
                  </span>
                </div>
              </div>
            </div>
          </aside>
        </div>

        <!-- 解析结果确认 -->
        <el-dialog v-model="extractPanel.visible" title="从 PDF 提取数据" width="720px">
          <div class="extract-body">
            <el-alert
              v-if="!rules.length"
              type="warning"
              :closable="false"
              show-icon
              title="当前项目/检项没有可用的提取规则"
              description="请联系管理员在「提取规则」页把规则授权到这个范围。"
            />

            <template v-else>
              <div class="extract-bar">
                <el-select v-model="extractPanel.ruleId" placeholder="选择提取规则" class="w-rule">
                  <el-option
                    v-for="rule in rules"
                    :key="rule.id"
                    :label="`${rule.name} v${rule.version}`"
                    :value="rule.id"
                  />
                </el-select>

                <el-button type="primary" :loading="extractPanel.loading" @click="runExtract">
                  解析
                </el-button>

                <span v-if="files.length" class="ot-faint">将解析最近上传的：{{ files[files.length - 1].fileName }}</span>
              </div>

              <template v-if="extractPanel.result">
                <el-alert
                  v-if="extractPanel.result.error"
                  :type="extractPanel.result.status === 'NoMatch' ? 'warning' : 'error'"
                  :closable="false"
                  show-icon
                  :title="extractPanel.result.error"
                />

                <el-table
                  v-if="extractPanel.result.fields.length"
                  :data="extractPanel.result.fields"
                  size="small"
                  border
                  class="extract-table"
                >
                  <el-table-column width="52" align="center">
                    <template #default="{ row }">
                      <el-checkbox
                        :model-value="Boolean(extractPanel.checked[row.field])"
                        @update:model-value="(v) => (extractPanel.checked[row.field] = v)"
                      />
                    </template>
                  </el-table-column>

                  <el-table-column prop="label" label="字段" width="150" />

                  <el-table-column label="抽出值" min-width="160">
                    <template #default="{ row }">
                      <span v-if="isCellArray(row.value)" class="ot-faint">
                        表格 {{ row.value.length }} 行（合并填入）
                      </span>
                      <span v-else class="ot-mono">{{ row.value }}</span>
                    </template>
                  </el-table-column>

                  <el-table-column prop="snippet" label="依据（原文）" min-width="220" show-overflow-tooltip />
                </el-table>

                <div v-if="extractPanel.result.warnings.length" class="extract-warnings">
                  <p v-for="warning in extractPanel.result.warnings" :key="warning">⚠ {{ warning }}</p>
                </div>

                <details v-if="extractPanel.result.textPreview" class="text-preview">
                  <summary>查看 PDF 提取出的原文</summary>
                  <pre>{{ extractPanel.result.textPreview }}</pre>
                </details>

                <div v-if="extractPanel.result.fields.length" class="extract-actions">
                  <el-button
                    type="primary"
                    @click="
                      () => {
                        extractPanel.checked = Object.fromEntries(
                          extractPanel.result.fields.map((f) => [f.field, true]),
                        )
                        applyChecked()
                      }
                    "
                  >
                    全部填入
                  </el-button>
                  <el-button @click="applyChecked">填入勾选项</el-button>
                </div>
              </template>
            </template>
          </div>
        </el-dialog>
      </template>
    </div>
  </div>
</template>

<style scoped>
.record-page {
  height: 100%;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.record-loading {
  height: 100%;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.record-title {
  display: flex;
  align-items: center;
  gap: 10px;
}

.record-meta .sep {
  margin: 0 6px;
  color: var(--ot-text-faint);
}

.void-reason {
  margin: 6px 0 0;
  font-size: 12px;
  color: var(--el-color-danger);
}

.record-body {
  flex: 1;
  min-height: 0;
  display: flex;
  gap: 14px;
}

.record-form {
  flex: 1;
  min-width: 0;
  overflow: auto;
}

.form-body {
  overflow: visible;
}

.record-aside {
  width: 300px;
  flex: none;
  overflow: auto;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.aside-block {
  flex: none;
}

.aside-body {
  padding: 10px 12px;
}

.file-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 0;
  border-bottom: 1px solid var(--ot-border-soft);
}

.file-row:last-child {
  border-bottom: none;
}

.file-icon {
  color: var(--el-color-danger);
}

.file-info {
  flex: 1;
  min-width: 0;
}

.file-name {
  margin: 0;
  font-size: 12.5px;
  font-weight: 600;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.file-meta {
  margin: 0;
  font-size: 11px;
  color: var(--ot-text-faint);
}

.run-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 0;
  font-size: 12px;
}

.run-status {
  flex: none;
  font-weight: 700;
  font-size: 11px;
  padding: 1px 6px;
  border-radius: 4px;
}

.run-status.is-succeeded {
  background: var(--el-color-success-light-9);
  color: var(--ot-success);
}

.run-status.is-nomatch {
  background: var(--el-color-warning-light-9);
  color: var(--ot-warning);
}

.run-status.is-failed {
  background: var(--el-color-danger-light-9);
  color: var(--ot-danger);
}

.run-meta {
  color: var(--ot-text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.extract-bar {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 12px;
}

.w-rule {
  width: 280px;
}

.extract-table {
  margin-bottom: 10px;
}

.extract-warnings {
  margin-bottom: 10px;
  padding: 8px 12px;
  border-radius: 6px;
  background: var(--ot-surface-2);
  font-size: 12px;
  color: var(--ot-text-muted);
}

.extract-warnings p {
  margin: 2px 0;
}

.text-preview {
  margin-bottom: 10px;
  font-size: 12px;
}

.text-preview pre {
  max-height: 220px;
  overflow: auto;
  margin: 6px 0 0;
  padding: 10px;
  border-radius: 6px;
  background: var(--ot-surface-2);
  white-space: pre-wrap;
  word-break: break-all;
}

.extract-actions {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
}
</style>
