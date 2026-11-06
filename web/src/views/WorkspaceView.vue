<script setup>
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage } from '../ui/message'
import { navigate } from '../router'
import { elnApi } from '../api'
import { useAppStore } from '../store'
import { pushNotice } from '../ui/notices'
import { formatTime } from '../ui/format'
import ProjectTree from '../components/ProjectTree.vue'

/**
 * 记录中心（新的工作台）。
 *
 * 左侧仍是项目/检项目录（点选即筛选），右侧换成**记录列表**：
 * ELN 时代的主对象是"一条条记录"，模板只是填写时的格式选择，
 * 所以模板库单独成页，不再和工作区搅在一起。
 */
const store = useAppStore()

const rows = ref([])
const total = ref(0)
const loading = ref(false)

const filters = reactive({ status: '', keyword: '', page: 1, pageSize: 20 })

const createDialog = reactive({ visible: false, templateId: null, title: '', busy: false })
const templates = ref([])

const scopeEmpty = computed(() => !store.currentProject)
const scopeNoCheck = computed(() => !store.currentCheck)

const statusOptions = [
  { value: '', label: '全部状态' },
  { value: 'Draft', label: '草稿' },
  { value: 'Submitted', label: '已提交' },
  { value: 'Voided', label: '已作废' },
]

const statusText = (status) =>
  ({ Draft: '草稿', Submitted: '已提交', Voided: '已作废' })[status] ?? status

const statusTagType = (status) =>
  ({ Draft: 'info', Submitted: 'success', Voided: 'danger' })[status] ?? 'info'

async function load() {
  loading.value = true
  try {
    const result = await elnApi.records.list({
      project: store.currentProject || undefined,
      check: store.currentCheck || undefined,
      status: filters.status || undefined,
      keyword: filters.keyword || undefined,
      page: filters.page,
      pageSize: filters.pageSize,
    })

    rows.value = result.items
    total.value = result.total
  } catch (error) {
    ElMessageError(error)
  } finally {
    loading.value = false
  }
}

function ElMessageError(error) {
  // 单独提出来的原因：列表失败不该打断页面（树还能用），
  // 用悬浮通知而不是 Message，避免反复触发堆一排
  pushNotice({
    key: 'record-list-error',
    type: 'error',
    title: '记录列表加载失败',
    description: error.friendlyMessage || '请稍后重试。',
  })
}

async function openCreate() {
  if (scopeEmpty.value || scopeNoCheck.value) {
    ElMessage.warning('请先在左侧选择具体的检项（记录挂在检项下）')
    return
  }

  createDialog.visible = true
  createDialog.templateId = null
  createDialog.title = ''

  try {
    templates.value = await elnApi.templates.list({})
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '模板加载失败')
  }
}

async function submitCreate() {
  if (!createDialog.templateId) {
    ElMessage.warning('请选择模板')
    return
  }

  createDialog.busy = true
  try {
    const created = await elnApi.records.create({
      templateId: createDialog.templateId,
      project: store.currentProject,
      check: store.currentCheck,
      title: createDialog.title || undefined,
    })

    createDialog.visible = false
    ElMessage.success(`记录已创建：${created.recordNo}`)
    navigate('/record', { query: { id: created.id } })
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '创建失败')
  } finally {
    createDialog.busy = false
  }
}

function openRecord(row) {
  navigate('/record', { query: { id: row.id } })
}

onMounted(() => {
  load()
})

watch(
  () => [store.currentProject, store.currentCheck],
  () => {
    filters.page = 1
    load()
  },
)

watch(
  () => [filters.status, filters.keyword],
  () => {
    filters.page = 1
    load()
  },
)
</script>

