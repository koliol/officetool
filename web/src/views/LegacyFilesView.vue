<script setup>
import { onMounted, reactive, ref } from 'vue'
import { ElMessage } from '../ui/message'
import { templateApi, documentApi } from '../api'
import { useAppStore } from '../store'
import { formatSize, formatTime } from '../ui/format'

/**
 * 历史文件（只读）。
 *
 * 产品转向 ELN 后，"上传模板 / 由模板生成文档"不再是主流程，
 * 但共享盘上已有的文件与索引**原样保留**：过往的检验记录可能还要翻、
 * 要复制路径给别人。这一页只提供查看，不再提供上传与生成。
 */
const store = useAppStore()

const tab = ref('templates')
const loading = ref(false)

const filters = reactive({ page: 1, pageSize: 20 })
const rows = ref([])
const total = ref(0)

const title = ref('')

async function load() {
  loading.value = true
  try {
    const params = {
      project: store.currentProject || undefined,
      check: store.currentCheck || undefined,
      page: filters.page,
      pageSize: filters.pageSize,
    }

    const result = tab.value === 'templates'
      ? await templateApi.list(params)
      : await documentApi.list(params)

    rows.value = result.items
    total.value = result.total
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '加载失败')
  } finally {
    loading.value = false
  }
}

async function copyPath(row) {
  try {
    await navigator.clipboard.writeText(row.accessPath)
    ElMessage.success('共享路径已复制')
  } catch {
    ElMessage.warning('浏览器拒绝剪贴板访问，请手动复制')
  }
}

function switchTab(name) {
  tab.value = name
  filters.page = 1
  load()
}

onMounted(load)
</script>

<template>
  <div class="page legacy-page">
    <div class="page-head">
      <div>
        <h1 class="page-head__title">历史文件</h1>
        <p class="page-head__sub">
          ELN 上线前的模板与文档，只读保留；新记录请在「记录中心」创建
        </p>
      </div>
    </div>

    <div class="ot-panel legacy-panel">
      <el-tabs v-model="tab" class="legacy-tabs" @tab-change="switchTab">
        <el-tab-pane label="历史模板" name="templates" />
        <el-tab-pane label="历史文档" name="documents" />
      </el-tabs>

      <div class="legacy-toolbar">
        <span class="ot-faint">共 {{ total }} 个文件</span>
      </div>

      <div v-loading="loading" class="legacy-body">
        <el-table :data="rows" border size="small" height="100%">
          <el-table-column prop="fileName" label="文件名" min-width="220" show-overflow-tooltip />
          <el-table-column prop="project" label="项目" width="110" />
          <el-table-column prop="check" label="检项" width="90" />
          <el-table-column prop="size" label="大小" width="90" align="right">
            <template #default="{ row }">{{ formatSize(row.size) }}</template>
          </el-table-column>
          <el-table-column :label="tab === 'templates' ? '修改时间' : '创建时间'" width="150">
            <template #default="{ row }">
              {{ formatTime(tab === 'templates' ? row.modifiedAt : row.createdAt) }}
            </template>
          </el-table-column>

          <el-table-column label="操作" width="180" fixed="right">
            <template #default="{ row }">
              <el-button link type="primary" size="small" @click="copyPath(row)">复制路径</el-button>
              <a class="el-button el-button--primary is-link" :href="`officetool://open?path=${encodeURIComponent(row.accessPath)}`">
                打开
              </a>
            </template>
          </el-table-column>

          <template #empty>
            <p class="empty-tip">该范围下没有历史文件</p>
          </template>
        </el-table>
      </div>

      <div class="legacy-foot">
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
    </div>
  </div>
</template>

<style scoped>
.legacy-page {
  height: 100%;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.legacy-panel {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.legacy-tabs {
  flex: none;
  padding: 0 16px;
}

.legacy-toolbar {
  padding: 0 16px 8px;
  flex: none;
}

.legacy-body {
  flex: 1;
  min-height: 0;
  padding: 0 16px;
}

.legacy-foot {
  flex: none;
  display: flex;
  justify-content: flex-end;
  padding: 8px 16px;
}
</style>
