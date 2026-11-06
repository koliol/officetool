<script setup>
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { navigate } from '../router'
import { elnApi } from '../api'
import { useAuthStore } from '../auth'
import { formatTime } from '../ui/format'
import FormRenderer from '../components/eln/FormRenderer.vue'

/**
 * 模板库。
 *
 * 模板是**全公司共享的标准**：任何项目/检项新建记录都从这里选。
 * 页面角色分两层 —— 大家都能浏览和预览；「规则管理」级权限的人才能建/改/停。
 */
const auth = useAuthStore()

const rows = ref([])
const loading = ref(false)
const keyword = ref('')

const canMaintain = computed(
  () => !auth.authEnabled || auth.isSystemAdmin || auth.canAnyRuleManage,
)

const preview = reactive({ visible: false, template: null })

async function load() {
  loading.value = true
  try {
    rows.value = await elnApi.templates.list({ keyword: keyword.value || undefined, includeDisabled: canMaintain.value })
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '模板加载失败')
  } finally {
    loading.value = false
  }
}

async function openPreview(row) {
  try {
    preview.template = await elnApi.templates.get(row.id)
    preview.visible = true
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '模板加载失败')
  }
}

function openEditor(row) {
  navigate('/template-editor', row ? { query: { id: row.id } } : {})
}

async function toggleEnabled(row) {
  try {
    await elnApi.templates.setEnabled(row.id, !row.isEnabled)
    ElMessage.success(row.isEnabled ? '已停用' : '已启用')
    await load()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '操作失败')
  }
}

async function copyTemplate(row) {
  let code = ''
  try {
    const result = await ElMessageBox.prompt(
      '新模板的编码（大写字母开头，字母/数字/下划线）：',
      `复制「${row.name}」`,
      {
        inputValue: `${row.code}_COPY`,
        confirmButtonText: '复制',
        cancelButtonText: '取消',
        inputValidator: (v) => (v && v.trim() ? true : '编码不能为空'),
      },
    )
    code = result.value.trim()
  } catch {
    return
  }

  try {
    const created = await elnApi.templates.copy(row.id, { code, name: `${row.name}（副本）` })
    ElMessage.success(`已复制为：${created.code}`)
    navigate('/template-editor', { query: { id: created.id } })
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '复制失败')
  }
}

async function removeTemplate(row) {
  try {
    await ElMessageBox.confirm(
      `确认删除模板「${row.name}」？被记录使用过的模板无法删除（请改为停用）。`,
      '删除模板',
      { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' },
    )
  } catch {
    return
  }

  try {
    await elnApi.templates.remove(row.id)
    ElMessage.success('已删除')
    await load()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '删除失败')
  }
}

onMounted(load)
</script>

<template>
  <div class="page templates-page">
    <div class="page-head">
      <div>
        <h1 class="page-head__title">模板库</h1>
        <p class="page-head__sub">记录格式在这里定义；新建记录时从这里选一份</p>
      </div>

      <div class="page-head__actions">
        <el-input
          v-model="keyword"
          placeholder="搜索名称 / 编码 / 描述"
          clearable
          class="keyword"
          @keyup.enter="load"
          @clear="load"
        >
          <template #prefix>
            <el-icon><Search /></el-icon>
          </template>
        </el-input>

        <el-button v-if="canMaintain" type="primary" @click="openEditor(null)">
          <el-icon><Plus /></el-icon>&nbsp;新建模板
        </el-button>
      </div>
    </div>

    <div v-loading="loading" class="ot-panel templates-panel">
      <el-table :data="rows" size="default" height="100%">
        <el-table-column prop="code" label="编码" width="230">
          <template #default="{ row }">
            <span class="ot-mono">{{ row.code }}</span>
            <el-tag v-if="row.isBuiltIn" size="small" effect="plain" class="tag-gap">内置</el-tag>
          </template>
        </el-table-column>

        <el-table-column prop="name" label="名称" min-width="200" show-overflow-tooltip />

        <el-table-column prop="category" label="分类" width="110">
          <template #default="{ row }">{{ row.category || '未分类' }}</template>
        </el-table-column>

        <el-table-column prop="version" label="版本" width="70" align="center">
          <template #default="{ row }">v{{ row.version }}</template>
        </el-table-column>

        <el-table-column prop="fieldCount" label="字段" width="64" align="center" />
        <el-table-column prop="recordCount" label="已有记录" width="90" align="center" />

        <el-table-column prop="isEnabled" label="状态" width="84" align="center">
          <template #default="{ row }">
            <el-tag :type="row.isEnabled ? 'success' : 'info'" size="small" effect="plain">
              {{ row.isEnabled ? '启用' : '停用' }}
            </el-tag>
          </template>
        </el-table-column>

        <el-table-column prop="updatedAt" label="最近修改" width="150">
          <template #default="{ row }">
            {{ formatTime(row.updatedAt) }}
            <span class="ot-faint">（{{ row.updatedBy || '匿名' }}）</span>
          </template>
        </el-table-column>

        <el-table-column label="操作" :width="canMaintain ? 250 : 90" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" size="small" @click="openPreview(row)">预览</el-button>

            <template v-if="canMaintain">
              <el-button link type="primary" size="small" @click="openEditor(row)">编辑</el-button>
              <el-button link size="small" @click="copyTemplate(row)">复制</el-button>
              <el-button link size="small" @click="toggleEnabled(row)">
                {{ row.isEnabled ? '停用' : '启用' }}
              </el-button>
              <el-button
                v-if="!row.isBuiltIn"
                link
                type="danger"
                size="small"
                @click="removeTemplate(row)"
              >
                删除
              </el-button>
            </template>
          </template>
        </el-table-column>

        <template #empty>
          <div class="empty-state">
            <el-icon :size="26"><Files /></el-icon>
            <p class="empty-state__title">还没有模板</p>
            <p class="empty-state__hint" v-if="canMaintain">点右上角「新建模板」创建第一份记录格式</p>
          </div>
        </template>
      </el-table>
    </div>

    <el-dialog v-model="preview.visible" :title="`预览：${preview.template?.name ?? ''}`" width="760px">
      <FormRenderer
        v-if="preview.template"
        :schema="preview.template.schema"
        :model-value="{}"
        disabled
      />
      <template #footer>
        <el-button @click="preview.visible = false">关闭</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.templates-page {
  height: 100%;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.keyword {
  width: 260px;
}

.templates-panel {
  flex: 1;
  min-height: 0;
}

.tag-gap {
  margin-left: 6px;
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
</style>
