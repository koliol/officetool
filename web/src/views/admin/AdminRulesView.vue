<script setup>
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { elnApi, projectApi } from '../../api'
import { useAppStore } from '../../store'

/**
 * 提取规则与生效范围（管理员）。
 *
 * 规则是产品内置资产，这里只做两件事：启停、划范围。
 * 规则 JSON 允许编辑（导入新版本/微调正则），保存时服务端会校验并升版本。
 */
const store = useAppStore()

const rows = ref([])
const projects = ref([])
const loading = ref(false)

const editDialog = reactive({
  visible: false,
  rule: null,
  name: '',
  category: '',
  description: '',
  ruleJson: '',
  busy: false,
})

const scopeDialog = reactive({ visible: false, rule: null, selections: [], busy: false })

const canAdmin = computed(() => !store.config || true) // 页面入口已由导航按角色过滤，这里仅控制文案

async function load() {
  loading.value = true
  try {
    rows.value = await elnApi.adminRules.list()

    try {
      projects.value = await projectApi.tree()
    } catch {
      projects.value = []
    }
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '规则加载失败')
  } finally {
    loading.value = false
  }
}

function openEdit(row) {
  editDialog.rule = row
  editDialog.name = row.name
  editDialog.category = row.category
  editDialog.description = row.description
  editDialog.ruleJson = JSON.stringify(row.rule, null, 2)
  editDialog.visible = true
}

async function saveEdit() {
  let parsed
  try {
    parsed = JSON.parse(editDialog.ruleJson)
  } catch (error) {
    ElMessage.error(`规则不是合法 JSON：${error.message}`)
    return
  }

  editDialog.busy = true
  try {
    await elnApi.adminRules.update(editDialog.rule.id, {
      name: editDialog.name,
      category: editDialog.category,
      description: editDialog.description,
      rule: parsed,
    })
    ElMessage.success('已保存（内容变更会自动升版本）')
    editDialog.visible = false
    await load()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '保存失败')
  } finally {
    editDialog.busy = false
  }
}

async function toggleEnabled(row) {
  try {
    await elnApi.adminRules.setEnabled(row.id, !row.isEnabled)
    ElMessage.success(row.isEnabled ? '已停用' : '已启用')
    await load()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '操作失败')
  }
}

// ── 生效范围 ────────────────────────────────────────────────────────

function openScopes(row) {
  scopeDialog.rule = row

  // 现有范围转成 cascader 选中值：[projectId] 或 [projectId, checkId]
  scopeDialog.selections = row.scopes.map((s) =>
    s.checkId ? [s.projectId, s.checkId] : [s.projectId],
  )
  scopeDialog.visible = true
}

const cascaderProps = {
  multiple: true,
  checkStrictly: true, // 允许只选到"项目"级（整项目生效）
  emitPath: true,
}

const scopeOptions = computed(() =>
  projects.value.map((p) => ({
    value: p.id,
    label: p.name,
    children: (p.checks ?? []).map((c) => ({ value: c.id, label: c.name })),
  })),
)

async function saveScopes() {
  const scopes = scopeDialog.selections.map((path) => ({
    projectId: path[0],
    checkId: path.length > 1 ? path[path.length - 1] : null,
  }))

  scopeDialog.busy = true
  try {
    await elnApi.adminRules.replaceScopes(scopeDialog.rule.id, scopes)
    ElMessage.success(`已保存（${scopes.length} 处范围）`)
    scopeDialog.visible = false
    await load()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '保存失败')
  } finally {
    scopeDialog.busy = false
  }
}

function scopeText(row) {
  if (!row.scopes.length) {
    return '未设置（用户不可见）'
  }

  return row.scopes
    .map((s) => `${s.project}${s.check ? `/${s.check}` : '（整个项目）'}`)
    .join('、')
}

onMounted(load)
</script>

