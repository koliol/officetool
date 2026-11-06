<script setup>
import { computed, reactive, ref } from 'vue'
import { ElMessageBox } from 'element-plus'
import { ElMessage } from '../ui/message'
import { useAppStore } from '../store'
import { useAuthStore, LEVELS } from '../auth'

/**
 * 左侧目录：项目（一级）→ 检项（二级）。
 *
 * ── 为什么不用 el-tree ────────────────────────────────────────────────
 * 之前用 el-tree 时，每个节点的操作只有一行悬停才出现的「删除」文字，
 * 新建入口又在顶部，用户看不出「这个项目下能做什么、我现在在哪一层」。
 * 这里改成自绘的两级列表：
 *   · 每个节点右侧固定一个「⋯」菜单，新建检项 / 删除都在**该节点自己的**菜单里，
 *     想对哪个项目动手就在哪一行操作，不用先选中再去顶部找；
 *   · 选中态用左侧 4px 主色竖条 + 底色，项目与检项分别有不同的图标与缩进；
 *   · 顺带修掉 el-tree `node-key="name"` 的隐患：跨项目同名检项会互相串选中态，
 *     现在改为「项目名 + 检项名」联合判定。
 *
 * 权限门与后端 ProjectCatalogService 一一对应：
 *   · 新建项目 —— 只有系统管理员（新项目还没有检项，无从按项目授权）
 *   · 新建 / 删除检项、删除项目 —— 需要该项目下**所有**检项都有 Manage
 * 前端只知道可见的检项，所以这里是保守判断，真正的拦截在服务端。
 */
const store = useAppStore()
const auth = useAuthStore()

const canCreateProject = computed(() => !auth.authEnabled || auth.isSystemAdmin)

const canManageProject = (projectId) => auth.canManageProjectStructure(projectId)
const canDeleteCheck = (check) => auth.levelOfCheck(check.id) >= LEVELS.Manage

const currentProjectId = computed(
  () => store.projects.find((p) => p.name === store.currentProject)?.id,
)

const canCreateCheck = computed(
  () => Boolean(currentProjectId.value) && canManageProject(currentProjectId.value),
)

const projectDialog = reactive({ visible: false, name: '', busy: false })
const checkDialog = reactive({ visible: false, name: '', busy: false })
const keyword = ref('')

/**
 * 折叠状态用「已折叠项目名」记录，而不是「已展开」：
 * 默认全部展开（新项目自动可见），用户收起过的那几个才需要记。
 */
const collapsed = ref(new Set())

function isExpanded(name) {
  return !collapsed.value.has(name)
}

function toggle(name) {
  const next = new Set(collapsed.value)
  if (next.has(name)) {
    next.delete(name)
  } else {
    next.add(name)
  }
  collapsed.value = next
}

function expandAll() {
  collapsed.value = new Set()
}

function collapseAll() {
  collapsed.value = new Set(store.projects.map((p) => p.name))
}

const filteredProjects = computed(() => {
  const kw = keyword.value.trim().toLowerCase()
  if (!kw) return store.projects
  return store.projects.filter((p) => p.name.toLowerCase().includes(kw))
})

const totalChecks = computed(() =>
  store.projects.reduce((sum, p) => sum + (p.checks?.length || 0), 0),
)

const isProjectActive = (project) =>
  store.currentProject === project.name && !store.currentCheck

const isCheckActive = (project, check) =>
  store.currentProject === project.name && store.currentCheck === check.name

async function pickProject(project) {
  await store.pick(project.name, '')
}

async function pickCheck(project, check) {
  await store.pick(project.name, check.name)
}

