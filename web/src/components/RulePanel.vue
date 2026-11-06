<script setup>
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { ElMessageBox } from 'element-plus'
import { ElMessage } from '../ui/message'
import { projectApi, ruleApi } from '../api'
import { useAppStore } from '../store'
import { useAuthStore, LEVELS } from '../auth'
import CopyToDialog from './CopyToDialog.vue'

/**
 * 提取规则列表。
 *
 * 规则与模板/文档/附件一样是「项目/检项」两级目录下的**文件**
 * （ExtractionRules\{项目}\{检项}\），建项目/检项时目录已一并创建。
 * 备注是应用侧元数据，存在数据库里，可在本页直接编辑。
 */
const props = defineProps({
  keyword: { type: String, default: '' },
  scopeText: { type: String, default: '' },
})

const store = useAppStore()
const auth = useAuthStore()

const rows = ref([])
const loading = ref(false)
const uploading = ref(false)
const selected = ref([])

const filters = reactive({ project: '', check: '' })
const checkOptions = ref([])

const copyDialog = reactive({ visible: false })

/** 正在内联编辑备注的行（存文件名）；空串表示没有在编辑 */
const editingNote = ref('')
const noteDraft = ref('')

const allowedExtensions = computed(() => store.config?.ruleExtensions || [])

const canOperate = computed(() => Boolean(filters.project && filters.check))

/**
 * 当前范围上有没有提取规则维护权。
 *
 * 与 `canOperate` 分开：前者是「选没选范围」，后者是「有没有权限」。
 * 合并成一个会导致「没选范围」和「没权限」给出同一句提示，
 * 用户分不清是该先去左边选项目，还是该去找管理员要权限。
 */
const canRuleManage = computed(
  () =>
    canOperate.value &&
    auth.levelOfScope(filters.project, filters.check) >= LEVELS.RuleManage,
)

const deniedHint = computed(() => {
  if (!canOperate.value) {
    return '请先在左侧选择项目与检项'
  }
  if (!canRuleManage.value) {
    return '你对当前「项目 / 检项」没有提取规则维护权限'
  }
  return ''
})

const selectedNames = computed(() => selected.value.map((r) => r.fileName))

function formatSize(bytes) {
  if (bytes === null || bytes === undefined) return '-'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(2)} MB`
}

function formatTime(value) {
  if (!value) return '-'
  const d = new Date(value)
  const pad = (n) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(
    d.getMinutes(),
  )}`
}

async function loadCheckOptions() {
  if (!filters.project) {
    checkOptions.value = []
    return
  }
  try {
    checkOptions.value = await projectApi.checks(filters.project)
  } catch {
    checkOptions.value = []
  }
}

async function load() {
  if (!canOperate.value) {
    rows.value = []
    return
  }

  loading.value = true
  try {
    rows.value = await ruleApi.list(filters.project, filters.check)
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '加载提取规则失败')
    rows.value = []
  } finally {
    loading.value = false
  }
}

function onProjectChange() {
  filters.check = ''
  loadCheckOptions()
  load()
}

function resetScope() {
  filters.project = ''
  filters.check = ''
  checkOptions.value = []
  load()
}

function beforeUpload(file) {
  const allowed = allowedExtensions.value.map((e) => e.toLowerCase().replace('.', ''))
  const ext = (file.name.split('.').pop() || '').toLowerCase()

  if (allowed.length && !allowed.includes(ext)) {
    ElMessage.error(`不支持的规则文件类型 .${ext}，仅允许：${allowedExtensions.value.join(' ')}`)
    return false
  }

  const maxBytes = (store.config?.maxSizeMB || 100) * 1024 * 1024
  if (file.size > maxBytes) {
    ElMessage.error(`文件超过 ${store.config?.maxSizeMB || 100}MB 上限`)
    return false
  }

  return true
}

async function customUpload(option) {
  if (!canRuleManage.value) {
    ElMessage.warning(deniedHint.value || '没有维护提取规则的权限')
    uploading.value = false
    return
  }

  uploading.value = true
  try {
    await ruleApi.upload(filters.project, filters.check, option.file, (e) => {
      if (e.total) option.onProgress({ percent: Math.round((e.loaded / e.total) * 100) })
    })
    ElMessage.success(`提取规则已上传：${option.file.name}`)
    option.onSuccess?.({})
    store.bumpDataVersion()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '上传失败')
    option.onError?.(error)
  } finally {
    uploading.value = false
  }
}

