<script setup>
import { computed, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { attachmentApi, ruleApi } from '../api'
import { useAppStore } from '../store'
import { useAuthStore, LEVELS } from '../auth'

const props = defineProps({
  visible: { type: Boolean, default: false },
  /** 附件归属的项目 / 检项 */
  project: { type: String, default: '' },
  check: { type: String, default: '' },
  /**
   * 提取的目标文档（新建文档后由「打开文件」弹窗传入）。
   * 没有目标文档时只能上传和删除，不能提取 —— 提取必须有落点。
   */
  targetDocument: { type: Object, default: null },
})

const emit = defineEmits(['update:visible'])

const store = useAppStore()
const auth = useAuthStore()

const rows = ref([])
const rules = ref([])
const loading = ref(false)
const uploading = ref(false)
const extracting = ref(false)
/** 选中的提取规则**文件名**（规则是当前文件夹下的文件，不是配置里的常量） */
const ruleFileName = ref('')

const state = reactive({ tableHeight: 260 })

/** 后端尚未实现提取脚本，前端据此把「提取」按钮置灰并说明原因 */
const extractionAvailable = computed(() => store.config?.extractionAvailable === true)

const allowedExtensions = computed(() => store.config?.attachmentExtensions || [])

const scopeText = computed(() =>
  props.project ? (props.check ? `${props.project} / ${props.check}` : props.project) : '未选择项目',
)

const canOperate = computed(() => Boolean(props.project && props.check))

/**
 * 附件落在 项目/检项 上，权限也按该范围判：
 * 上传是编辑动作，删除是管理动作。读取与提取只要求能看到附件。
 */
const canWrite = computed(
  () => canOperate.value && auth.levelOfScope(props.project, props.check) >= LEVELS.Write,
)

const canManage = computed(
  () => canOperate.value && auth.levelOfScope(props.project, props.check) >= LEVELS.Manage,
)

const selectedRule = computed(
  () => rules.value.find((r) => r.fileName === ruleFileName.value) || null,
)

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

async function load() {
  if (!canOperate.value) {
    rows.value = []
    return
  }

  loading.value = true
  try {
    rows.value = await attachmentApi.list(props.project, props.check)
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '加载附件失败')
    rows.value = []
  } finally {
    loading.value = false
  }
}

/** 规则来自**当前文件夹**（ExtractionRules\{项目}\{检项}），与「提取规则列表」页是同一份数据 */
async function loadRules() {
  if (!canOperate.value) {
    rules.value = []
    return
  }

  try {
    rules.value = await ruleApi.list(props.project, props.check)

    // 选中的规则若已不存在（被删/被改名），清掉选择，避免提交一个悬空的文件名
    if (ruleFileName.value && !rules.value.some((r) => r.fileName === ruleFileName.value)) {
      ruleFileName.value = ''
    }

    if (!ruleFileName.value && rules.value.length) {
      ruleFileName.value = rules.value[0].fileName
    }
  } catch {
    rules.value = []
  }
}

watch(
  () => props.visible,
  (open) => {
    if (!open) return
    load()
    loadRules()
  },
)

watch(
  () => [props.project, props.check],
  () => {
    if (props.visible) {
      load()
      loadRules()
    }
  },
)

// 在「提取规则列表」页上传/删除规则后，这里的选择项要跟着更新
watch(
  () => store.dataVersion,
  () => {
    if (props.visible) loadRules()
  },
)

function beforeUpload(file) {
  const allowed = allowedExtensions.value.map((e) => e.toLowerCase().replace('.', ''))
  const ext = (file.name.split('.').pop() || '').toLowerCase()

  if (allowed.length && !allowed.includes(ext)) {
    ElMessage.error(`不支持的附件类型 .${ext}，仅允许：${allowedExtensions.value.join(' ')}`)
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
  if (!canOperate.value) {
    ElMessage.warning('请先在左侧选择项目与检项')
    uploading.value = false
    return
  }

  if (!canWrite.value) {
    ElMessage.error('你对当前「项目 / 检项」没有编辑权限，无法上传附件')
    uploading.value = false
    return
  }

  uploading.value = true
  try {
    await attachmentApi.upload(props.project, props.check, option.file, (e) => {
      if (e.total) option.onProgress({ percent: Math.round((e.loaded / e.total) * 100) })
    })
    ElMessage.success(`附件已上传：${option.file.name}`)
    option.onSuccess?.({})
    await load()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '上传失败')
    option.onError?.(error)
  } finally {
    uploading.value = false
  }
}

async function extract(row) {
  if (!props.targetDocument) {
    ElMessage.warning('没有目标文档：请先「新建文档」，再从弹出窗口里上传并提取。')
    return
  }

  if (!ruleFileName.value) {
    ElMessage.warning('请先选择提取规则。若该文件夹下还没有规则，请到「提取规则列表」上传。')
    return
  }

  extracting.value = true
  try {
    await attachmentApi.extract({
      project: props.project,
      check: props.check,
      attachmentFileName: row.fileName,
      ruleFileName: ruleFileName.value,
      targetDocumentFileName: props.targetDocument.fileName,
    })
    ElMessage.success('提取完成，内容已写入文档')
    store.bumpDataVersion()
  } catch (error) {
    // 后端目前返回 501 + 明确说明（接口已预留、脚本未实现），原样展示最有信息量
    ElMessage.warning({
      message: error.friendlyMessage || '提取失败',
      duration: 6000,
      showClose: true,
    })
  } finally {
    extracting.value = false
  }
}