function openProjectDialog() {
  if (!canCreateProject.value) {
    ElMessage.warning('新建项目需要系统管理员权限（新项目尚未授权，无从按项目判权）')
    return
  }

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

async function openCheckDialog(projectName = '') {
  // 从某个项目的「⋯ → 新建检项」进来时，先把范围切到该项目，
  // 否则新建出来的检项会挂到当前选中的另一个项目下（用户会以为点错了）。
  if (projectName && projectName !== store.currentProject) {
    await store.pick(projectName, '')
  }

  if (!store.currentProject) {
    ElMessage.warning('请先选择一个项目')
    return
  }

  if (!canCreateCheck.value) {
    ElMessage.warning(
      `无权在「${store.currentProject}」下新建检项（需对该项目下所有检项具备管理权限）`,
    )
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

async function removeProject(project) {
  if (!canManageProject(project.id)) {
    ElMessage.warning(`无权删除「${project.name}」（需对该项目下所有检项具备管理权限）`)
    return
  }

  try {
    await ElMessageBox.confirm(
      `确认删除项目「${project.name}」？目录非空时后端会拒绝删除。`,
      '删除项目',
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

async function removeCheck(project, check) {
  if (!canDeleteCheck(check)) {
    ElMessage.warning(`无权删除检项「${check.name}」（需具备管理权限）`)
    return
  }

  try {
    await ElMessageBox.confirm(
      `确认删除检项「${check.name}」？目录非空时后端会拒绝删除。`,
      '删除检项',
      { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' },
    )
  } catch {
    return
  }

  try {
    await store.pick(project.name, check.name)
    await store.removeCheck(check.name)
    ElMessage.success('检项已删除')
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '删除失败')
  }
}

/** 节点菜单命令：同一个入口里放"能对这个节点做的事"，避免操作与目标脱节 */
function onProjectCommand(command, project) {
  if (command === 'new-check') {
    openCheckDialog(project.name)
  } else if (command === 'remove') {
    removeProject(project)
  }
}

function onCheckCommand(command, project, check) {
  if (command === 'remove') {
    removeCheck(project, check)
  }
}
</script>

<template>
  <div class="tree-panel">
    <div class="ot-panel__head tree-head">
      <h2 class="ot-panel__title">
        目录
        <span class="ot-count">{{ store.projects.length }}</span>
      </h2>

      <div class="tree-head__actions">
        <el-tooltip
          v-if="!canCreateProject"
          content="仅系统管理员可新建项目"
          placement="bottom"
        >
          <span>
            <el-button size="small" type="primary" disabled>
              <el-icon><FolderAdd /></el-icon>&nbsp;新建
            </el-button>
          </span>
        </el-tooltip>

        <el-dropdown v-else trigger="click" @command="(cmd) => cmd === 'project' ? openProjectDialog() : openCheckDialog()">
          <el-button size="small" type="primary">
            <el-icon><Plus /></el-icon>&nbsp;新建
            <el-icon class="el-icon--right"><ArrowDown /></el-icon>
          </el-button>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item command="project">新建项目</el-dropdown-item>
              <el-dropdown-item command="check" :disabled="!canCreateCheck">
                新建检项{{ store.currentProject ? `（${store.currentProject}）` : '' }}
              </el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
      </div>
    </div>

    <div class="tree-filter">
      <el-input v-model="keyword" size="small" placeholder="筛选项目编码" clearable>
        <template #prefix>
          <el-icon><Search /></el-icon>
        </template>
      </el-input>

      <div class="tree-tools">
        <button class="link-btn" type="button" @click="expandAll">展开</button>
        <span class="dot">·</span>
        <button class="link-btn" type="button" @click="collapseAll">收起</button>
        <span class="tree-total">{{ totalChecks }} 个检项</span>
      </div>
    </div>

    <div class="tree-scroll">
      <div v-if="!filteredProjects.length" class="tree-empty">
        <el-icon :size="26"><FolderOpened /></el-icon>
        <p v-if="keyword">没有匹配「{{ keyword }}」的项目</p>
        <p v-else>还没有任何项目</p>
        <el-button v-if="!keyword && canCreateProject" type="primary" size="small" @click="openProjectDialog">
          创建第一个项目
        </el-button>
      </div>

      <div v-for="project in filteredProjects" :key="project.id" class="project-block">
        <div
          class="node is-project"
          :class="{ 'is-active': isProjectActive(project) }"
          @click="pickProject(project)"
        >
          <button
            class="caret"
            type="button"
            :title="isExpanded(project.name) ? '收起' : '展开'"
            @click.stop="toggle(project.name)"
          >
            <el-icon :class="{ 'is-open': isExpanded(project.name) }"><ArrowRight /></el-icon>
          </button>

          <el-icon class="node-icon"><FolderFilled /></el-icon>
          <span class="node-name" :title="project.name">{{ project.name }}</span>
          <span class="node-count">{{ project.checks?.length || 0 }}</span>

          <el-dropdown
            trigger="click"
            placement="bottom-end"
            @command="(cmd) => onProjectCommand(cmd, project)"
          >
            <button class="node-menu" type="button" title="该项目可执行的操作" @click.stop>
              <el-icon><MoreFilled /></el-icon>
            </button>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item command="new-check" :disabled="!canManageProject(project.id)">
                  新建检项
                </el-dropdown-item>
                <el-dropdown-item command="remove" divided :disabled="!canManageProject(project.id)">
                  删除项目
                </el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </div>

        <div v-show="isExpanded(project.name)" class="check-list">
          <div
            v-for="check in project.checks"
            :key="`${project.name}/${check.name}`"
            class="node is-check"
            :class="{ 'is-active': isCheckActive(project, check) }"
            @click="pickCheck(project, check)"
          >
            <span class="branch" aria-hidden="true"></span>
            <span class="check-dot" aria-hidden="true"></span>
            <span class="node-name" :title="check.name">{{ check.name }}</span>

            <el-dropdown
              trigger="click"
              placement="bottom-end"
              @command="(cmd) => onCheckCommand(cmd, project, check)"
            >
              <button class="node-menu" type="button" title="该检项可执行的操作" @click.stop>
                <el-icon><MoreFilled /></el-icon>
              </button>
              <template #dropdown>
                <el-dropdown-menu>
                  <el-dropdown-item command="remove" :disabled="!canDeleteCheck(check)">
                    删除检项
                  </el-dropdown-item>
                </el-dropdown-menu>
              </template>
            </el-dropdown>
          </div>

          <button
            v-if="!project.checks?.length"
            class="check-empty"
            type="button"
            :disabled="!canManageProject(project.id)"
            @click="openCheckDialog(project.name)"
          >
            尚无检项，点击新建第一个
          </button>
        </div>
      </div>
    </div>

    <div class="tree-foot">
      <el-icon><InfoFilled /></el-icon>
      <span>点项目看全部检项，点检项只看单个目录</span>
    </div>

    <el-dialog v-model="projectDialog.visible" title="新建项目" width="440px">
      <el-form label-width="90px" @submit.prevent>
        <el-form-item label="项目编码">
          <el-input
            v-model="projectDialog.name"
            placeholder="如 QLS2409"
            maxlength="64"
            @keyup.enter="submitProject"
          />
        </el-form-item>
        <p class="dialog-hint">
          仅允许字母、数字、下划线、短横线。将同时创建模板库、数据目录、附件目录与提取规则目录。
        </p>
      </el-form>
      <template #footer>
        <el-button @click="projectDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="projectDialog.busy" @click="submitProject">创建</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="checkDialog.visible" title="新建检项" width="440px">
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
        <p class="dialog-hint">检项是项目下的一级目录，模板、文档、附件、规则都按它分文件夹存放。</p>
      </el-form>
      <template #footer>
        <el-button @click="checkDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="checkDialog.busy" @click="submitCheck">创建</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.tree-panel {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
}

.tree-head {
  padding: 12px 14px;
}

.tree-head__actions {
  margin-left: auto;
}

.tree-filter {
  padding: 10px 14px 6px;
  flex: none;
}

.tree-tools {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 8px;
  font-size: 12px;
  color: var(--ot-text-faint);
}

.link-btn {
  border: none;
  background: none;
  padding: 0;
  font-size: 12px;
  font-weight: 600;
  color: var(--ot-primary);
  cursor: pointer;
}

.link-btn:hover {
  color: var(--ot-primary-dark);
  text-decoration: underline;
}

.tree-tools .dot {
  color: var(--ot-border);
}

.tree-total {
  margin-left: auto;
  font-variant-numeric: tabular-nums;
}

.tree-scroll {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 4px 8px 8px;
}

.tree-empty {
  padding: 40px 12px;
  text-align: center;
  color: var(--ot-text-faint);
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  font-size: 13px;
}

.tree-empty p {
  margin: 0;
}

/* ── 节点 ─────────────────────────────────────────────────────── */

.node {
  position: relative;
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 7px 8px;
  border-radius: 6px;
  cursor: pointer;
  user-select: none;
  border-left: 4px solid transparent;
  transition: background-color 0.12s, border-color 0.12s;
}

.node:hover {
  background: var(--ot-surface-2);
}

.node.is-active {
  background: var(--ot-primary-soft);
  border-left-color: var(--ot-primary);
}

.node.is-active .node-name {
  color: var(--ot-primary-dark);
  font-weight: 700;
}

.node.is-project {
  font-size: 13.5px;
  font-weight: 700;
  color: var(--ot-text);
}

.node.is-check {
  margin-left: 18px;
  padding-left: 10px;
  font-size: 13px;
  font-weight: 500;
  color: var(--ot-text-muted);
}

.caret {
  width: 18px;
  height: 18px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  padding: 0;
  border: none;
  background: none;
  color: var(--ot-text-muted);
  cursor: pointer;
  flex: none;
}

.caret:hover {
  color: var(--ot-primary);
}

.caret .el-icon {
  transition: transform 0.15s;
}

.caret .el-icon.is-open {
  transform: rotate(90deg);
}

.node-icon {
  color: #b45309;
  flex: none;
}

.node-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.node-count {
  flex: none;
  min-width: 20px;
  height: 18px;
  padding: 0 6px;
  border-radius: 9px;
  background: var(--ot-surface-3);
  color: var(--ot-text-muted);
  font-size: 11px;
  font-weight: 700;
  line-height: 18px;
  text-align: center;
  font-variant-numeric: tabular-nums;
}

.node.is-active .node-count {
  background: #fff;
  color: var(--ot-primary-dark);
}

/* 操作按钮常驻而不是悬停才出现：悬停显隐让「能对这里做什么」变成需要摸索的事 */
.node-menu {
  flex: none;
  width: 22px;
  height: 22px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  padding: 0;
  border: none;
  border-radius: 4px;
  background: transparent;
  color: var(--ot-text-faint);
  cursor: pointer;
}

.node-menu:hover {
  background: var(--ot-surface-3);
  color: var(--ot-text);
}

.check-list {
  padding-bottom: 4px;
}

/* 二级节点左侧的引导线：让"谁属于谁"不靠缩进猜 */
.branch {
  position: absolute;
  left: -10px;
  top: -6px;
  bottom: 50%;
  width: 10px;
  border-left: 1px solid var(--ot-border);
  border-bottom: 1px solid var(--ot-border);
  border-bottom-left-radius: 6px;
  pointer-events: none;
}

.check-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--ot-border);
  flex: none;
}

.node.is-check.is-active .check-dot {
  background: var(--ot-primary);
}

.check-empty {
  display: block;
  width: calc(100% - 18px);
  margin: 2px 0 6px 28px;
  padding: 8px 10px;
  border: 1px dashed var(--ot-border);
  border-radius: 6px;
  background: transparent;
  color: var(--ot-text-faint);
  font-size: 12px;
  text-align: left;
  cursor: pointer;
}

.check-empty:hover:not(:disabled) {
  border-color: var(--ot-primary);
  color: var(--ot-primary);
  background: var(--ot-primary-soft);
}

.check-empty:disabled {
  cursor: not-allowed;
  opacity: 0.7;
}

.tree-foot {
  flex: none;
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 14px;
  border-top: 1px solid var(--ot-border-soft);
  color: var(--ot-text-faint);
  font-size: 11.5px;
  background: var(--ot-surface-2);
}

.dialog-hint {
  margin: 0 0 0 90px;
  color: var(--ot-text-faint);
  font-size: 12px;
  line-height: 1.7;
}
</style>
