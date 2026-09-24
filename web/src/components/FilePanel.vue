<script setup>
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  documentApi,
  projectApi,
  systemApi,
  templateApi,
} from '../api'
import { useAppStore } from '../store'
import { useAuthStore, LEVELS } from '../auth'
import OpenFileDialog from './OpenFileDialog.vue'
import AttachmentDialog from './AttachmentDialog.vue'
import CopyToDialog from './CopyToDialog.vue'

const props = defineProps({
  kind: { type: String, required: true },
  keyword: { type: String, default: '' },
  scopeText: { type: String, default: '' },
})

const store = useAppStore()
const auth = useAuthStore()

const rows = ref([])
const total = ref(0)
const loading = ref(false)
const syncing = ref(false)

const openDialog = reactive({ visible: false, file: null })

/** 模板列表里「上传附件」的落点；文档列表里会额外带上目标文档 */
const attachmentDialog = reactive({ visible: false, project: '', check: '', targetDocument: null })

/** 文档/模板「复制到…」：目标路径由用户在弹窗里选择，而不是复制到当前文件夹 */
const copyDialog = reactive({ visible: false, project: '', check: '', fileNames: [] })

const filters = reactive({
  project: '',
  check: '',
  extension: '',
  dateRange: null,
  sortBy: 'created',
  sortDir: 'desc',
  page: 1,
  pageSize: 20,
})

const isTemplates = computed(() => props.kind === 'templates')
const title = computed(() => (isTemplates.value ? '模板' : '文档'))

/**
 * 权限门（仅决定按钮是否显示/可点；真正的拦截在服务端）。
 *
 * 逐行按 `项目/检项` 判定，而不是整页判定：同一个人可能对 A 项目有编辑权、
 * 对 B 项目只有只读权，整页放行会让他看到点了就报 403 的按钮。
 */
const canWriteRow = (row) => auth.levelOfScope(row.project, row.check) >= LEVELS.Write
const canManageRow = (row) => auth.levelOfScope(row.project, row.check) >= LEVELS.Manage

/** 当前筛选范围上的写权限（上传模板需要它） */
const canWriteScope = computed(
  () =>
    Boolean(filters.project && filters.check) &&
    auth.levelOfScope(filters.project, filters.check) >= LEVELS.Write,
)

/** 整列入口的显示条件：任何一处分区都没有写权时，整列隐藏而不是全灰 */
const showWriteColumns = computed(() => auth.canAnyWrite)
const showDeleteAction = computed(() => auth.canAnyManage)

const checkOptions = ref([])

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

function buildParams() {
  const params = {
    project: filters.project || undefined,
    check: filters.check || undefined,
    keyword: props.keyword || undefined,
    extension: filters.extension || undefined,
    sortBy: filters.sortBy,
    sortDir: filters.sortDir,
    page: filters.page,
    pageSize: filters.pageSize,
  }

  if (filters.dateRange?.length === 2) {
    params.from = filters.dateRange[0]
    params.to = filters.dateRange[1]
  }

  return params
}

async function load() {
  loading.value = true
  try {
    const data = isTemplates.value
      ? await store.listTemplates(buildParams())
      : await store.listDocuments(buildParams())
    rows.value = data.items
    total.value = data.total
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '加载失败')
    rows.value = []
    total.value = 0
  } finally {
    loading.value = false
  }
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

function onProjectFilterChange() {
  filters.check = ''
  loadCheckOptions()
  filters.page = 1
  load()
}

function onQuery() {
  filters.page = 1
  load()
}

function onReset() {
  filters.project = ''
  filters.check = ''
  filters.extension = ''
  filters.dateRange = null
  filters.sortBy = 'created'
  filters.sortDir = 'desc'
  filters.page = 1
  checkOptions.value = []
  load()
}

/**
 * 点文件名：装了插件就直接唤起本机 Office（设计文档 §8.3 的一键体验），
 * 没装则弹出详情弹窗。
 */
