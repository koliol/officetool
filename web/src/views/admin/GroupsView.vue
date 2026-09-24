<script setup>
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { adminApi } from '../../api'

/**
 * 用户组。
 *
 * 组是**唯一的授权主体** —— 权限只能授给组，不能直接授给个人。
 * 这样「这个人为什么能看到这个项目」永远只有一个答案：他所在的某个组被授权了。
 *
 * 一条硬规则贯穿本页：**域组（Source=Ad）的成员不可手工维护**。
 * 域用户的组关系在每次 SSO 时被 LDAP 结果整体覆盖，手工加的人会在
 * 该成员下次登录时被静默抹掉——表现为「当时生效、第二天失效」，
 * 是最难排查的一类问题。所以宁可在这里直接禁用，也不提供注定失效的功能。
 */
const groups = ref([])
const loading = ref(false)

const createDialog = reactive({ visible: false, busy: false, name: '', displayName: '' })
const editDialog = reactive({ visible: false, busy: false, row: null, displayName: '', isEnabled: true })
const memberDrawer = reactive({ visible: false, loading: false, group: null, members: [], allUsers: [], selected: [], saving: false })

const totalMembers = computed(() => groups.value.reduce((sum, g) => sum + (g.memberCount || 0), 0))

function formatTime(value) {
  if (!value) return '-'
  const d = new Date(value)
  const pad = (n) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
}

function isAdGroup(row) {
  return row.source === 'Ad'
}

/** el-transfer 需要 {key, label} 结构 */
const transferData = computed(() =>
  memberDrawer.allUsers.map((u) => ({
    key: u.id,
    label: `${u.displayName || u.userName}（${u.userName}）`,
  })),
)

async function load() {
  loading.value = true
  try {
    groups.value = await adminApi.groups.list()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '加载用户组失败')
    groups.value = []
  } finally {
    loading.value = false
  }
}

function openCreate() {
  createDialog.name = ''
  createDialog.displayName = ''
  createDialog.visible = true
}

async function submitCreate() {
  const name = createDialog.name.trim()
  if (!name) {
    ElMessage.warning('请输入组名')
    return
  }

  createDialog.busy = true
  try {
    await adminApi.groups.create({ name, displayName: createDialog.displayName.trim() || null })
    createDialog.visible = false
    ElMessage.success(`用户组已创建：${name}`)
    await load()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '创建失败')
  } finally {
    createDialog.busy = false
  }
}

function openEdit(row) {
  editDialog.row = row
  editDialog.displayName = row.displayName || ''
  editDialog.isEnabled = row.isEnabled
  editDialog.visible = true
}

async function submitEdit() {
  editDialog.busy = true
  try {
    await adminApi.groups.update(editDialog.row.id, {
      displayName: editDialog.displayName.trim() || null,
      isEnabled: editDialog.isEnabled,
    })
    editDialog.visible = false
    ElMessage.success('已保存')
    await load()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '保存失败')
  } finally {
    editDialog.busy = false
  }
}

async function removeGroup(row) {
  try {
    await ElMessageBox.confirm(
      `确认删除用户组「${row.name}」？该组上的授权条目会一并失效，组内成员将失去由此组获得的权限。`,
      '删除用户组',
      { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' },
    )
  } catch {
    return
  }

  try {
    await adminApi.groups.remove(row.id)
    ElMessage.success('用户组已删除')
    await load()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '删除失败')
  }
}

async function openMembers(row) {
  memberDrawer.group = row
  memberDrawer.visible = true
  memberDrawer.loading = true
  memberDrawer.selected = []

  try {
    const detail = await adminApi.groups.get(row.id)
    memberDrawer.members = detail.members || []
    memberDrawer.selected = memberDrawer.members.map((m) => m.id)

    // 域组不需要用户名单（成员由域同步决定），省掉这次请求
    if (!isAdGroup(row)) {
      const users = await adminApi.users.list({ page: 1, pageSize: 500 })
      memberDrawer.allUsers = users.items
    } else {
      memberDrawer.allUsers = []
    }
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '加载成员失败')
    memberDrawer.visible = false
  } finally {
    memberDrawer.loading = false
  }
}

async function saveMembers() {
  if (!memberDrawer.group) {
    return
  }

  memberDrawer.saving = true
  try {
    const detail = await adminApi.groups.setMembers(memberDrawer.group.id, memberDrawer.selected)
    memberDrawer.members = detail.members || []
    ElMessage.success(`成员已更新，共 ${memberDrawer.members.length} 人`)
    await load()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '保存成员失败')
  } finally {
    memberDrawer.saving = false
  }
}

onMounted(load)
</script>

