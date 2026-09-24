<script setup>
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { adminApi } from '../../api'
import { useAuthStore, levelName, levelTagType } from '../../auth'

/**
 * 用户管理。
 *
 * 两个刻意的取舍：
 *
 * 1. 明文密码（初始密码 / 重置后的密码）**只弹一次**，用必须点「我已保存」
 *    的弹窗呈现。管理员要把它转述给本人，一闪而过的提示条会被漏掉，
 *    漏掉就得再重置一次，而每次重置都会让旧密码立即失效。
 *
 * 2. 「有效权限」是这一页最有用的功能，但藏在一个独立按钮里 ——
 *    它要回答的是「他为什么看不到某个项目」。只看最终等级无法定位问题，
 *    所以展开时会带上每条权限的**授予来源**（哪个组的哪一条授权）。
 */
const auth = useAuthStore()

const rows = ref([])
const total = ref(0)
const loading = ref(false)

const filters = reactive({ keyword: '', source: '', enabled: '', page: 1, pageSize: 50 })

const createDialog = reactive({ visible: false, busy: false, userName: '', displayName: '', isSystemAdmin: false })
const editDialog = reactive({ visible: false, busy: false, row: null, displayName: '', isEnabled: true, isSystemAdmin: false, mustChangePassword: false })
const secretDialog = reactive({ visible: false, title: '', userName: '', password: '' })
const effectiveDrawer = reactive({ visible: false, loading: false, data: null })