function openFile(row) {
  if (store.pluginConfirmed) {
    window.location.href = store.buildProtocolUrl(row)
    return
  }

  showDetail(row)
}

/**
 * 详情弹窗：**无论有没有标记安装插件都能打开**。
 *
 * 之所以单独拆出来：装了插件之后 openFile() 会直接跳转协议、不再弹窗，
 * 于是「上传附件」「复制路径」「协议链接」这些只在这个弹窗里的入口就全部够不着了。
 * 操作列保留一个显式的「详情」按钮走这里，保证这些入口始终可达。
 */
function showDetail(row) {
  openDialog.file = row
  openDialog.visible = true
}

async function onSortChange({ prop, order }) {
  if (!order) return
  filters.sortBy = prop === 'fileName' ? 'name' : prop === 'size' ? 'size' : 'created'
  filters.sortDir = order === 'ascending' ? 'asc' : 'desc'
  filters.page = 1
  await load()
}

// ── 模板：上传 ────────────────────────────────────────────────────────
const uploading = ref(false)

async function customUpload(option) {
  if (!filters.project || !filters.check) {
    ElMessage.warning('请先在左侧选择项目与检项，再上传模板')
    uploading.value = false
    return
  }

  if (!canWriteScope.value) {
    ElMessage.error('你对当前「项目 / 检项」没有编辑权限，无法上传模板')
    uploading.value = false
    return
  }

  uploading.value = true
  try {
    await templateApi.upload(filters.project, filters.check, option.file, (e) => {
      if (e.total) option.onProgress({ percent: Math.round((e.loaded / e.total) * 100) })
    })
    ElMessage.success(`模板已上传：${option.file.name}`)
    option.onSuccess?.({})
    store.bumpDataVersion()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '上传失败')
    option.onError?.(error)
  } finally {
    uploading.value = false
  }
}

function beforeUpload(file) {
  const allowed = store.allowedExtensions.map((e) => e.toLowerCase().replace('.', ''))
  const ext = (file.name.split('.').pop() || '').toLowerCase()
  if (!allowed.includes(ext)) {
    ElMessage.error(`不支持的扩展名 .${ext}，仅允许：${store.allowedExtensions.join(' ')}`)
    return false
  }
  const maxBytes = (store.config?.maxSizeMB || 100) * 1024 * 1024
  if (file.size > maxBytes) {
    ElMessage.error(`文件超过 ${store.config?.maxSizeMB || 100}MB 上限`)
    return false
  }
  return true
}

// ── 模板：复制 / 重命名 / 删除；文档：生成 / 重命名 / 删除 ──────────────
async function createDocument(row) {
  try {
    const created = await documentApi.create({
      project: row.project,
      check: row.check,
      templateFileName: row.fileName,
    })
    ElMessage.success(`已生成文档：${created.fileName}`)
    store.bumpDataVersion()

    // 需求：新建文档后直接弹出「打开文件」弹窗 —— 生成之后马上就能打开/上传附件，
    // 不必再去「文档列表」里找。这里用的是接口返回的 DocumentDto（含 accessPath）。
    openDialog.file = created
    openDialog.visible = true
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '生成失败')
  }
}

/** 打开「复制到…」弹窗，让用户选择目标项目/检项 */
function openCopyTo(row) {
  if (!row?.project || !row?.check) {
    ElMessage.warning('该行缺少项目/检项信息，无法复制')
    return
  }

  copyDialog.project = row.project
  copyDialog.check = row.check
  copyDialog.fileNames = [row.fileName]
  copyDialog.visible = true
}

/**
 * 打开附件弹窗。
 *
 * 模板列表：附件与具体模板无关，只定位到项目/检项（目标文档留空）。
 * 文档列表：附件就是为了把内容提取进这份文档，直接把该行作为**目标文档**带上，
 *          省掉「先去打开弹窗再点上传附件」的两步。
 */