<template>
  <div class="admin-view">
    <div class="table-toolbar">
      <span style="color: #606266">
        <strong>用户组</strong>
        <span class="count">共 {{ groups.length }} 个组，{{ totalMembers }} 人次</span>
      </span>

      <span class="spacer" />

      <el-button type="primary" @click="openCreate">
        <el-icon><Plus /></el-icon>&nbsp;新建用户组
      </el-button>
      <el-button @click="load">
        <el-icon><RefreshRight /></el-icon>&nbsp;刷新
      </el-button>
    </div>

    <el-alert
      type="info"
      :closable="false"
      show-icon
      title="权限只授给组，不授给个人"
      description="这样「他为什么能看到这个项目」永远只有一个答案：他所在的某个组被授权了。域组由域同步自动维护，本地组用于归拢没有域账号的人。"
      style="margin-bottom: 12px"
    />

    <el-table v-loading="loading" :data="groups" border stripe size="small">
      <el-table-column prop="name" label="组名" min-width="160" show-overflow-tooltip />
      <el-table-column prop="displayName" label="显示名" min-width="150" show-overflow-tooltip />

      <el-table-column label="来源" width="96">
        <template #default="{ row }">
          <el-tag :type="isAdGroup(row) ? '' : 'info'" size="small">
            {{ isAdGroup(row) ? '域组' : '本地组' }}
          </el-tag>
        </template>
      </el-table-column>

      <el-table-column label="成员数" width="86">
        <template #default="{ row }">
          <el-button link type="primary" size="small" @click="openMembers(row)">
            {{ row.memberCount }} 人
          </el-button>
        </template>
      </el-table-column>

      <el-table-column label="状态" width="86">
        <template #default="{ row }">
          <el-tag v-if="row.isEnabled" type="success" size="small">启用</el-tag>
          <el-tag v-else type="info" size="small">停用</el-tag>
        </template>
      </el-table-column>

      <el-table-column label="创建时间" width="110">
        <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
      </el-table-column>

      <el-table-column label="操作" width="212" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" size="small" @click="openMembers(row)">成员</el-button>
          <el-button link type="primary" size="small" @click="openEdit(row)">编辑</el-button>
          <el-button
            link
            type="danger"
            size="small"
            :disabled="isAdGroup(row)"
            @click="removeGroup(row)"
          >
            删除
          </el-button>
        </template>
      </el-table-column>

      <template #empty>
        <span style="color: #909399">还没有任何用户组。授权前需要先建组。</span>
      </template>
    </el-table>

    <!-- 新建 -->
    <el-dialog v-model="createDialog.visible" title="新建用户组" width="440px">
      <el-form label-width="80px" @submit.prevent>
        <el-form-item label="组名">
          <el-input v-model="createDialog.name" placeholder="如 OUTSOURCE" maxlength="64" @keyup.enter="submitCreate" />
        </el-form-item>
        <el-form-item label="显示名">
          <el-input v-model="createDialog.displayName" placeholder="留空则用组名" maxlength="64" />
        </el-form-item>
      </el-form>

      <div class="form-hint block">本地组用于归拢没有域账号的人（外包、临时账号、访客）。</div>

      <template #footer>
        <el-button @click="createDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="createDialog.busy" @click="submitCreate">创建</el-button>
      </template>
    </el-dialog>

    <!-- 编辑 -->
    <el-dialog v-model="editDialog.visible" title="编辑用户组" width="440px">
      <el-form v-if="editDialog.row" label-width="80px" @submit.prevent>
        <el-form-item label="组名">
          <el-input :model-value="editDialog.row.name" disabled />
        </el-form-item>
        <el-form-item label="显示名">
          <el-input v-model="editDialog.displayName" maxlength="64" />
        </el-form-item>
        <el-form-item label="启用">
          <el-switch v-model="editDialog.isEnabled" />
        </el-form-item>
      </el-form>

      <div class="form-hint block">
        停用该组会让它上面的全部授权立即失效，成员随之失去对应权限 —— 记录保留，随时可以重新启用。
      </div>

      <template #footer>
        <el-button @click="editDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="editDialog.busy" @click="submitEdit">保存</el-button>
      </template>
    </el-dialog>

    <!-- 成员 -->
    <el-drawer v-model="memberDrawer.visible" size="620px">
      <template #header>
        <span>成员管理 —— {{ memberDrawer.group?.name }}</span>
      </template>

      <div v-loading="memberDrawer.loading">
        <template v-if="memberDrawer.group">
          <el-alert
            v-if="isAdGroup(memberDrawer.group)"
            type="warning"
            :closable="false"
            show-icon
            title="域组成员不可手工维护"
            description="域用户的组关系在每次域认证时按 LDAP 结果整体覆盖。手工加入的人会在该成员下次登录时被静默移除（表现为「当时生效、第二天失效」），所以这里只读。需要手工归拢的人请放进本地组。"
            style="margin-bottom: 14px"
          />

          <!-- 域组：只读名单 -->
          <template v-if="isAdGroup(memberDrawer.group)">
            <el-table :data="memberDrawer.members" border stripe size="small">
              <el-table-column prop="userName" label="登录名" min-width="150" />
              <el-table-column prop="displayName" label="姓名" min-width="140" />

              <template #empty>
                <span style="color: #909399">
                  尚未同步到成员。成员名单会在该组内的人下次登录时自动建立。
                </span>
              </template>
            </el-table>
          </template>

          <!-- 本地组：可整体调整 -->
          <template v-else>
            <el-transfer
              v-model="memberDrawer.selected"
              :data="transferData"
              filterable
              filter-placeholder="搜索登录名或姓名"
              :titles="['未加入', '已加入']"
            />

            <div class="form-hint block">
              保存时是<strong>整体覆盖</strong>：右侧名单即最终成员，左侧的人会被移出该组。
            </div>

            <div class="drawer-footer">
              <el-button @click="memberDrawer.visible = false">关闭</el-button>
              <el-button type="primary" :loading="memberDrawer.saving" @click="saveMembers">
                保存成员（{{ memberDrawer.selected.length }} 人）
              </el-button>
            </div>
          </template>
        </template>
      </div>
    </el-drawer>
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

.form-hint {
  color: #909399;
  font-size: 12px;
}

.form-hint.block {
  display: block;
  margin: 10px 0 0;
  line-height: 1.6;
}

.drawer-footer {
  margin-top: 18px;
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
