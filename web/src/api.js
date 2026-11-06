import axios from 'axios'

const client = axios.create({
  baseURL: '/api',
  timeout: 60000,
})

/**
 * 401 的全局出口。
 *
 * api.js 不能直接 import 认证 store —— store 会 import api.js，形成循环依赖。
 * 所以这里只留一个回调位，由 auth store 在应用启动时注册。
 */
let unauthorizedHandler = null

export function setUnauthorizedHandler(handler) {
  unauthorizedHandler = handler
}

/**
 * 把 axios 的错误翻成能直接展示的中文。
 *
 * 后端的 ApiError 一定带 message，但**不是所有失败都来自后端**：
 * 网关 502、Kestrel 拒绝的 413、断网导致的 ERR_NETWORK 都没有 JSON 响应体，
 * 此时原先会退化成 "Request failed with status code 502" 这种英文，
 * 用户看不懂，我们也无从判断是权限问题还是服务没起。
 */
function describeError(error, payload) {
  if (payload?.message) {
    return payload.message
  }

  const status = error.response?.status
  const byStatus = {
    400: '请求参数有误，请检查填写内容。',
    401: '登录状态已失效，请重新登录。',
    403: '没有执行该操作的权限，请联系管理员授权。',
    404: '请求的资源不存在，可能已被他人删除。',
    409: '操作与当前状态冲突，请刷新后重试。',
    413: '文件超过服务端允许的大小上限。',
    415: '服务端不支持该文件类型。',
    500: '服务端处理失败，请查看服务端日志。',
    502: '网关或共享存储不可用，请确认共享目录已挂载。',
    503: '服务暂时不可用，请稍后重试。',
    504: '服务响应超时，请稍后重试。',
  }

  if (status && byStatus[status]) {
    return byStatus[status]
  }

  if (error.code === 'ERR_NETWORK') {
    return '无法连接服务端，请检查网络或确认服务已启动。'
  }

  if (error.code === 'ECONNABORTED') {
    return '请求超时，请稍后重试。'
  }

  return error.message || '请求失败'
}

// 统一把后端 ApiError 转成可直接展示的中文提示
client.interceptors.response.use(
  (response) => response,
  (error) => {
    const status = error.response?.status
    const payload = error.response?.data

    error.status = status
    // 后端 ApiError 的字段名是 code（不是 error）。两种都认，兼容早期写的调用方。
    error.code = payload?.code || payload?.error || null
    error.friendlyMessage = describeError(error, payload)

    // 首次登录未改密：服务端会拦掉除 /api/auth/* 以外的请求。
    // 这里广播一次，由 App.vue 立刻弹出改密窗口 —— 否则用户只会看到
    // 一连串"操作失败"，不知道为什么什么都不让做。
    if (error.code === 'must_change_password') {
      window.dispatchEvent(new CustomEvent('officetool:must-change-password'))
    }

    // 认证接口自身的 401 是**预期结果**（还没登录 / 会话来路不明），由调用方处理。
    // 若在这里也触发全局跳转，登录页会把自己踢走、形成死循环。
    // 白名单刻意列全路径而不是 `/auth/` 一刀切：那样会把 /auth/change-password 的
    // 401（会话过期）也吞掉，用户改完密码点提交后停在页面上，不知道发生了什么。
    const url = error.config?.url || ''
    const isAuthEndpoint = ['/auth/me', '/auth/login', '/auth/sso', '/auth/logout'].some((path) =>
      url.startsWith(path),
    )

    if (status === 401 && !isAuthEndpoint && typeof unauthorizedHandler === 'function') {
      unauthorizedHandler()
    }

    return Promise.reject(error)
  },
)

export const api = client

export const systemApi = {
  config: () => client.get('/system/config').then((r) => r.data),
  sync: () => client.post('/system/sync').then((r) => r.data),
  logs: (params) => client.get('/logs', { params }).then((r) => r.data),
}