function openAttachment(row) {
  attachmentDialog.project = row?.project || filters.project || ''
  attachmentDialog.check = row?.check || filters.check || ''

  if (!attachmentDialog.project || !attachmentDialog.check) {
    ElMessage.warning('请先在左侧选择项目与检项，再上传附件')
    return
  }

  attachmentDialog.targetDocument = isTemplates.value ? null : row
  attachmentDialog.visible = true
}

/**
 * 原地复制一份模板。
 *
 * 与「复制到…」并存而不是合并：做模板变体（在同一目录里改一版）是高频操作，
 * 走弹窗要先选项目再选检项，两次选择对高频动作来说太重。
 */
async function duplicateTemplate(row) {
  try {
    const created = await templateApi.copy({
      project: row.project,
      check: row.check,
      sourceFileName: row.fileName,
    })
    ElMessage.success(`已复制为：${created.fileName}`)
    store.bumpDataVersion()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '复制失败')
  }
}

async function rename(row) {
  let value
  try {
    const result = await ElMessageBox.prompt('请输入新文件名（含扩展名）', '重命名', {
      inputValue: row.fileName,
      confirmButtonText: '确定',
      cancelButtonText: '取消',
      inputValidator: (v) => (v && v.trim() ? true : '文件名不能为空'),
    })
    value = result.value.trim()
  } catch {
    return
  }

  const payload = { project: row.project, check: row.check, fileName: row.fileName, newFileName: value }

  try {
    if (isTemplates.value) await templateApi.rename(payload)
    else await documentApi.rename(payload)
    ElMessage.success('重命名成功')
    store.bumpDataVersion()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '重命名失败')
  }
}

async function remove(row) {
  try {
    await ElMessageBox.confirm(
      `确认将 ${row.fileName} 移入回收站？可在回收站中恢复或彻底删除。`,
      '二次确认',
      {
        type: 'warning',
        confirmButtonText: '移入回收站',
        cancelButtonText: '取消',
      },
    )
  } catch {
    return
  }

  const params = { project: row.project, check: row.check, fileName: row.fileName }

  try {
    if (isTemplates.value) await templateApi.remove(params)
    else await documentApi.remove(params)
    ElMessage.success('已移入回收站')
    store.bumpDataVersion()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '删除失败')
  }
}

async function copyPath(row) {
  try {
    await navigator.clipboard.writeText(row.accessPath)
    ElMessage.success('路径已复制')
  } catch {
    ElMessage.warning('浏览器拒绝剪贴板访问，请手动复制')
  }
}

async function syncMetadata() {
  syncing.value = true
  try {
    const result = await systemApi.sync()
    ElMessage.success(
      `同步完成：模板 +${result.templatesAdded}/-${result.templatesRemoved}，文档 +${result.documentsAdded}/-${result.documentsRemoved}`,
    )
    store.bumpDataVersion()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '同步失败')
  } finally {
    syncing.value = false
  }
}

function resetScope() {
  filters.project = ''
  filters.check = ''
  checkOptions.value = []
  onQuery()
}

/** 「更多」下拉的动作分发 */
function onRowAction(command, row) {
  switch (command) {
    case 'copyPath':
      return copyPath(row)
    case 'copyTo':
      return openCopyTo(row)
    case 'duplicate':
      return duplicateTemplate(row)
    case 'rename':
      return rename(row)
    default:
      return undefined
  }
}

// 首屏就要有数据，否则用户看到的是一张空表
onMounted(load)

// 左侧树选中项目/检项后联动过滤主区域（设计文档 §8.2）
watch(
  () => [store.currentProject, store.currentCheck],
  ([project, check]) => {
    filters.project = project || ''
    filters.check = check || ''
    loadCheckOptions()
    filters.page = 1
    load()
  },
)

// 任一写操作后由全局版本号驱动刷新（含跨面板）
watch(
  () => store.dataVersion,
  () => load(),
)

