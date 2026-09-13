<script setup>
import { computed, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useAppStore } from '../store'

const store = useAppStore()

const projectDialog = reactive({ visible: false, name: '', busy: false })
const checkDialog = reactive({ visible: false, name: '', busy: false })
const keyword = ref('')

/**
 * 默认展开的项目。
 *
 * el-tree 的 default-expanded-keys 只在「键集合变化」时生效，
 * 所以这里维护一个只增不减的列表：
 *  - 数据首次到达时把全部项目展开（子文件夹直接可见，符合资源管理器习惯）
 *  - 之后用户手动收起的状态不会被覆盖
 *  - 新增项目会被自动展开
 */
const expandedKeys = ref([])
const knownProjectNames = new Set()

/**
 * 树的重新挂载计数。
 *
 * el-tree 的 default-expanded-keys 是「初始化」语义：给它空数组**不会**收起已展开的节点。
 * 所以「全部收起 / 全部展开」通过改 key 触发一次重挂载来生效 ——
 * 只用公开 API，不去调组件内部的 store.setExpandedKeys()。
 */
const treeKey = ref(0)

watch(
  () => store.projects.map((p) => p.name).join('\u0000'),
  (joined) => {
    const names = joined ? joined.split('\u0000') : []
    const fresh = names.filter((n) => n && !knownProjectNames.has(n))
    if (!fresh.length) return

    fresh.forEach((n) => knownProjectNames.add(n))
    expandedKeys.value = [...expandedKeys.value, ...fresh]
    // 有新项目才重挂载：否则用户手动收起的节点会被反复展开
    treeKey.value += 1
  },
  { immediate: true },
)

const filteredProjects = computed(() => {
  const kw = keyword.value.trim().toLowerCase()
  if (!kw) return store.projects
  return store.projects.filter((p) => p.name.toLowerCase().includes(kw))
})

async function openProjectDialog() {
  projectDialog.name = ''
  projectDialog.visible = true
}

async function submitProject() {
  const name = projectDialog.name.trim()
  if (!name) {
    ElMessage.warning('请输入项目编码')
    return
  }
  // 与后端一致的编码规则，提前拦掉明显错误
  if (!/^[A-Za-z0-9_-]+$/.test(name)) {
    ElMessage.error('项目编码只允许字母、数字、下划线、短横线')
    return
  }

  projectDialog.busy = true
  try {
    await store.createProject(name)
    ElMessage.success(`项目已创建：${name}`)
    projectDialog.visible = false
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '创建失败')
  } finally {
    projectDialog.busy = false
  }
}

function openCheckDialog() {
  if (!store.currentProject) {
    ElMessage.warning('请先选择项目')
    return
  }
  checkDialog.name = ''
  checkDialog.visible = true
}

async function submitCheck() {
  const name = checkDialog.name.trim()
  if (!name) {
    ElMessage.warning('请输入检项编码')
    return
  }
  if (!/^[A-Za-z0-9_-]+$/.test(name)) {
    ElMessage.error('检项编码只允许字母、数字、下划线、短横线')
    return
  }

  checkDialog.busy = true
  try {
    await store.createCheck(name)
    ElMessage.success(`检项已创建：${name}`)
    checkDialog.visible = false
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '创建失败')
  } finally {
    checkDialog.busy = false
  }
}

/**
 * 点击树节点。
 * 第 1 层是项目（定位到「该项目全部检项」），第 2 层是检项（定位到具体检项）。
 */
async function onNodeClick(data, node) {
  if (node.level === 1) {
    await store.pick(data.name, '')
    return
  }

  await store.pick(data.projectName, data.name)
}

async function removeProject(project) {
  try {
    await ElMessageBox.confirm(
      `确认删除项目 ${project.name}？目录非空时后端会拒绝删除。`,
      '二次确认',
      { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' },
    )
  } catch {
    return
  }

  try {
    await store.removeProject(project.name)
    ElMessage.success('项目已删除')
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '删除失败')
  }
}

async function removeCheck(check) {
  try {
    await ElMessageBox.confirm(`确认删除检项 ${check.name}？`, '二次确认', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消',
    })
  } catch {
    return
  }

  try {
    await store.pick(check.projectName, check.name)
    await store.removeCheck(check.name)
    ElMessage.success('检项已删除')
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '删除失败')
  }
}