// ── 备注：网页上直接改 ────────────────────────────────────────────────

function startEditNote(row) {
  if (!canRuleManage.value) {
    ElMessage.warning('没有维护提取规则的权限')
    return
  }
  editingNote.value = row.fileName
  noteDraft.value = row.note || ''
}

function cancelEditNote() {
  editingNote.value = ''
  noteDraft.value = ''
}

async function saveNote(row) {
  // blur 与回车都会触发，用「是否仍在编辑该行」做去重
  if (editingNote.value !== row.fileName) return

  const text = noteDraft.value.trim()
  const current = row.note || ''

  editingNote.value = ''
  noteDraft.value = ''

  if (text === current) return // 没改动就不发请求

  try {
    const updated = await ruleApi.updateNote({
      project: filters.project,
      check: filters.check,
      fileName: row.fileName,
      note: text,
    })
    row.note = updated.note
    row.noteUpdatedAt = updated.noteUpdatedAt
    ElMessage.success(text ? '备注已保存' : '备注已清除')
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '保存备注失败')
  }
}

// ── 其他行操作 ────────────────────────────────────────────────────────

async function copyPath(row) {
  try {
    await navigator.clipboard.writeText(row.accessPath)
    ElMessage.success('规则路径已复制')
  } catch {
    ElMessage.warning('浏览器拒绝剪贴板访问，请手动复制')
  }
}

async function removeRow(row) {
  if (!canRuleManage.value) {
    ElMessage.warning('没有维护提取规则的权限')
    return
  }

  try {
    await ElMessageBox.confirm(
      `确认删除提取规则 ${row.fileName}？备注会一并清除，此操作不可撤销。`,
      '二次确认',
      { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' },
    )
  } catch {
    return
  }

  try {
    await ruleApi.remove(filters.project, filters.check, row.fileName)
    ElMessage.success('提取规则已删除')
    store.bumpDataVersion()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '删除失败')
  }
}

function openCopyDialog() {
  if (!canRuleManage.value) {
    ElMessage.warning('没有维护提取规则的权限')
    return
  }

  if (!selectedNames.value.length) {
    ElMessage.warning('请先勾选要复制的规则')
    return
  }
  copyDialog.visible = true
}

function onCopied() {
  selected.value = []
  load()
}

// 跟随左侧树的选中范围（与模板/文档面板一致）
watch(
  () => [store.currentProject, store.currentCheck],
  ([project, check]) => {
    filters.project = project || ''
    filters.check = check || ''
    loadCheckOptions()
    load()
  },
)

watch(
  () => store.dataVersion,
  () => load(),
)

watch(
  () => props.keyword,
  () => load(),
)

onMounted(() => {
  filters.project = store.currentProject || ''
  filters.check = store.currentCheck || ''
  loadCheckOptions()
  // 首次进入时 watch 不会触发（值没有「变化」），必须主动拉一次，
  // 否则表格永远是空的，用户得手动点刷新才看得到规则。
  load()
})

defineOptions({ name: 'RulePanel' })
</script>

