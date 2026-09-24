<script setup>
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { trashApi } from '../api'
import { useAuthStore, LEVELS } from '../auth'
import { useAppStore } from '../store'

/**
 * 回收站。
 *
 * 这块此前只有后端接口、没有界面 —— 删掉的文件进得去出不来，
 * 只能靠手工翻 `_trash` 目录，等于回收站只做了一半。
 *
 * 权限上按「管理」档位判定：恢复与彻底删除都是管理动作。
 * 逐行判定而不是整页判定：同一个人可能对 A 项目有管理权、对 B 项目只有只读权，
 * 整页放行会让他在 B 项目上看到能点、点了报 403 的按钮。
 */
const auth = useAuthStore()
const store = useAppStore()

const rows = ref([])
const total = ref(0)
const loading = ref(false)

const filters = reactive({ kind: '', project: '', page: 1, pageSize: 50 })

const KINDS = [
  { value: 'Template', label: '模板' },
  { value: 'Document', label: '文档' },
  { value: 'Attachment', label: '附件' },
  { value: 'Rule', label: '提取规则' },
]

function kindLabel(kind) {
  const found = KINDS.find((k) => k.value === kind)
  return found ? found.label : kind || '-'
}

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

/** 该条目对应的 项目/检项 上有没有管理权 */
function canManage(row) {
  return auth.levelOfScope(row.project, row.check) >= LEVELS.Manage
}

const manageableCount = computed(() => rows.value.filter(canManage).length)

async function load() {
  loading.value = true
  try {
    const data = await trashApi.list({
      kind: filters.kind || undefined,
      project: filters.project || undefined,
      page: filters.page,
      pageSize: filters.pageSize,
    })
    rows.value = data.items
    total.value = data.total
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '加载回收站失败')
    rows.value = []
    total.value = 0
  } finally {
    loading.value = false
  }
}

function onQuery() {
  filters.page = 1
  load()
}

function onReset() {
  filters.kind = ''
  filters.project = ''
  filters.page = 1
  load()
}

async function restore(row) {
  try {
    await ElMessageBox.confirm(
      `确认把 ${row.fileName} 恢复到原位置（${row.project} / ${row.check}）？若原位置已有同名文件，恢复会被拒绝。`,
      '恢复文件',
      { type: 'info', confirmButtonText: '恢复', cancelButtonText: '取消' },
    )
  } catch {
    return
  }

  try {
    await trashApi.restore(row.id)
    ElMessage.success(`已恢复：${row.fileName}`)
    store.bumpDataVersion()
    await load()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '恢复失败')
  }
}

async function purge(row) {
  try {
    await ElMessageBox.confirm(
      `确认彻底删除 ${row.fileName}？此操作不可撤销，文件将从磁盘上移除。`,
      '彻底删除',
      { type: 'warning', confirmButtonText: '彻底删除', cancelButtonText: '取消' },
    )
  } catch {
    return
  }

  try {
    await trashApi.purge(row.id)
    ElMessage.success('已彻底删除')
    await load()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '删除失败')
  }
}

onMounted(load)
</script>

<template>
  <div class="trash-view">
    <el-form class="search-bar" :inline="true" label-width="70px" @submit.prevent>
      <el-form-item label="类型">
        <el-select v-model="filters.kind" placeholder="全部" clearable style="width: 140px" @change="onQuery">
          <el-option v-for="k in KINDS" :key="k.value" :label="k.label" :value="k.value" />
        </el-select>
      </el-form-item>

      <el-form-item label="项目">
        <el-select v-model="filters.project" placeholder="全部" clearable style="width: 160px" @change="onQuery">
          <el-option v-for="p in store.projects" :key="p.name" :label="p.name" :value="p.name" />
        </el-select>
      </el-form-item>

      <el-form-item>
        <el-button type="primary" @click="onQuery">查询</el-button>
        <el-button @click="onReset">重置</el-button>
        <el-button @click="load">
          <el-icon><RefreshRight /></el-icon>&nbsp;刷新
        </el-button>
      </el-form-item>
    </el-form>

    <el-alert
      v-if="rows.length && manageableCount === 0"
      type="info"
      :closable="false"
      show-icon
      title="你只能查看回收站内容"
      description="恢复与彻底删除需要对该项目具备「管理」权限。"
      style="margin-bottom: 12px"
    />

    <el-table v-loading="loading" :data="rows" border stripe size="small">
      <el-table-column prop="fileName" label="文件名" min-width="200" show-overflow-tooltip />
      <el-table-column label="类型" width="96">
        <template #default="{ row }">{{ kindLabel(row.kind) }}</template>
      </el-table-column>
      <el-table-column prop="project" label="项目" width="120" show-overflow-tooltip />
      <el-table-column prop="check" label="检项" width="110" show-overflow-tooltip />
      <el-table-column label="大小" width="86">
        <template #default="{ row }">{{ formatSize(row.size) }}</template>
      </el-table-column>
      <el-table-column label="删除时间" width="136">
        <template #default="{ row }">{{ formatTime(row.deletedAt) }}</template>
      </el-table-column>

      <el-table-column label="操作" width="176" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" size="small" :disabled="!canManage(row)" @click="restore(row)">
            恢复
          </el-button>
          <el-button link type="danger" size="small" :disabled="!canManage(row)" @click="purge(row)">
            彻底删除
          </el-button>
        </template>
      </el-table-column>

      <template #empty>
        <span style="color: #909399">回收站是空的。</span>
      </template>
    </el-table>

    <div class="pager">
      <el-pagination
        v-model:current-page="filters.page"
        v-model:page-size="filters.pageSize"
        :total="total"
        :page-sizes="[50, 100, 200]"
        layout="total, sizes, prev, pager, next"
        @current-change="load"
        @size-change="onQuery"
      />
    </div>
  </div>
</template>

<style scoped>
.trash-view {
  padding: 12px 16px 16px;
  height: 100%;
  overflow: auto;
  box-sizing: border-box;
}

.pager {
  margin-top: 14px;
  display: flex;
  justify-content: flex-end;
}
</style>