function formatTime(value) {
  if (!value) return '从未登录'
  const d = new Date(value)
  const pad = (n) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(
    d.getMinutes(),
  )}`
}

function sourceLabel(source) {
  return source === 'Ad' ? '域账户' : '本地账户'
}

async function load() {
  loading.value = true
  try {
    const data = await adminApi.users.list({
      keyword: filters.keyword || undefined,
      source: filters.source || undefined,
      enabled: filters.enabled === '' ? undefined : filters.enabled === 'true',
      page: filters.page,
      pageSize: filters.pageSize,
    })
    rows.value = data.items
    total.value = data.total
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '加载用户失败')
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
  filters.keyword = ''
  filters.source = ''
  filters.enabled = ''
  filters.page = 1
  load()
}

function openCreate() {
  createDialog.userName = ''
  createDialog.displayName = ''
  createDialog.isSystemAdmin = false
  createDialog.visible = true
}

async function submitCreate() {
  const userName = createDialog.userName.trim()
  if (!userName) {
    ElMessage.warning('请输入登录名')
    return
  }

  createDialog.busy = true
  try {
    const result = await adminApi.users.create({
      userName,
      displayName: createDialog.displayName.trim() || null,
      isSystemAdmin: createDialog.isSystemAdmin,
      mustChangePassword: true,
    })

    createDialog.visible = false
    secretDialog.title = '账户已创建'
    secretDialog.userName = result.user.userName
    secretDialog.password = result.password
    secretDialog.visible = true

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
  editDialog.isSystemAdmin = row.isSystemAdmin
  editDialog.mustChangePassword = row.mustChangePassword
  editDialog.visible = true
}

async function submitEdit() {
  editDialog.busy = true
  try {
    await adminApi.users.update(editDialog.row.id, {
      displayName: editDialog.displayName.trim() || null,
      isEnabled: editDialog.isEnabled,
      isSystemAdmin: editDialog.isSystemAdmin,
      mustChangePassword: editDialog.mustChangePassword,
    })

    editDialog.visible = false
    ElMessage.success('已保存')
    await load()
  } catch (error) {
    // 「不能停用/降级最后一个可用管理员」由服务端以 409 拒绝，原样展示
    ElMessage.error(error.friendlyMessage || '保存失败')
  } finally {
    editDialog.busy = false
  }
}

async function resetPassword(row) {
  try {
    await ElMessageBox.confirm(
      `确认重置 ${row.userName} 的密码？旧密码将立即失效，且对方必须重新设置。`,
      '重置密码',
      { type: 'warning', confirmButtonText: '重置', cancelButtonText: '取消' },
    )
  } catch {
    return
  }

  try {
    const result = await adminApi.users.resetPassword(row.id)
    secretDialog.title = '密码已重置'
    secretDialog.userName = row.userName
    secretDialog.password = result.password
    secretDialog.visible = true
    await load()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '重置失败')
  }
}

async function removeUser(row) {
  try {
    await ElMessageBox.confirm(`确认删除用户 ${row.userName}？`, '删除用户', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消',
    })
  } catch {
    return
  }

  try {
    await adminApi.users.remove(row.id)
    ElMessage.success('用户已删除')
    await load()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '删除失败')
  }
}

async function copySecret() {
  try {
    await navigator.clipboard.writeText(secretDialog.password)
    ElMessage.success('密码已复制')
  } catch {
    ElMessage.warning('浏览器拒绝剪贴板访问，请手动选中复制')
  }
}

async function showEffective(row) {
  effectiveDrawer.visible = true
  effectiveDrawer.loading = true
  effectiveDrawer.data = null
  try {
    effectiveDrawer.data = await adminApi.users.effective(row.userName)
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '展开权限失败')
    effectiveDrawer.visible = false
  } finally {
    effectiveDrawer.loading = false
  }
}

onMounted(load)
</script>

<template>
  <div class="admin-view">
    <el-form class="search-bar" :inline="true" label-width="70px" @submit.prevent>
      <el-form-item label="搜索">
        <el-input
          v-model="filters.keyword"
          placeholder="登录名或姓名"
          clearable
          style="width: 180px"
          @keyup.enter="onQuery"
        />
      </el-form-item>

      <el-form-item label="来源">
        <el-select v-model="filters.source" placeholder="全部" clearable style="width: 130px" @change="onQuery">
          <el-option label="域账户" value="Ad" />
          <el-option label="本地账户" value="Local" />
        </el-select>
      </el-form-item>

      <el-form-item label="状态">
        <el-select v-model="filters.enabled" placeholder="全部" clearable style="width: 120px" @change="onQuery">
          <el-option label="已启用" value="true" />
          <el-option label="已停用" value="false" />
        </el-select>
      </el-form-item>

      <el-form-item>
        <el-button type="primary" @click="onQuery">查询</el-button>
        <el-button @click="onReset">重置</el-button>
      </el-form-item>
    </el-form>

    <div class="table-toolbar">
      <span style="color: #606266">
        <strong>用户</strong>
        <span class="count">共 {{ total }} 人</span>
      </span>

      <span class="spacer" />

      <el-button type="primary" @click="openCreate">
        <el-icon><Plus /></el-icon>&nbsp;新建本地账户
      </el-button>
      <el-button @click="load">
        <el-icon><RefreshRight /></el-icon>&nbsp;刷新
      </el-button>
    </div>

    <el-alert
      type="info"
      :closable="false"
      show-icon
      title="域账户无需手工创建"
      description="域用户首次通过域认证登录时会自动建档。这里只需要维护那些没有域账号的人（外包、临时账号）为本地账户。"
      style="margin-bottom: 12px"
    />

    <el-table v-loading="loading" :data="rows" border stripe size="small">
      <el-table-column prop="userName" label="登录名" min-width="150" show-overflow-tooltip />
      <el-table-column prop="displayName" label="姓名" min-width="130" show-overflow-tooltip />

      <el-table-column label="来源" width="96">
        <template #default="{ row }">
          <el-tag :type="row.source === 'Ad' ? '' : 'info'" size="small">{{ sourceLabel(row.source) }}</el-tag>
        </template>
      </el-table-column>

      <el-table-column label="所属组" min-width="180">
        <template #default="{ row }">
          <template v-if="row.groups && row.groups.length">
            <el-tag v-for="g in row.groups" :key="g" size="small" type="info" class="group-tag">{{ g }}</el-tag>
          </template>
          <span v-else class="muted">未加入任何组</span>
        </template>
      </el-table-column>

      <el-table-column label="状态" width="110">
        <template #default="{ row }">
          <el-tag v-if="row.isSystemAdmin" type="danger" size="small">超级管理员</el-tag>
          <el-tag v-else-if="row.isEnabled" type="success" size="small">启用</el-tag>
          <el-tag v-else type="info" size="small">停用</el-tag>
        </template>
      </el-table-column>

      <el-table-column label="待改密" width="82">
        <template #default="{ row }">
          <el-tag v-if="row.mustChangePassword" type="warning" size="small">是</el-tag>
          <span v-else class="muted">-</span>
        </template>
      </el-table-column>

      <el-table-column label="最近登录" width="140">
        <template #default="{ row }">{{ formatTime(row.lastLoginAt) }}</template>
      </el-table-column>

      <el-table-column label="操作" width="238" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" size="small" @click="showEffective(row)">有效权限</el-button>
          <el-button link type="primary" size="small" @click="openEdit(row)">编辑</el-button>
          <el-button
            link
            type="primary"
            size="small"
            :disabled="row.source !== 'Local'"
            @click="resetPassword(row)"
          >
            重置密码
          </el-button>
          <el-button
            link
            type="danger"
            size="small"
            :disabled="row.source !== 'Local'"
            @click="removeUser(row)"
          >
            删除
          </el-button>
        </template>
      </el-table-column>

      <template #empty>
        <span style="color: #909399">没有匹配的用户。</span>
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

    <!-- 新建账户 -->
    <el-dialog v-model="createDialog.visible" title="新建本地账户" width="460px">
      <el-form label-width="90px" @submit.prevent>
        <el-form-item label="登录名">
          <el-input
            v-model="createDialog.userName"
            placeholder="如 zhangsan"
            maxlength="64"
            @keyup.enter="submitCreate"
          />
        </el-form-item>
        <el-form-item label="姓名">
          <el-input v-model="createDialog.displayName" placeholder="留空则用登录名" maxlength="64" />
        </el-form-item>
        <el-form-item label="超级管理员">
          <el-switch v-model="createDialog.isSystemAdmin" />
          <span class="form-hint">跳过全部权限校验，仅用于系统维护与故障自救</span>
        </el-form-item>
      </el-form>

      <div class="form-hint block">初始密码由系统生成，创建后只显示一次，需要转告本人。</div>

      <template #footer>
        <el-button @click="createDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="createDialog.busy" @click="submitCreate">创建</el-button>
      </template>
    </el-dialog>

    <!-- 编辑账户 -->
    <el-dialog v-model="editDialog.visible" title="编辑账户" width="460px">
      <el-form v-if="editDialog.row" label-width="110px" @submit.prevent>
        <el-form-item label="登录名">
          <el-input :model-value="editDialog.row.userName" disabled />
        </el-form-item>
        <el-form-item label="姓名">
          <el-input v-model="editDialog.displayName" maxlength="64" />
        </el-form-item>
        <el-form-item label="启用">
          <el-switch v-model="editDialog.isEnabled" />
          <span class="form-hint">停用后立即失去全部权限</span>
        </el-form-item>
        <el-form-item label="超级管理员">
          <el-switch v-model="editDialog.isSystemAdmin" />
        </el-form-item>
        <el-form-item label="下次登录改密">
          <el-switch v-model="editDialog.mustChangePassword" />
        </el-form-item>
      </el-form>

      <div class="form-hint block">
        系统不允许把最后一个可用的超级管理员停用或降级 —— 那是唯一无法从界面内部修复的故障。
      </div>

      <template #footer>
        <el-button @click="editDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="editDialog.busy" @click="submitEdit">保存</el-button>
      </template>
    </el-dialog>

    <!-- 明文密码：只此一次 -->
    <el-dialog v-model="secretDialog.visible" :title="secretDialog.title" width="460px" :close-on-click-modal="false">
      <el-alert
        type="warning"
        :closable="false"
        show-icon
        title="请立即复制并转告本人"
        description="关闭后无法再次查看这份密码，只能重新重置。"
        style="margin-bottom: 14px"
      />

      <div class="field-label">登录名</div>
      <div class="unc-path">{{ secretDialog.userName }}</div>

      <div class="field-label" style="margin-top: 12px">初始密码</div>
      <div class="unc-path">{{ secretDialog.password }}</div>

      <template #footer>
        <el-button type="primary" @click="copySecret">复制密码</el-button>
        <el-button @click="secretDialog.visible = false">我已保存</el-button>
      </template>
    </el-dialog>

    <!-- 有效权限展开 -->
    <el-drawer v-model="effectiveDrawer.visible" title="有效权限" size="620px">
      <div v-loading="effectiveDrawer.loading">
        <template v-if="effectiveDrawer.data">
          <el-descriptions :column="1" border size="small" style="margin-bottom: 14px">
            <el-descriptions-item label="用户">
              {{ effectiveDrawer.data.displayName }}（{{ effectiveDrawer.data.userName }}）
            </el-descriptions-item>
            <el-descriptions-item label="超级管理员">
              <el-tag v-if="effectiveDrawer.data.isSystemAdmin" type="danger" size="small">
                是（跳过全部 ACL）
              </el-tag>
              <span v-else>否</span>
            </el-descriptions-item>
            <el-descriptions-item label="所属组">
              <template v-if="effectiveDrawer.data.groups.length">
                <el-tag v-for="g in effectiveDrawer.data.groups" :key="g" size="small" type="info" class="group-tag">
                  {{ g }}
                </el-tag>
              </template>
              <span v-else class="muted">未加入任何组（因此不会有任何权限）</span>
            </el-descriptions-item>
          </el-descriptions>

          <el-alert
            v-if="!effectiveDrawer.data.groups.length"
            type="warning"
            :closable="false"
            show-icon
            title="该用户不属于任何启用的组"
            description="权限是按组授予的。把他加入某个组并在「授权配置」里给该组授权，他才能看到内容。"
            style="margin-bottom: 12px"
          />

          <el-table :data="effectiveDrawer.data.checks" border stripe size="small">
            <el-table-column label="项目 / 检项" min-width="170">
              <template #default="{ row }">{{ row.project }} / {{ row.check }}</template>
            </el-table-column>
            <el-table-column label="等级" width="96">
              <template #default="{ row }">
                <el-tag :type="levelTagType(row.level)" size="small">{{ levelName(row.level) }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column prop="grantedBy" label="授予来源" min-width="200" show-overflow-tooltip />

            <template #empty>
              <span style="color: #909399">该用户目前看不到任何检项。</span>
            </template>
          </el-table>
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

.group-tag {
  margin-right: 4px;
  margin-bottom: 2px;
}

.muted {
  color: #c0c4cc;
  font-size: 12px;
}

.pager {
  margin-top: 14px;
  display: flex;
  justify-content: flex-end;
}

.field-label {
  color: #606266;
  font-size: 13px;
  margin-bottom: 6px;
}

.form-hint {
  color: #909399;
  font-size: 12px;
  margin-left: 10px;
}

.form-hint.block {
  display: block;
  margin: 10px 0 0;
  line-height: 1.6;
}
</style>
