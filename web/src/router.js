import { reactive } from 'vue'

/**
 * 极简 hash 路由（零依赖）。
 *
 * 为什么不用 vue-router：
 *   1. 页面只有十来个、没有嵌套路由，引入一个路由库换不来任何实际能力；
 *   2. hash 路由不需要服务端回退配置，Kestrel 只托管静态文件即可；
 *      群晖反向代理把应用挂在子路径（如 /officetool）下也不会失效；
 *   3. 依赖越少，构建产物越小、升级面越窄（本项目已因终端安全策略踩过构建坑）。
 *
 * 接口形状刻意与 vue-router 对齐（`current.path` / `current.name` / `navigate()`），
 * 将来若确实需要嵌套路由或按需加载，替换成本很低。
 */

/** 路由表。`admin: true` 的页面只有系统管理员可见（App.vue 里做跳转）。 */
export const routes = [
  { path: '/', name: 'workspace', title: '记录中心' },
  { path: '/record', name: 'record', title: '填写记录' },
  { path: '/templates', name: 'templates', title: '模板库' },
  { path: '/template-editor', name: 'template-editor', title: '模板设计' },
  { path: '/legacy-files', name: 'legacy-files', title: '历史文件' },
  { path: '/trash', name: 'trash', title: '回收站' },
  { path: '/me/tokens', name: 'tokens', title: '访问令牌' },
  { path: '/admin/users', name: 'admin-users', title: '用户管理', admin: true },
  { path: '/admin/groups', name: 'admin-groups', title: '用户组', admin: true },
  { path: '/admin/acl', name: 'admin-acl', title: '授权配置', admin: true },
  { path: '/admin/rules', name: 'admin-rules', title: '提取规则', admin: true },
  { path: '/login', name: 'login', title: '登录', anonymous: true },
  { path: '/forbidden', name: 'forbidden', title: '无权访问', anonymous: true },
]

const HOME = routes[0]

/** 当前路由。组件里直接读它即可 —— reactive，变化会触发重渲染。 */
export const current = reactive({
  path: HOME.path,
  name: HOME.name,
  title: HOME.title,
  /** URL 查询参数（#/record?id=1 → { id: '1' }） */
  query: {},
  admin: false,
  anonymous: false,
  /** 访问了路由表里没有的路径，已回退到首页 */
  notFound: false,
  /** 每次导航自增。同名页面需要强制重建时（如换账号后回到首页）可以依赖它。 */
  version: 0,
})

function parseHash() {
  let path = (window.location.hash || '').replace(/^#/, '')

  // 查询串：ELN 的记录页 / 设计器靠 ?id= 传参，不能再像从前那样直接丢弃
  const query = {}
  const queryAt = path.indexOf('?')
  if (queryAt >= 0) {
    for (const [key, value] of new URLSearchParams(path.slice(queryAt + 1)).entries()) {
      query[key] = value
    }
    path = path.slice(0, queryAt)
  }

  try {
    path = decodeURIComponent(path)
  } catch {
    // 不完整的 % 转义会抛异常，此时按原样处理，好过让路由整个崩掉
  }

  if (!path || path === '/') {
    path = '/'
  }

  if (!path.startsWith('/')) {
    path = `/${path}`
  }

  // 去掉尾部斜杠（根路径除外），让 /admin/users 与 /admin/users/ 等价
  if (path.length > 1) {
    path = path.replace(/\/+$/, '')
  }

  current.query = query

  return path || '/'
}

function apply() {
  const path = parseHash()
  const match = routes.find((r) => r.path === path)

  // 未知路径：**保留**用户输入的 path，交给 App.vue 渲染「页面不存在」。
  // 原先直接回退到首页，地址栏显示 /xxx 而内容是工作台，`notFound` 也永远没人消费，
  // 结果是一堆"点了没反应"的链接看起来像点击失效。
  const target = match || { path, name: 'not-found', title: '页面不存在' }

  current.path = target.path
  current.name = target.name
  current.title = target.title
  current.admin = Boolean(target.admin)
  current.anonymous = Boolean(target.anonymous)
  current.notFound = !match
  current.version += 1

  document.title = match ? `${target.title} · Office 检验记录` : 'Office 文档管理工具'
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

/** 生成可放进 href 的链接。支持 { id: 1 } 形式的查询参数。 */
export function href(path, query = {}) {
  const search = new URLSearchParams(query).toString()
  const base = `#${path.startsWith('/') ? path : `/${path}`}`
  return search ? `${base}?${search}` : base
}

window.addEventListener('hashchange', apply)

export function startRouter() {
  // 首次进入没有 hash 时补一个，保证地址栏可见、刷新后位置不丢
  if (!window.location.hash) {
    window.location.replace(`${window.location.pathname}${window.location.search}#${HOME.path}`)
  }

  apply()
}