<template>
  <div class="rule-panel">
    <div class="filter-bar">
      <el-select
        v-model="filters.project"
        placeholder="全部项目"
        clearable
        size="small"
        style="width: 140px"
        @change="onProjectChange"
      >
        <el-option v-for="p in store.projects" :key="p.name" :label="p.name" :value="p.name" />
      </el-select>

      <el-select
        v-model="filters.check"
        placeholder="全部检项"
        clearable
        size="small"
        style="width: 120px"
        @change="load"
      >
        <el-option v-for="c in checkOptions" :key="c.name" :label="c.name" :value="c.name" />
      </el-select>

      <el-button size="small" @click="resetScope">返回全部</el-button>

      <span class="flex-spacer" />

      <el-upload
        :show-file-list="false"
        :before-upload="beforeUpload"
        :http-request="customUpload"
        :disabled="!canRuleManage"
      >
        <el-button type="primary" size="small" :loading="uploading" :disabled="!canRuleManage">
          <el-icon><Upload /></el-icon>&nbsp;上传规则
        </el-button>
      </el-upload>

      <el-button
        size="small"
        :disabled="!canRuleManage || !selectedNames.length"
        @click="openCopyDialog"
      >
        <el-icon><CopyDocument /></el-icon>&nbsp;复制到…
        <span v-if="selectedNames.length">（{{ selectedNames.length }}）</span>
      </el-button>

      <el-button size="small" @click="load">
        <el-icon><RefreshRight /></el-icon>&nbsp;刷新
      </el-button>
    </div>

    <!-- 无维护权时用一条窄提示替代整块 el-alert：这里只是解释按钮为什么是灰的 -->
    <div v-if="deniedHint" class="denied-strip">
      <el-icon><InfoFilled /></el-icon>
      <span>{{ deniedHint }}（维护规则需要该范围的「规则管理」权限）</span>
    </div>

    <el-table
      v-loading="loading"
      :data="rows"
      border
      size="small"
      height="100%"
      class="rule-table"
      row-key="fileName"
      @selection-change="(v) => (selected = v)"
    >
      <el-table-column type="selection" width="46" reserve-selection />

      <el-table-column prop="fileName" label="规则文件" min-width="180" show-overflow-tooltip />
      <el-table-column prop="extension" label="类型" width="72" />
      <el-table-column label="大小" width="82">
        <template #default="{ row }">{{ formatSize(row.size) }}</template>
      </el-table-column>
      <el-table-column label="修改时间" width="136">
        <template #default="{ row }">{{ formatTime(row.modifiedAt) }}</template>
      </el-table-column>

      <!-- 备注：点一下就能改，回车/失焦即保存 -->
      <el-table-column label="备注" min-width="200">
        <template #default="{ row }">
          <el-input
            v-if="editingNote === row.fileName"
            v-model="noteDraft"
            size="small"
            placeholder="输入备注后回车保存，Esc 取消"
            autofocus
            @blur="saveNote(row)"
            @keyup.enter="saveNote(row)"
            @keyup.esc="cancelEditNote"
          />
          <div v-else class="note-cell" @click="startEditNote(row)">
            <span v-if="row.note" class="note-text">{{ row.note }}</span>
            <span v-else class="note-empty">点击填写备注</span>
          </div>
        </template>
      </el-table-column>

      <el-table-column label="操作" width="196" fixed="right">
        <template #default="{ row }">
          <el-button
            link
            type="primary"
            size="small"
            :disabled="!canRuleManage"
            @click="startEditNote(row)"
          >
            {{ row.note ? '改备注' : '加备注' }}
          </el-button>
          <el-button link type="primary" size="small" @click="copyPath(row)">复制路径</el-button>
          <el-button link type="danger" size="small" :disabled="!canRuleManage" @click="removeRow(row)">
            删除
          </el-button>
        </template>
      </el-table-column>

      <template #empty>
        <span style="color: var(--ot-text-faint)">
          {{ canOperate ? '该文件夹下还没有提取规则，点右上角「上传提取规则」添加。' : '请先选择项目与检项。' }}
        </span>
      </template>
    </el-table>

    <CopyToDialog
      v-model:visible="copyDialog.visible"
      kind="rule"
      :project="filters.project"
      :check="filters.check"
      :file-names="selectedNames"
      @copied="onCopied"
    />
  </div>
</template>

<style scoped>
.rule-panel {
  height: 100%;
  display: flex;
  flex-direction: column;
  min-height: 0;
  padding: 0 16px 12px;
}

.filter-bar {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  padding: 10px 0 8px;
  flex: none;
}

/* 同 FilePanel：不让控件被 flex 压缩 */
.filter-bar > *:not(.flex-spacer) {
  flex: none;
}

.flex-spacer {
  flex: 1;
}

.rule-table {
  flex: 1;
  min-height: 0;
}

.denied-strip {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
  padding: 6px 10px;
  border-radius: 6px;
  background: var(--ot-surface-2);
  border: 1px solid var(--ot-border-soft);
  color: var(--ot-text-muted);
  font-size: 12px;
  flex: none;
}

.note-cell {
  min-height: 22px;
  cursor: text;
  padding: 2px 4px;
  border-radius: 3px;
}

.note-cell:hover {
  background: var(--ot-surface-2);
  outline: 1px dashed var(--ot-border);
}

.note-text {
  color: var(--ot-text);
}

.note-empty {
  color: var(--ot-text-faint);
  font-size: 12px;
}
</style>
