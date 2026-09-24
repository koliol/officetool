<script setup>
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { adminApi, projectApi } from '../../api'
import { LEVEL_OPTIONS, levelName, levelTagType } from '../../auth'

/**
 * 授权配置。这是整套权限系统的「操作台」——没有这一页，
 * 前面做的 ACL 表、判定、过滤都只是等着被人用的空转逻辑。
 *
 * 页面结构围绕一个真实问题组织：**「某个项目没有任何人能管理」**
 * 是这里最容易发生也最难发现的事故。所以自检结果放在最上方，
 * 而不是塞进一个需要主动点开的标签页。
 */
const groups = ref([])
const projects = ref([])
const entries = ref([])
const health = ref(null)
const loading = ref(false)

const filters = reactive({ groupId: '', projectId: '' })

const createDialog = reactive({
  visible: false,
  busy: false,
  groupId: '',
  projectId: '',
  checkId: '',
  level: 'Read',
})

/** 新增授权弹窗里的检项候选，随所选项目变化 */
const createDialogChecks = ref([])

const chosenProject = computed(() => projects.value.find((p) => p.id === createDialog.projectId))

const selectedGroupName = computed(() => {
  const g = groups.value.find((x) => x.id === createDialog.groupId)
  return g ? g.name : ''
})

const selectedProjectName = computed(() => (chosenProject.value ? chosenProject.value.name : ''))

const healthProblems = computed(() => {
  if (!health.value) {
    return []
  }

  const list = [...(health.value.warnings || [])]

  if (health.value.aclEntryCount === 0) {
    list.push('尚未配置任何授权条目：除超级管理员之外，所有人登录后看不到任何内容。')
  }

  return list
})

async function load() {
  loading.value = true
  try {
    const [groupList, projectList, aclList, healthResult] = await Promise.all([
      adminApi.groups.list(),
      projectApi.list(),
      adminApi.acl.list({
        groupId: filters.groupId || undefined,
        projectId: filters.projectId || undefined,
      }),
      adminApi.health(),
    ])

    groups.value = groupList
    projects.value = projectList
    entries.value = aclList
    health.value = healthResult
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '加载授权配置失败')
  } finally {
    loading.value = false
  }
}

function onProjectFilterChange() {
  load()
}

async function loadCreateChecks(projectId) {
  const project = projects.value.find((p) => p.id === projectId)
  if (!project) {
    createDialogChecks.value = []
    return
  }

  try {
    createDialogChecks.value = await projectApi.checks(project.name)
  } catch {
    createDialogChecks.value = []
  }
}

watch(
  () => createDialog.projectId,
  async (value) => {
    createDialog.checkId = ''
    createDialogChecks.value = []
    if (value) {
      await loadCreateChecks(value)
    }
  },
)

function openCreate() {
  createDialog.groupId = filters.groupId || ''
  createDialog.projectId = filters.projectId || ''
  createDialog.checkId = ''
  createDialog.level = 'Read'
  createDialogChecks.value = []
  createDialog.visible = true
}

async function submitCreate() {
  if (!createDialog.groupId || !createDialog.projectId) {
    ElMessage.warning('请选择用户组与项目')
    return
  }

  createDialog.busy = true
  try {
    await adminApi.acl.create({
      groupId: createDialog.groupId,
      projectId: createDialog.projectId,
      checkId: createDialog.checkId || null,
      level: createDialog.level,
    })

    createDialog.visible = false
    ElMessage.success(
      `已授权：${selectedGroupName.value} → ${selectedProjectName.value}${
        createDialog.checkId ? '' : '（整个项目）'
      }`,
    )
    await load()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '授权失败')
  } finally {
    createDialog.busy = false
  }
}

async function changeLevel(row, value) {
  try {
    await adminApi.acl.update(row.id, value)
    ElMessage.success('授权等级已更新，对方下次请求即生效')
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '更新失败')
  } finally {
    // 无论成败都重新拉一次：失败时把下拉还原成服务端的真实值，
    // 否则界面会停在一个没有生效的等级上，管理员会以为已经改好了。
    await load()
  }
}