<template>
  <div class="workspace">
    <div class="page-head">
      <div>
        <h1 class="page-head__title">记录中心</h1>
        <p class="page-head__sub">在线模板 · 填写记录 · 从 PDF 报告自动带入数据</p>
      </div>

      <div class="scope-bar" :class="{ 'is-empty': scopeEmpty }">
        <el-icon><FolderOpened /></el-icon>
        <template v-if="scopeEmpty">未选择目录</template>
        <template v-else>
          <span>{{ store.currentProject }}</span>
          <span class="scope-sep">/</span>
          <span v-if="store.currentCheck">{{ store.currentCheck }}</span>
          <span v-else class="scope-tag">全部检项</span>
        </template>
      </div>

      <div class="page-head__actions">
        <el-input
          v-model="filters.keyword"
          placeholder="搜索记录号 / 标题 / 模板 / 创建人"
          clearable
          class="keyword"
        >
          <template #prefix>
            <el-icon><Search /></el-icon>
          </template>
        </el-input>

        <el-button type="primary" :disabled="scopeEmpty || scopeNoCheck" @click="openCreate">
          <el-icon><Plus /></el-icon>&nbsp;新建记录
        </el-button>
      </div>
    </div>

    <div class="workspace-body">
      <aside class="workspace-aside ot-panel">
        <ProjectTree />
      </aside>

      <main class="workspace-main ot-panel">
        <div class="list-toolbar">
          <el-select v-model="filters.status" class="w-status">
            <el-option
              v-for="option in statusOptions"
              :key="option.value"
              :label="option.label"
              :value="option.value"
            />
          </el-select>

          <span class="ot-faint">共 {{ total }} 条</span>
          <span v-if="scopeNoCheck && !scopeEmpty" class="ot-faint">（当前为整个项目，含全部检项）</span>
        </div>

        <div v-loading="loading" class="list-body">
          <el-table :data="rows" border size="small" height="100%" @row-click="openRecord">
            <el-table-column prop="recordNo" label="记录号" width="235">
              <template #default="{ row }">
                <el-link type="primary" class="ot-mono">{{ row.recordNo }}</el-link>
              </template>
            </el-table-column>

            <el-table-column prop="title" label="标题" min-width="180" show-overflow-tooltip />

            <el-table-column prop="templateName" label="模板" width="170" show-overflow-tooltip>
              <template #default="{ row }">
                {{ row.templateName }}
                <span class="ot-faint">v{{ row.templateVersion }}</span>
              </template>
            </el-table-column>

            <el-table-column prop="project" label="项目" width="100" show-overflow-tooltip />
            <el-table-column prop="check" label="检项" width="78" />

            <el-table-column prop="status" label="状态" width="88" align="center">
              <template #default="{ row }">
                <el-tag :type="statusTagType(row.status)" size="small" effect="plain">
                  {{ statusText(row.status) }}
                </el-tag>
              </template>
            </el-table-column>

            <el-table-column prop="createdBy" label="创建人" width="90">
              <template #default="{ row }">{{ row.createdBy || '匿名' }}</template>
            </el-table-column>

            <el-table-column prop="createdAt" label="创建时间" width="140">
              <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
            </el-table-column>

            <el-table-column prop="fileCount" label="附件" width="60" align="center" />

            <template #empty>
              <div class="empty-state">
                <el-icon :size="26"><Notebook /></el-icon>
                <p class="empty-state__title">该范围下还没有记录</p>
                <p class="empty-state__hint">
                  {{ scopeEmpty ? '先在左侧选择项目 / 检项' : '点右上角「新建记录」开始填写' }}
                </p>
              </div>
            </template>
          </el-table>
        </div>

        <div class="list-foot">
          <el-pagination
            v-model:current-page="filters.page"
            v-model:page-size="filters.pageSize"
            :total="total"
            :page-sizes="[20, 50, 100]"
            layout="sizes, prev, pager, next"
            @current-change="load"
            @size-change="load"
          />
        </div>
      </main>
    </div>

    <el-dialog v-model="createDialog.visible" title="新建记录" width="500px">
      <el-form label-width="90px" @submit.prevent>
        <el-form-item label="项目 / 检项">
          <el-input :model-value="`${store.currentProject} / ${store.currentCheck}`" disabled />
        </el-form-item>

        <el-form-item label="模板" required>
          <el-select v-model="createDialog.templateId" placeholder="选择记录模板" class="w-full">
            <el-option
              v-for="template in templates"
              :key="template.id"
              :label="template.name"
              :value="template.id"
              :disabled="!template.isEnabled"
            >
              <span>{{ template.name }}</span>
              <span class="option-meta">{{ template.category || '未分类' }} · {{ template.fieldCount }} 字段</span>
            </el-option>
          </el-select>
          <p class="form-hint">
            没有合适的模板？到
            <el-link type="primary" @click="navigate('/templates')">模板库</el-link>
            新建或复制一份。
          </p>
        </el-form-item>

        <el-form-item label="标题">
          <el-input v-model="createDialog.title" placeholder="默认使用模板名" maxlength="200" />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="createDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="createDialog.busy" @click="submitCreate">创建并填写</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.workspace {
  height: 100%;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.keyword {
  width: 250px;
}

.workspace-body {
  flex: 1;
  display: flex;
  gap: 14px;
  min-height: 0;
}

.workspace-aside {
  width: 316px;
  flex: none;
  overflow: hidden;
}

.workspace-main {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.list-toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 16px;
  border-bottom: 1px solid var(--ot-border-soft);
  flex: none;
}

.w-status {
  width: 130px;
}

.list-body {
  flex: 1;
  min-height: 0;
}

.list-foot {
  flex: none;
  display: flex;
  justify-content: flex-end;
  padding: 8px 16px;
}

:deep(.el-table__row) {
  cursor: pointer;
}

.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 6px;
  padding: 32px 0;
  color: var(--ot-text-faint);
}

.empty-state__title {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  color: var(--ot-text-muted);
}

.empty-state__hint {
  margin: 0;
  font-size: 12px;
}

.w-full {
  width: 100%;
}

.option-meta {
  float: right;
  font-size: 11.5px;
  color: var(--ot-text-faint);
}

.form-hint {
  margin: 6px 0 0;
  font-size: 12px;
  color: var(--ot-text-faint);
}
</style>
