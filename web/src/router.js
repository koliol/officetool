import { reactive } from 'vue'

/**
 * 极简 hash 路由（零依赖）。
 *
 * 为什么不用 vue-router：
 *   1. 目标环境的终端安全策略**禁止执行 node.exe**，既装不了新依赖，
 *      也跑不了 vite build 验证。引入一个无法安装、无法验证的依赖，
 *      等于交付一份没人跑过的代码。
 *   2. hash 路由不需要服务端回退配置，Kestrel 只托管静态文件即可；
 *      群晖反向代理把应用挂在子路径（如 /officetool）下也不会失效。
 *   3. 本应用只有十来个页面，嵌套路由、懒加载、导航守卫这些能力都用不上。
 *
 * 接口形状刻意与 vue-router 对齐（`current.path` / `current.name` / `navigate()`），
 * 将来若确实需要嵌套路由或按需加载，替换成本很低。
 */

/** 路由表。`admin: true` 的页面只有系统管理员可见（App.vue 里做跳转）。 */
export const routes = [
  { path: '/', name: 'workspace', title: '文档工作台' },
  { path: '/trash', name: 'trash', title: '回收站' },
  { path: '/me/tokens', name: 'tokens', title: '访问令牌' },
  { path: '/admin/users', name: 'admin-users', title: '用户管理', admin: true },
  { path: '/admin/groups', name: 'admin-groups', title: '用户组', admin: true },
  { path: '/admin/acl', name: 'admin-acl', title: '授权配置', admin: true },
  { path: '/login', name: 'login', title: '登录', anonymous: true },
  { path: '/forbidden', name: 'forbidden', title: '无权访问', anonymous: true },
]

const HOME = routes[0]

/** 当前路由。组件里直接读它即可 —— reactive，变化会触发重渲染。 */
export const current = reactive({
  path: HOME.path,
  name: HOME.name,
  title: HOME.title,
  admin: false,
  anonymous: false,
  /** 访问了路由表里没有的路径，已回退到首页 */
  notFound: false,
  /** 每次导航自增。同名页面需要强制重建时（如换账号后回到首页）可以依赖它。 */
  version: 0,
})

function parseHash() {
  let path = (window.location.hash || '').replace(/^#/, '')

  // 查询串本应用用不到，但用户手输或历史链接可能带上，直接丢弃
  const query = path.indexOf('?')
  if (query >= 0) {
    path = path.slice(0, query)
  }

  try {
    path = decodeURIComponent(path)
  } catch {
    // 不完整的 % 转义会抛异常，此时按原样处理，好过让路由整个崩掉
  }

  if (!path || path === '/') {
    return '/'
  }

  if (!path.startsWith('/')) {
    path = `/${path}`
  }

  // 去掉尾部斜杠（根路径除外），让 /admin/users 与 /admin/users/ 等价
  if (path.length > 1) {
    path = path.replace(/\/+$/, '')
  }

  return path || '/'
}

function apply() {
  const path = parseHash()
  const match = routes.find((r) => r.path === path)
  const target = match || HOME

  current.path = target.path
  current.name = target.name
  current.title = target.title
  current.admin = Boolean(target.admin)
  current.anonymous = Boolean(target.anonymous)
  current.notFound = !match
  current.version += 1

  document.title = match ? `${target.title} · Office 文档管理工具` : 'Office 文档管理工具'
}

/** 跳转。重复点击同一路径也会重新触发一次（version 自增）。 */
export function navigate(path, { replace = false } = {}) {
  const normalized = path.startsWith('/') ? path : `/${path}`
  const target = `#${normalized}`

  if (window.location.hash === target) {
    apply()
    return
  }

  if (replace) {
    window.location.replace(`${window.location.pathname}${window.location.search}${target}`)
  } else {
    window.location.hash = target
  }
}

/** 生成可放进 href 的链接，便于用 el-link / a 标签直接跳转。 */
export function href(path) {
  return `#${path.startsWith('/') ? path : `/${path}`}`
}

window.addEventListener('hashchange', apply)

export function startRouter() {
  // 首次进入没有 hash 时补一个，保证地址栏可见、刷新后位置不丢
  if (!window.location.hash) {
    window.location.replace(`${window.location.pathname}${window.location.search}#${HOME.path}`)
  }

  apply()
}