export const authApi = {
  /**
   * 当前身份。前端启动第一个请求就打这里。
   * 未登录时返回 401 —— 这是正常流程的一部分，不是错误。
   */
  me: () => client.get('/auth/me').then((r) => r.data),

  /**
   * Windows 集成认证入口。只能靠**整页跳转**触发：
   * Negotiate 需要浏览器发起 401 握手，fetch 拿不到这个行为。
   * 成功后后端签发 Cookie 并 302 回首页。
   */
  ssoUrl: '/api/auth/sso',

  login: (userName, password) =>
    client.post('/auth/login', { userName, password }).then((r) => r.data),

  logout: () => client.post('/auth/logout').then((r) => r.data),

  changePassword: (oldPassword, newPassword) =>
    client.post('/auth/change-password', { oldPassword, newPassword }),

  /** 全部可见检项的有效等级。前端一次拉全，之后在内存里查表。 */
  accessMap: () => client.get('/auth/rights/map').then((r) => r.data),

  /** 外部工具 / 脚本的访问令牌（桌面插件不需要）。明文只在创建响应里出现一次。 */
  tokens: {
    list: () => client.get('/auth/tokens').then((r) => r.data),
    create: (name, expiresAt) =>
      client.post('/auth/tokens', { name, expiresAt: expiresAt || null }).then((r) => r.data),
    revoke: (id) => client.delete(`/auth/tokens/${id}`),
  },
}

export const projectApi = {
  list: () => client.get('/projects').then((r) => r.data),
  /** 项目 + 检项树，前端初始化用（避免 N+1） */
  tree: () => client.get('/projects/tree').then((r) => r.data),
  create: (name) => client.post('/projects', { name }).then((r) => r.data),
  remove: (name) => client.delete(`/projects/${encodeURIComponent(name)}`),
  checks: (project) =>
    client.get(`/projects/${encodeURIComponent(project)}/checks`).then((r) => r.data),
  createCheck: (project, name) =>
    client.post(`/projects/${encodeURIComponent(project)}/checks`, { name }).then((r) => r.data),
  removeCheck: (project, check) =>
    client.delete(`/projects/${encodeURIComponent(project)}/checks/${encodeURIComponent(check)}`),
}

export const trashApi = {
  list: (params) => client.get('/trash', { params }).then((r) => r.data),
  restore: (id) => client.post(`/trash/${id}/restore`).then((r) => r.data),
  purge: (id) => client.delete(`/trash/${id}`),
}

export const templateApi = {
  list: (params) => client.get('/templates', { params }).then((r) => r.data),
  upload: (project, check, file, onProgress) => {
    const form = new FormData()
    form.append('project', project)
    form.append('check', check)
    form.append('file', file)
    return client
      .post('/templates/upload', form, {
        headers: { 'Content-Type': 'multipart/form-data' },
        onUploadProgress: onProgress,
      })
      .then((r) => r.data)
  },
  /**
   * 复制模板。
   *
   * `targetProject` / `targetCheck` 都留空 → 复制到当前文件夹（旧行为，总是加「_副本」）；
   * 都传 → 跨目录复制（沿用原名，撞名才加「_副本」；显式命名撞名返回 409）。
   */
  copy: (payload) => client.post('/templates/copy', payload).then((r) => r.data),
  rename: (payload) => client.post('/templates/rename', payload).then((r) => r.data),
  remove: (params) => client.delete('/templates', { params }),
}

export const documentApi = {
  list: (params) => client.get('/documents', { params }).then((r) => r.data),
  create: (payload) => client.post('/documents/create', payload).then((r) => r.data),
  rename: (payload) => client.post('/documents/rename', payload).then((r) => r.data),
  /** 复制到其他项目/检项（目标由用户在网页上选择，不是复制到当前文件夹） */
  copy: (payload) => client.post('/documents/copy', payload).then((r) => r.data),
  remove: (params) => client.delete('/documents', { params }),
}