<template>
  <div class="page rules-page">
    <div class="page-head">
      <div>
        <h1 class="page-head__title">提取规则</h1>
        <p class="page-head__sub">
          规则由产品内置并随版本升级；在这里启用规则并设置生效范围，用户在范围内填写记录时即可选用
        </p>
      </div>
    </div>

    <div v-loading="loading" class="ot-panel rules-panel">
      <el-table :data="rows" size="default" height="100%">
        <el-table-column prop="code" label="编码" width="200">
          <template #default="{ row }">
            <span class="ot-mono">{{ row.code }}</span>
            <el-tag v-if="row.isBuiltIn" size="small" effect="plain" class="tag-gap">内置</el-tag>
          </template>
        </el-table-column>

        <el-table-column prop="name" label="名称" min-width="180" show-overflow-tooltip />
        <el-table-column prop="category" label="分类" width="100">
          <template #default="{ row }">{{ row.category || '未分类' }}</template>
        </el-table-column>
        <el-table-column prop="version" label="版本" width="70" align="center">
          <template #default="{ row }">v{{ row.version }}</template>
        </el-table-column>

        <el-table-column prop="isEnabled" label="状态" width="84" align="center">
          <template #default="{ row }">
            <el-tag :type="row.isEnabled ? 'success' : 'info'" size="small" effect="plain">
              {{ row.isEnabled ? '启用' : '停用' }}
            </el-tag>
          </template>
        </el-table-column>

        <el-table-column label="生效范围" min-width="260">
          <template #default="{ row }">
            <span :class="{ 'no-scope': !row.scopes.length }">{{ scopeText(row) }}</span>
          </template>
        </el-table-column>

        <el-table-column label="操作" width="250" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" size="small" @click="openScopes(row)">生效范围</el-button>
            <el-button link type="primary" size="small" @click="openEdit(row)">编辑规则</el-button>
            <el-button link size="small" @click="toggleEnabled(row)">
              {{ row.isEnabled ? '停用' : '启用' }}
            </el-button>
          </template>
        </el-table-column>

        <template #empty>
          <p class="empty-tip">还没有规则定义</p>
        </template>
      </el-table>
    </div>

    <el-dialog v-model="editDialog.visible" title="编辑规则定义" width="760px" top="6vh">
      <el-form label-width="70px" size="small" @submit.prevent>
        <el-form-item label="名称" required>
          <el-input v-model="editDialog.name" />
        </el-form-item>
        <el-form-item label="分类">
          <el-input v-model="editDialog.category" />
        </el-form-item>
        <el-form-item label="说明">
          <el-input v-model="editDialog.description" type="textarea" :rows="2" />
        </el-form-item>
        <el-form-item label="规则 JSON">
          <el-input
            v-model="editDialog.ruleJson"
            type="textarea"
            :rows="18"
            class="ot-mono rule-json"
            spellcheck="false"
          />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="editDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="editDialog.busy" @click="saveEdit">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="scopeDialog.visible" :title="`生效范围：${scopeDialog.rule?.name ?? ''}`" width="520px">
      <el-alert
        type="info"
        :closable="false"
        show-icon
        title="只选项目 = 对该项目下所有检项生效（含将来新建的）；选到检项 = 只对该检项生效。"
        style="margin-bottom: 12px"
      />

      <el-cascader
        v-model="scopeDialog.selections"
        :options="scopeOptions"
        :props="cascaderProps"
        placeholder="选择项目 / 检项"
        class="scope-cascader"
        clearable
      />

      <template #footer>
        <el-button @click="scopeDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="scopeDialog.busy" @click="saveScopes">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.rules-page {
  height: 100%;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.rules-panel {
  flex: 1;
  min-height: 0;
}

.tag-gap {
  margin-left: 6px;
}

.no-scope {
  color: var(--el-color-warning);
  font-weight: 600;
}

.rule-json :deep(.el-textarea__inner) {
  font-family: Consolas, Monaco, monospace;
  font-size: 12px;
  line-height: 1.5;
}

.scope-cascader {
  width: 100%;
}
</style>