function collapseAll() {
  expandedKeys.value = []
  treeKey.value += 1
}

function expandAll() {
  expandedKeys.value = store.projects.map((p) => p.name)
  treeKey.value += 1
}
</script>

<template>
  <div>
    <div class="aside-actions">
      <el-button type="primary" size="small" @click="openProjectDialog">
        <el-icon><FolderAdd /></el-icon>&nbsp;新建项目
      </el-button>
      <el-button size="small" @click="openCheckDialog">
        <el-icon><Plus /></el-icon>&nbsp;新建检项
      </el-button>
    </div>

    <el-input v-model="keyword" size="small" placeholder="筛选项目" clearable style="margin-bottom: 10px">
      <template #prefix>
        <el-icon><Search /></el-icon>
      </template>
    </el-input>

    <div v-if="!filteredProjects.length" class="empty-tip">暂无项目</div>

    <template v-else>
      <div class="tree-toolbar">
        <el-button link size="small" @click="expandAll">全部展开</el-button>
        <el-button link size="small" @click="collapseAll">全部收起</el-button>
        <span class="tree-count">共 {{ filteredProjects.length }} 个项目</span>
      </div>

      <!--
        资源管理器式目录树：只呈现**文件夹**（项目 / 检项），不显示文件。
        文件在右侧列表里操作，避免左右两处出现同一批文件造成混淆。
      -->
      <el-tree
        :key="treeKey"
        :data="filteredProjects"
        node-key="name"
        :props="{ label: 'name', children: 'checks' }"
        :default-expanded-keys="expandedKeys"
        highlight-current
        :current-node-key="store.currentCheck || store.currentProject"
        @node-click="onNodeClick"
      >
        <template #default="{ node, data }">
          <span class="tree-node">
            <span class="tree-label" :class="{ 'is-check': node.level === 2 }" :title="data.name">
              <el-icon class="folder-icon"><Folder /></el-icon>
              {{ data.name }}
            </span>
            <span class="tree-actions">
              <el-button v-if="node.level === 1" link size="small" @click.stop="removeProject(data)">
                删除
              </el-button>
              <el-button v-else link size="small" @click.stop="removeCheck(data)">删除</el-button>
            </span>
          </span>
        </template>
      </el-tree>
    </template>

    <el-dialog v-model="projectDialog.visible" title="新建项目" width="420px">
      <el-form label-width="90px" @submit.prevent>
        <el-form-item label="项目编码">
          <el-input
            v-model="projectDialog.name"
            placeholder="如 QLS2409"
            maxlength="64"
            @keyup.enter="submitProject"
          />
        </el-form-item>
        <div style="color: #909399; font-size: 12px; padding-left: 90px">
          仅允许字母、数字、下划线、短横线；将同时创建模板库、数据目录与附件目录。
        </div>
      </el-form>
      <template #footer>
        <el-button @click="projectDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="projectDialog.busy" @click="submitProject">创建</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="checkDialog.visible" title="新建检项" width="420px">
      <el-form label-width="90px" @submit.prevent>
        <el-form-item label="所属项目">
          <el-input :model-value="store.currentProject" disabled />
        </el-form-item>
        <el-form-item label="检项编码">
          <el-input
            v-model="checkDialog.name"
            placeholder="如 SEC"
            maxlength="64"
            @keyup.enter="submitCheck"
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="checkDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="checkDialog.busy" @click="submitCheck">创建</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.tree-toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 6px;
  font-size: 12px;
}

.tree-count {
  margin-left: auto;
  color: #909399;
}

.tree-node {
  display: flex;
  align-items: center;
  width: 100%;
  padding-right: 4px;
}

.tree-label {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  display: flex;
  align-items: center;
  gap: 4px;
}

/* 两级都是文件夹：检项用不同色调区分，但**不用文档图标**，
   否则看上去像文件，与「只显示文件夹」的预期冲突。 */
.tree-label.is-check {
  color: #606266;
}

.tree-label.is-check .folder-icon {
  color: #a0a4ab;
}

.folder-icon {
  color: #e6a23c;
}

.tree-actions {
  opacity: 0;
  transition: opacity 0.15s;
}

.el-tree-node__content:hover .tree-actions {
  opacity: 1;
}
</style>