// 附件（二期功能；「提取」接口契约已预留，当前返回 501）
export const attachmentApi = {
  list: (project, check) =>
    client.get('/attachments', { params: { project, check } }).then((r) => r.data),
  upload: (project, check, file, onProgress) => {
    const form = new FormData()
    form.append('project', project)
    form.append('check', check)
    form.append('file', file)
    return client
      .post('/attachments/upload', form, {
        headers: { 'Content-Type': 'multipart/form-data' },
        onUploadProgress: onProgress,
      })
      .then((r) => r.data)
  },
  remove: (project, check, fileName) =>
    client.delete('/attachments', { params: { project, check, fileName } }),
  /**
   * 执行提取。契约已定，但后端尚未实现，会返回 501（feature_not_available），
   * 消息体是明确的「尚未实现」说明，可直接展示给用户。
   */
  extract: (payload) => client.post('/attachments/extract', payload).then((r) => r.data),
}

// 提取规则：与模板/文档/附件一样的「项目/检项」两级目录，规则本体是文件
export const ruleApi = {
  list: (project, check) =>
    client.get('/extraction-rules', { params: { project, check } }).then((r) => r.data),
  upload: (project, check, file, onProgress) => {
    const form = new FormData()
    form.append('project', project)
    form.append('check', check)
    form.append('file', file)
    return client
      .post('/extraction-rules/upload', form, {
        headers: { 'Content-Type': 'multipart/form-data' },
        onUploadProgress: onProgress,
      })
      .then((r) => r.data)
  },
  remove: (project, check, fileName) =>
    client.delete('/extraction-rules', { params: { project, check, fileName } }),
  /** 网页上直接改备注。传空串即清除。 */
  updateNote: (payload) => client.post('/extraction-rules/note', payload).then((r) => r.data),
  /** 复制到其他项目/检项（备注一并带过去） */
  copy: (payload) => client.post('/extraction-rules/copy', payload).then((r) => r.data),
}

/**
 * 权限管理。全部落在 `/api/admin`，只有系统管理员可达。
 *
 * 服务端会**重新读数据库**判定超管位，而不是信任 Cookie 里的角色声明
 * （声明在签发时就固定了，刚被加为超管的人用旧声明仍会被拒）。
 * 所以前端的 `canAdmin` 只决定入口是否显示，不构成权限依据。
 */
export const adminApi = {
  users: {
    list: (params) => client.get('/admin/users', { params }).then((r) => r.data),
    /** 响应里的明文密码**只出现这一次** */
    create: (payload) => client.post('/admin/users', payload).then((r) => r.data),
    update: (id, payload) => client.patch(`/admin/users/${id}`, payload).then((r) => r.data),
    resetPassword: (id) => client.post(`/admin/users/${id}/reset-password`).then((r) => r.data),
    remove: (id) => client.delete(`/admin/users/${id}`),
    /** 展开某人的有效权限及来源，用于回答「他为什么看不到这个项目」 */
    effective: (userName) =>
      client.get(`/admin/users/${encodeURIComponent(userName)}/effective`).then((r) => r.data),
  },

  groups: {
    list: () => client.get('/admin/groups').then((r) => r.data),
    get: (id) => client.get(`/admin/groups/${id}`).then((r) => r.data),
    create: (payload) => client.post('/admin/groups', payload).then((r) => r.data),
    update: (id, payload) => client.patch(`/admin/groups/${id}`, payload).then((r) => r.data),
    remove: (id) => client.delete(`/admin/groups/${id}`),
    /** 整体覆盖成员。仅本地组可用 —— 域组的成员以域同步为准。 */
    setMembers: (id, userIds) =>
      client.put(`/admin/groups/${id}/members`, { userIds }).then((r) => r.data),
  },

  acl: {
    list: (params) => client.get('/admin/acl', { params }).then((r) => r.data),
    create: (payload) => client.post('/admin/acl', payload).then((r) => r.data),
    update: (id, level) => client.put(`/admin/acl/${id}`, { level }).then((r) => r.data),
    remove: (id) => client.delete(`/admin/acl/${id}`),
  },

  /** 权限配置自检：无人管理的项目、超管数量、是否配过授权 */
  health: () => client.get('/admin/health').then((r) => r.data),
}