async function removeEntry(row) {
  const where = row.checkName ? `${row.projectName} / ${row.checkName}` : `${row.projectName}（整个项目）`

  try {
    await ElMessageBox.confirm(
      `确认撤销「${row.groupName}」对 ${where} 的授权？该组成员会立即失去对应权限。`,
      '撤销授权',
      { type: 'warning', confirmButtonText: '撤销', cancelButtonText: '取消' },
    )
  } catch {
    return
  }

  try {
    await adminApi.acl.remove(row.id)
    ElMessage.success('授权已撤销')
    await load()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '撤销失败')
  }
}

function resetFilters() {
  filters.groupId = ''
  filters.projectId = ''
  load()
}

onMounted(load)
</script>

<template>
  <div class="admin-view">
    <!-- 自检：把「最容易发生也最难发现」的问题摆在最上方 -->
    <template v-if="health">
      <el-alert
        v-if="healthProblems.length"
        type="error"
        :closable="false"
        show-icon
        title="发现需要处理的权限问题"
        style="margin-bottom: 12px"
      >
        <template #default>
          <ul class="problem-list">
            <li v-for="(item, index) in healthProblems" :key="index">{{ item }}</li>
          </ul>
        </template>
      </el-alert>

      <el-alert
        v-else
        type="success"
        :closable="false"
        show-icon
        title="权限配置自检通过"
        :description="`超级管理员 ${health.adminCount} 人，用户组 ${health.groupCount} 个，授权条目 ${health.aclEntryCount} 条，所有项目都有可管理的人。`"
        style="margin-bottom: 12px"
      />

      <el-table
        v-if="health.unmanagedProjects.length"
        :data="health.unmanagedProjects"
        border
        size="small"
        style="margin-bottom: 12px"
      >
        <el-table-column prop="projectName" label="没有可管理者的项目" min-width="160" />
        <el-table-column prop="checkCount" label="检项数" width="90" />
        <el-table-column prop="grantedCheckCount" label="已授权检项数" width="120" />
        <el-table-column label="说明" min-width="240">
          <template #default="{ row }">
            该项目下有人具备「管理」及以上权限的检项数为 0。删除项目、增删检项这类操作会无人可做。
          </template>
        </el-table-column>
      </el-table>
    </template>

    <el-form class="search-bar" :inline="true" label-width="70px" @submit.prevent>
      <el-form-item label="用户组">
        <el-select
          v-model="filters.groupId"
          placeholder="全部"
          clearable
          style="width: 170px"
          @change="load"
        >
          <el-option v-for="g in groups" :key="g.id" :label="g.name" :value="g.id" />
        </el-select>
      </el-form-item>

      <el-form-item label="项目">
        <el-select
          v-model="filters.projectId"
          placeholder="全部"
          clearable
          style="width: 170px"
          @change="onProjectFilterChange"
        >
          <el-option v-for="p in projects" :key="p.id" :label="p.name" :value="p.id" />
        </el-select>
      </el-form-item>

      <el-form-item>
        <el-button type="primary" @click="load">查询</el-button>
        <el-button @click="resetFilters">重置</el-button>
      </el-form-item>
    </el-form>

    <div class="table-toolbar">
      <span style="color: #606266">
        <strong>授权条目</strong>
        <span class="count">共 {{ entries.length }} 条</span>
      </span>

      <span class="spacer" />

      <el-button type="primary" :disabled="!groups.length || !projects.length" @click="openCreate">
        <el-icon><Plus /></el-icon>&nbsp;新增授权
      </el-button>
      <el-button @click="load">
        <el-icon><RefreshRight /></el-icon>&nbsp;刷新
      </el-button>
    </div>

    <el-alert
      type="info"
      :closable="false"
      show-icon
      title="检项级条目会覆盖项目级条目，包括「降级」"
      description="「整个项目授权管理 + 某个检项单独只读」是常见需求；把某个检项设为「无权限」还能实现黑名单例外。具体条目优先于笼统条目。"
      style="margin-bottom: 12px"
    />

    <el-table v-loading="loading" :data="entries" border stripe size="small">
      <el-table-column prop="groupName" label="用户组" min-width="150" show-overflow-tooltip />
      <el-table-column prop="projectName" label="项目" min-width="130" show-overflow-tooltip />

      <el-table-column label="检项" min-width="140">
        <template #default="{ row }">
          <el-tag v-if="row.checkName" size="small">{{ row.checkName }}</el-tag>
          <el-tag v-else size="small" type="warning">整个项目</el-tag>
        </template>
      </el-table-column>

      <el-table-column label="等级" width="150">
        <template #default="{ row }">
          <el-select
            :model-value="row.level"
            size="small"
            style="width: 130px"
            @change="(value) => changeLevel(row, value)"
          >
            <el-option v-for="o in LEVEL_OPTIONS" :key="o.value" :label="o.label" :value="o.value" />
          </el-select>
        </template>
      </el-table-column>

      <el-table-column label="含义" min-width="200" show-overflow-tooltip>
        <template #default="{ row }">
          <span class="muted">
            {{ (LEVEL_OPTIONS.find((o) => o.value === row.level) || {}).desc || '' }}
          </span>
        </template>
      </el-table-column>

      <el-table-column label="操作" width="96" fixed="right">
        <template #default="{ row }">
          <el-button link type="danger" size="small" @click="removeEntry(row)">撤销</el-button>
        </template>
      </el-table-column>

      <template #empty>
        <span style="color: #909399">
          还没有授权条目。点右上角「新增授权」把某个用户组授权到某个项目，该组成员登录后才能看到内容。
        </span>
      </template>
    </el-table>

    <!-- 新增授权 -->
    <el-dialog v-model="createDialog.visible" title="新增授权" width="520px">
      <el-form label-width="90px" @submit.prevent>
        <el-form-item label="用户组">
          <el-select v-model="createDialog.groupId" placeholder="请选择用户组" filterable style="width: 100%">
            <el-option
              v-for="g in groups"
              :key="g.id"
              :label="g.displayName && g.displayName !== g.name ? `${g.name}（${g.displayName}）` : g.name"
              :value="g.id"
            />
          </el-select>
        </el-form-item>

        <el-form-item label="项目">
          <el-select v-model="createDialog.projectId" placeholder="请选择项目" filterable style="width: 100%">
            <el-option v-for="p in projects" :key="p.id" :label="p.name" :value="p.id" />
          </el-select>
        </el-form-item>

        <el-form-item label="检项">
          <el-select
            v-model="createDialog.checkId"
            placeholder="留空表示整个项目"
            clearable
            :disabled="!createDialog.projectId"
            style="width: 100%"
          >
            <el-option v-for="c in createDialogChecks" :key="c.id" :label="c.name" :value="c.id" />
          </el-select>
          <div class="form-hint block">
            留空 = 对该项目下<strong>所有检项（含将来新建的）</strong>生效。逐个检项授权在检项多时是沉重的管理负担。
          </div>
        </el-form-item>

        <el-form-item label="等级">
          <el-select v-model="createDialog.level" style="width: 100%">
            <el-option v-for="o in LEVEL_OPTIONS" :key="o.value" :label="o.label" :value="o.value" />
          </el-select>
          <div class="form-hint block">
            {{ (LEVEL_OPTIONS.find((o) => o.value === createDialog.level) || {}).desc || '' }}
          </div>
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="createDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="createDialog.busy" @click="submitCreate">授权</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.admin-view {
  padding: 12px 16px 16px;
  height: 100%;
  overflow: auto;
  box-sizing: border-box;
}

.count {
  margin-left: 8px;
  color: #909399;
  font-size: 12px;
}

.muted {
  color: #909399;
  font-size: 12px;
}

.form-hint {
  color: #909399;
  font-size: 12px;
}

.form-hint.block {
  display: block;
  margin-top: 6px;
  line-height: 1.6;
}

.problem-list {
  margin: 0;
  padding-left: 18px;
  line-height: 1.8;
}
</style>