async function removeRow(row) {
  if (!canManage.value) {
    ElMessage.warning('删除附件需要「管理」权限')
    return
  }

  try {
    await ElMessageBox.confirm(`确认删除附件 ${row.fileName}？`, '二次确认', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消',
    })
  } catch {
    return
  }

  try {
    await attachmentApi.remove(props.project, props.check, row.fileName)
    ElMessage.success('附件已删除')
    await load()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '删除失败')
  }
}

async function copyPath(row) {
  try {
    await navigator.clipboard.writeText(row.accessPath)
    ElMessage.success('附件路径已复制')
  } catch {
    ElMessage.warning('浏览器拒绝剪贴板访问，请手动复制')
  }
}

function close() {
  emit('update:visible', false)
}
</script>

<template>
  <el-dialog
    :model-value="visible"
    title="上传附件"
    width="820px"
    @update:model-value="emit('update:visible', $event)"
  >
    <el-alert
      v-if="!extractionAvailable"
      type="info"
      :closable="false"
      show-icon
      title="提取功能接口已预留，脚本尚未实现"
      description="附件可以正常上传、查看、删除；「提取」按钮的完整参数链路也已接通，待提取脚本编写完成后即可启用（届时本提示会自动消失）。"
      style="margin-bottom: 12px"
    />

    <el-descriptions :column="2" border size="small" style="margin-bottom: 12px">
      <el-descriptions-item label="项目 / 检项">{{ scopeText }}</el-descriptions-item>
      <el-descriptions-item label="目标文档">
        <span v-if="targetDocument">{{ targetDocument.fileName }}</span>
        <span v-else style="color: #e6a23c">未指定（提取需要先新建文档）</span>
      </el-descriptions-item>
    </el-descriptions>

    <div class="attachment-toolbar">
      <el-upload
        v-if="canWrite"
        :show-file-list="false"
        :before-upload="beforeUpload"
        :http-request="customUpload"
        :disabled="!canWrite"
      >
        <el-button type="primary" :loading="uploading" :disabled="!canWrite">
          <el-icon><Upload /></el-icon>&nbsp;上传附件
        </el-button>
      </el-upload>

      <el-button :disabled="!canOperate" @click="load">
        <el-icon><RefreshRight /></el-icon>&nbsp;刷新
      </el-button>

      <span class="spacer" />

      <span class="rule-label">提取规则</span>
      <el-select
        v-model="ruleFileName"
        placeholder="该文件夹下暂无规则"
        size="small"
        style="width: 240px"
        :disabled="!rules.length"
      >
        <el-option v-for="r in rules" :key="r.fileName" :label="r.fileName" :value="r.fileName" />
      </el-select>
    </div>

    <div v-if="selectedRule?.note" class="rule-desc">备注：{{ selectedRule.note }}</div>
    <div v-else-if="!rules.length" class="rule-desc warn">
      该文件夹下还没有提取规则。请到「提取规则列表」页上传，或从其他文件夹复制过来。
    </div>

    <el-table v-loading="loading" :data="rows" border stripe size="small" :max-height="state.tableHeight">
      <el-table-column prop="fileName" label="附件名" min-width="240" show-overflow-tooltip />
      <el-table-column prop="extension" label="类型" width="80" />
      <el-table-column label="大小" width="95">
        <template #default="{ row }">{{ formatSize(row.size) }}</template>
      </el-table-column>
      <el-table-column label="上传时间" width="150">
        <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
      </el-table-column>
      <el-table-column label="操作" width="200" fixed="right">
        <template #default="{ row }">
          <el-tooltip
            :disabled="extractionAvailable"
            content="提取脚本尚未实现（接口已预留）"
            placement="top"
          >
            <span>
              <el-button
                type="primary"
                link
                size="small"
                :disabled="!extractionAvailable || extracting"
                @click="extract(row)"
              >
                提取
              </el-button>
            </span>
          </el-tooltip>
          <el-button link type="primary" size="small" @click="copyPath(row)">复制路径</el-button>
          <el-button link type="danger" size="small" :disabled="!canManage" @click="removeRow(row)">
            删除
          </el-button>
        </template>
      </el-table-column>
    </el-table>

    <div v-if="!rows.length && !loading" class="empty-tip" style="margin-top: 10px">
      暂无附件。上传后选择提取规则，点「提取」即可把内容填入目标文档。
    </div>

    <template #footer>
      <el-button @click="close">关闭</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.attachment-toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
}

.attachment-toolbar .spacer {
  flex: 1;
}

.rule-label {
  color: #606266;
  font-size: 13px;
}

.rule-desc {
  color: #909399;
  font-size: 12px;
  margin-bottom: 10px;
}

.rule-desc.warn {
  color: #e6a23c;
}
</style>