// 顶部全局搜索框变化后重新查询
watch(
  () => props.keyword,
  () => {
    filters.page = 1
    load()
  },
)

defineExpose({ load })

defineOptions({ name: 'FilePanel' })
</script>

<template>
  <div>
    <el-form class="search-bar" :inline="true" label-width="70px" @submit.prevent>
      <el-form-item label="项目">
        <el-select
          v-model="filters.project"
          placeholder="全部"
          clearable
          style="width: 150px"
          @change="onProjectFilterChange"
        >
          <el-option v-for="p in store.projects" :key="p.name" :label="p.name" :value="p.name" />
        </el-select>
      </el-form-item>
      <el-form-item label="检项">
        <el-select v-model="filters.check" placeholder="全部" clearable style="width: 130px" @change="onQuery">
          <el-option v-for="c in checkOptions" :key="c.name" :label="c.name" :value="c.name" />
        </el-select>
      </el-form-item>
      <el-form-item label="扩展名">
        <el-select v-model="filters.extension" placeholder="全部" clearable style="width: 120px" @change="onQuery">
          <el-option v-for="e in store.allowedExtensions" :key="e" :label="e" :value="e" />
        </el-select>
      </el-form-item>
      <el-form-item label="时间范围">
        <el-date-picker
          v-model="filters.dateRange"
          type="daterange"
          value-format="YYYY-MM-DDTHH:mm:ss"
          start-placeholder="开始"
          end-placeholder="结束"
          style="width: 260px"
        />
      </el-form-item>
      <el-form-item>
        <el-button type="primary" @click="onQuery">查询</el-button>
        <el-button @click="onReset">重置</el-button>
      </el-form-item>
    </el-form>

    <div class="table-toolbar">
      <span style="color: #606266">
        <strong>{{ title }}列表</strong>
        <el-tag size="small" type="info" style="margin-left: 8px">{{ scopeText }}</el-tag>
      </span>

      <span v-if="filters.project || filters.check" style="margin-left: 8px">
        <el-button link type="primary" size="small" @click="resetScope">返回全部</el-button>
      </span>

      <span class="spacer" />

      <el-upload
        v-if="isTemplates && showWriteColumns"
        :show-file-list="false"
        :before-upload="beforeUpload"
        :http-request="customUpload"
        accept=".docx,.xlsx,.pptx,.docm,.xlsm,.pptm"
      >
        <el-button type="primary" :loading="uploading" :disabled="!canWriteScope">
          <el-icon><Upload /></el-icon>&nbsp;上传模板
        </el-button>
      </el-upload>

      <el-button
        v-if="isTemplates && showDeleteAction"
        :loading="syncing"
        @click="syncMetadata"
      >
        <el-icon><Refresh /></el-icon>&nbsp;同步元数据
      </el-button>

      <el-button @click="load">
        <el-icon><RefreshRight /></el-icon>&nbsp;刷新
      </el-button>
    </div>

    <el-table
      v-loading="loading"
      :data="rows"
      border
      stripe
      size="small"
      @sort-change="onSortChange"
    >
      <el-table-column prop="fileName" label="文件名" min-width="160" sortable="custom" show-overflow-tooltip>
        <template #default="{ row }">
          <el-button link type="primary" @click="openFile(row)">{{ row.fileName }}</el-button>
        </template>
      </el-table-column>
      <el-table-column prop="extension" label="类型" width="72" />
      <el-table-column prop="size" label="大小" width="82" sortable="custom">
        <template #default="{ row }">{{ formatSize(row.size) }}</template>
      </el-table-column>
      <el-table-column
        :label="isTemplates ? '修改时间' : '创建时间'"
        width="136"
        sortable="custom"
        prop="time"
      >
        <template #default="{ row }">{{ formatTime(isTemplates ? row.modifiedAt : row.createdAt) }}</template>
      </el-table-column>
      <el-table-column prop="project" label="项目" width="90" />
      <el-table-column prop="check" label="检项" width="74" />

      <!--
        需求：新建文档不再埋在「更多」里 —— 独立成一列，按钮足够醒目。
        整列在没有任何编辑权时隐藏：给只读用户摆一列全灰的按钮，
        除了让他以为自己被针对之外没有用处。
      -->
      <el-table-column v-if="isTemplates && showWriteColumns" label="新建文档" width="102" fixed="right">
        <template #default="{ row }">
          <el-button
            type="primary"
            size="small"
            :disabled="!canWriteRow(row)"
            @click="createDocument(row)"
          >
            新建文档
          </el-button>
        </template>
      </el-table-column>

      <!--
        附件列在**两个列表都有**：装了桌面插件后点文件名会直接跳转协议、不再弹窗，
        附件入口不能只挂在弹窗里（否则装了插件就再也传不了附件）。
      -->
      <el-table-column v-if="showWriteColumns" label="附件" width="102" fixed="right">
        <template #default="{ row }">
          <el-button size="small" :disabled="!canWriteRow(row)" @click="openAttachment(row)">
            上传附件
          </el-button>
        </template>
      </el-table-column>

      <el-table-column label="操作" :width="showDeleteAction ? 148 : 120" fixed="right">
        <template #default="{ row }">
          <!-- 详情弹窗永远可打开：路径、协议链接、上传附件入口都在里面 -->
          <el-button link type="primary" size="small" @click="showDetail(row)">详情</el-button>
          <el-dropdown trigger="click" @command="(cmd) => onRowAction(cmd, row)">
            <el-button link type="primary" size="small">
              更多<el-icon><ArrowDown /></el-icon>
            </el-button>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item command="copyPath">复制路径</el-dropdown-item>

                <!--
                  模板与文档现在都走「复制到…」：目标项目/检项由用户在弹窗里选，
                  不再只能复制到当前文件夹。模板额外保留「原地复制一份」，
                  因为做模板变体是高频操作，不该每次都走两段下拉选择。
                -->
                <el-dropdown-item
                  v-if="showWriteColumns"
                  :disabled="!canWriteRow(row)"
                  command="copyTo"
                >
                  复制到…
                </el-dropdown-item>

                <el-dropdown-item
                  v-if="isTemplates && showWriteColumns"
                  :disabled="!canWriteRow(row)"
                  command="duplicate"
                >
                  原地复制一份
                </el-dropdown-item>

                <el-dropdown-item
                  v-if="showWriteColumns"
                  :disabled="!canWriteRow(row)"
                  divided
                  command="rename"
                >
                  重命名
                </el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>

          <el-button
            v-if="showDeleteAction"
            link
            type="danger"
            size="small"
            :disabled="!canManageRow(row)"
            @click="remove(row)"
          >
            删除
          </el-button>
        </template>
      </el-table-column>
    </el-table>

    <div style="margin-top: 14px; display: flex; justify-content: flex-end">
      <el-pagination
        v-model:current-page="filters.page"
        v-model:page-size="filters.pageSize"
        :total="total"
        :page-sizes="[20, 50, 100]"
        layout="total, sizes, prev, pager, next"
        @current-change="load"
        @size-change="onQuery"
      />
    </div>

    <OpenFileDialog v-model:visible="openDialog.visible" :file="openDialog.file" />

    <!-- 「上传附件」列专用：模板列表不带目标文档，文档列表带上该行文档 -->
    <AttachmentDialog
      v-model:visible="attachmentDialog.visible"
      :project="attachmentDialog.project"
      :check="attachmentDialog.check"
      :target-document="attachmentDialog.targetDocument"
    />

    <!-- 「复制到…」：模板与文档共用，目标项目/检项由用户选择 -->
    <CopyToDialog
      v-model:visible="copyDialog.visible"
      :kind="isTemplates ? 'template' : 'document'"
      :project="copyDialog.project"
      :check="copyDialog.check"
      :file-names="copyDialog.fileNames"
    />
  </div>
</template>
