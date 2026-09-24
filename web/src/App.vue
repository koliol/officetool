<script setup>
import { computed, onMounted, ref, watch } from 'vue'
import { useAppStore } from './store'
import { useAuthStore } from './auth'
import { current, href, navigate } from './router'
import LoginView from './views/LoginView.vue'
import ForbiddenView from './views/ForbiddenView.vue'
import WorkspaceView from './views/WorkspaceView.vue'
import TrashView from './views/TrashView.vue'
import TokensView from './views/TokensView.vue'
import AdminUsersView from './views/admin/UsersView.vue'
import AdminGroupsView from './views/admin/GroupsView.vue'
import AdminAclView from './views/admin/AclView.vue'
import ChangePasswordDialog from './components/ChangePasswordDialog.vue'

const store = useAppStore()
const auth = useAuthStore()

const initError = ref('')
const passwordDialog = ref(false)

/**
 * 导航项按权限过滤 —— 这就是「按角色切视图」的落点。
 *
 * 刻意不引入「角色」这一层概念：系统里只有「权限等级」，菜单是否出现
 * 由等级推导。多一层角色映射只会带来「角色与权限不一致」这类难查的问题。
 */
const navItems = computed(() => {
  const items = [
    { path: '/', label: '文档工作台' },
    { path: '/trash', label: '回收站', visible: auth.canAnyManage },
    { path: '/me/tokens', label: '访问令牌' },
    { path: '/admin/users', label: '用户管理', visible: auth.canAdmin },
    { path: '/admin/groups', label: '用户组', visible: auth.canAdmin },
    { path: '/admin/acl', label: '授权配置', visible: auth.canAdmin },
  ]

  return items.filter((item) => item.visible !== false)
})

const view = computed(() => current.path)

/**
 * 工作区数据初始化。
 *
 * 必须在确认身份之后再调：`/api/system/config` 与 `/api/projects/tree`
 * 都要求已认证，未登录时打过去只会拿到 401。
 */
async function ensureWorkspace() {
  if (!auth.authenticated || store.config) {
    return
  }

  try {
    await store.init()
  } catch (error) {
    initError.value = error.friendlyMessage || '工作区初始化失败'
  }
}

onMounted(async () => {
  try {
    await auth.bootstrap()
  } catch (error) {
    initError.value = error.friendlyMessage || '无法连接到服务端'
  }

  await ensureWorkspace()

  // 首次登录被要求改密：直接弹出来。藏在菜单里等于没做，
  // 引导出来的初始密码会被一直用下去。
  if (auth.authenticated && auth.mustChangePassword) {
    passwordDialog.value = true
  }
})

watch(
  () => auth.authenticated,
  async (ok) => {
    if (!ok) {
      return
    }

    await ensureWorkspace()

    if (auth.mustChangePassword) {
      passwordDialog.value = true
    }
  },
)

/**
 * 路由守卫。
 *
 * 没有做成「跳转前中间件」，而是做成对当前路由的响应式校验：
 * 用户可能通过改地址栏、刷新、点链接等任意方式到达任何路径，
 * 只在跳转时判一次会漏。这里每次状态变化都重新校验，兜住所有入口。
 */
watch(
  () => [current.path, auth.ready, auth.authenticated, auth.canAdmin],
  () => {
    if (!auth.ready) {
      return
    }

    if (!auth.authenticated) {
      if (current.path !== '/login') {
        navigate('/login', { replace: true })
      }
      return
    }

    // 已登录却停在登录页：直接送进工作台，避免出现「登录了但页面还是登录框」
    if (current.path === '/login') {
      navigate('/', { replace: true })
      return
    }

    if (current.admin && !auth.canAdmin) {
      navigate('/forbidden', { replace: true })
    }
  },
  { immediate: true },
)
</script>

<template>
  <div v-if="!auth.ready" class="boot">
    <el-icon class="is-loading" :size="26"><Loading /></el-icon>
    <span>正在加载…</span>
  </div>

  <LoginView v-else-if="!auth.authenticated" />

  <div v-else class="app-shell">
    <header class="app-header">
      <div class="brand">Office 文档管理工具</div>

      <!--
        用原生 <a> + hash 链接做导航：零额外依赖，右键「在新标签打开」也能用，
        刷新后位置不丢。比给 el-menu 挂 router 更稳，也不用引入路由库。
      -->
      <nav class="app-nav">
        <a
          v-for="item in navItems"
          :key="item.path"
          class="nav-item"
          :class="{ 'is-active': view === item.path }"
          :href="href(item.path)"
        >
          {{ item.label }}
        </a>
      </nav>

      <div class="header-right">
        <el-tag v-if="!auth.authEnabled" type="warning" size="small" effect="dark">
          鉴权未启用
        </el-tag>
        <el-tag v-else-if="auth.isAd" size="small" effect="plain">域账户</el-tag>

        <el-dropdown trigger="click">
          <span class="user-chip">
            <el-icon><User /></el-icon>
            {{ auth.displayLabel }}
            <el-icon><ArrowDown /></el-icon>
          </span>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item disabled>
                {{ auth.userName || '匿名' }}
                <template v-if="auth.isSystemAdmin">（超级管理员）</template>
              </el-dropdown-item>
              <el-dropdown-item divided @click="passwordDialog = true">修改密码</el-dropdown-item>
              <el-dropdown-item @click="auth.logout()">退出登录</el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
      </div>
    </header>

    <div class="app-body">
      <el-alert
        v-if="initError"
        type="error"
        :closable="false"
        show-icon
        :title="initError"
        style="margin: 12px 16px 0"
      />

      <el-alert
        v-if="auth.accessError"
        type="warning"
        :closable="false"
        show-icon
        title="未能加载权限信息"
        :description="`${auth.accessError}。页面已按最小权限呈现，部分按钮会不可见；刷新可重试。`"
        style="margin: 12px 16px 0"
      />

      <div class="app-view">
        <WorkspaceView v-if="view === '/'" />

        <TrashView v-else-if="view === '/trash'" />

        <TokensView v-else-if="view === '/me/tokens'" />

        <AdminUsersView v-else-if="view === '/admin/users'" />

        <AdminGroupsView v-else-if="view === '/admin/groups'" />

        <AdminAclView v-else-if="view === '/admin/acl'" />

        <ForbiddenView v-else-if="view === '/forbidden'" />

        <el-result
          v-else
          icon="warning"
          title="页面不存在"
          sub-title="地址可能已失效，请从上方导航进入。"
        >
          <template #extra>
            <el-button type="primary" @click="navigate('/')">回到工作台</el-button>
          </template>
        </el-result>
      </div>
    </div>

    <ChangePasswordDialog v-model:visible="passwordDialog" :forced="auth.mustChangePassword" />
  </div>
</template>

<style scoped>
.boot {
  height: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 10px;
  color: #909399;
}

.app-nav {
  display: flex;
  align-items: center;
  gap: 2px;
  margin-left: 8px;
}

.nav-item {
  color: #c0c4cc;
  text-decoration: none;
  font-size: 14px;
  padding: 6px 12px;
  border-radius: 4px;
  white-space: nowrap;
  transition: color 0.15s, background-color 0.15s;
}

.nav-item:hover {
  color: #fff;
  background: rgba(255, 255, 255, 0.08);
}

.nav-item.is-active {
  color: #fff;
  background: rgba(255, 255, 255, 0.16);
  font-weight: 600;
}

.user-chip {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: #dcdfe6;
  font-size: 13px;
  cursor: pointer;
  outline: none;
}

.user-chip:hover {
  color: #fff;
}
</style>
