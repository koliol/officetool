import { defineStore } from 'pinia'
import { authApi, setUnauthorizedHandler } from './api'
import { navigate } from './router'

/**
 * 权限等级。与后端 `AccessLevel` 一一对应，**递进**语义：
 * 高级别自动包含低级别的全部能力（规则管理 ⊃ 管理 ⊃ 编辑 ⊃ 只读）。
 *
 * 前端只按等级比较，不按动作名判断 —— 后端将来若调整某动作所需等级，
 * 前端无需跟着改，只要比较方式一致即可。
 */
export const LEVELS = { None: 0, Read: 1, Write: 2, Manage: 3, RuleManage: 4 }

/** 授权页下拉用。顺序即递进顺序，末尾的说明直接告诉管理员这一档能做什么。 */
export const LEVEL_OPTIONS = [
  { value: 'Read', label: '只读', desc: '查看列表与详情、复制路径、打开文件' },
  { value: 'Write', label: '编辑', desc: '上传模板、新建文档、重命名、复制到其他文件夹' },
  { value: 'Manage', label: '管理', desc: '删除（进回收站）、回收站恢复、建删检项' },
  { value: 'RuleManage', label: '规则管理', desc: '维护提取规则（上传 / 删除 / 备注 / 复制）' },
  { value: 'None', label: '无权限', desc: '不可见。用于把某人从整个项目的授权里单独排除' },
]

export function levelName(value) {
  const found = LEVEL_OPTIONS.find((x) => x.value === value)
  return found ? found.label : value || '无'
}

export function levelTagType(value) {
  switch (value) {
    case 'Read':
      return 'info'
    case 'Write':
      return 'primary'
    case 'Manage':
      return 'warning'
    case 'RuleManage':
      return 'danger'
    default:
      return 'info'
  }
}

/**
 * 作用域键。与后端 `UserAccess.ScopeKey` 用同一个分隔符（\\u001f，单元分隔符）：
 * 项目名与检项名都允许出现短横线、下划线，用常见的 - 或 _ 拼接会撞键。
 *
 * 注意：这里用 \\u001f 字面量而不是真实控制字符，避免源码里出现不可见字符。
 */
export function scopeKey(project, check) {
  return `${project}\u001f${check || ''}`
}

/** 本项目内「上一轮域认证是否已尝试过」。用于在登录页解释失败原因，避免跳转死循环。 */
const SSO_TRIED_KEY = 'officetool.ssoTried'

export const useAuthStore = defineStore('auth', {
  state: () => ({
    /** 启动流程（查身份 + 拉权限表）是否已完成。未完成时不要渲染业务页面。 */
    ready: false,
    /** 服务端是否启用了鉴权。false = 内网可信形态，全部放行。 */
    authEnabled: true,
    authenticated: false,
    userName: '',
    displayName: '',
    /** 'Ad' | 'Local' */
    source: '',
    isSystemAdmin: false,
    mustChangePassword: false,

    /** 权限表的三种索引，来自 /api/auth/rights/map，供同步查表 */
    accessLoaded: false,
    accessError: '',
    /** 检项 id → 等级 */
    levelByCheckId: {},
    /** 「项目名\u001f检项名」→ 等级（规则、附件、回收站按名称过滤，走这个） */
    levelByScope: {},
    /** 项目 id → 该项目下可见节点中的**最高**等级 */
    levelByProjectId: {},
    /** 项目 id → 该项目下可见节点中的**最低**等级。建删检项要求全项目都有 Manage。 */
    minLevelByProjectId: {},

    /** 上一轮域认证是否失败（登录页据此给出解释） */
    ssoFailed: false,
  }),

  getters: {
    displayLabel: (s) => s.displayName || s.userName || '未登录',
    isAd: (s) => s.source === 'Ad',
    /** 能否进管理后台。鉴权关闭时后端也放行，这里保持一致。 */
    canAdmin: (s) => !s.authEnabled || s.isSystemAdmin,
    /** 有没有任何一个可见检项 */
    hasAnyScope: (s) => s.isSystemAdmin || Object.keys(s.levelByScope).length > 0,
    /** 至少有一个节点可编辑（决定左侧面板是否露出上传/新建等入口） */
    canAnyWrite: (s) =>
      s.isSystemAdmin || Object.values(s.levelByScope).some((v) => v >= LEVELS.Write),
    /** 至少有一个节点可维护提取规则（决定左侧是否露出「提取规则」标签页） */
    canAnyRuleManage: (s) =>
      s.isSystemAdmin || Object.values(s.levelByScope).some((v) => v >= LEVELS.RuleManage),
    /** 至少有一个节点可管理（决定是否显示回收站入口） */
    canAnyManage: (s) =>
      s.isSystemAdmin || Object.values(s.levelByScope).some((v) => v >= LEVELS.Manage),
  },

  actions: {
    /** 应用 /api/auth/me 的结果 */
    applyMe(me) {
      this.authEnabled = me?.authEnabled !== false
      this.authenticated = Boolean(me?.authenticated)
      this.userName = me?.userName || ''
      this.displayName = me?.displayName || ''
      this.source = me?.source || ''
      this.isSystemAdmin = Boolean(me?.isSystemAdmin)
      this.mustChangePassword = Boolean(me?.mustChangePassword)
    },

    /**
     * 启动流程：查身份 → 拉权限表。
     *
     * 两件事必须**都**完成才能渲染业务页面：只拿到身份就渲染，
     * 按钮会先出现再消失（权限表到达后），观感很差且容易误点。
     */
    async bootstrap() {
      try {
        this.applyMe(await authApi.me())
      } catch (error) {
        if (error.status === 401) {
          // 未登录是正常流程的一部分，不是错误
          this.authEnabled = true
          this.authenticated = false
        } else {
          this.ready = true
          throw error
        }
      }

      if (this.authenticated) {
        await this.loadAccess()
        // 身份已确认，清掉「域认证尝试中」标记
        try {
          sessionStorage.removeItem(SSO_TRIED_KEY)
        } catch {
          /* 隐私模式下 sessionStorage 可能不可用，忽略 */
        }
      }

      this.ready = true
      return this.authenticated
    },

    /**
     * 拉取权限表并展开成查表结构。
     *
     * 拿不到时**不抛异常**：按最小权限渲染（按钮隐藏）。
     * 理由——隐藏按钮失败只是体验问题，真正的拦截在服务端；
     * 若这里抛异常导致整页打不开，反而把可用性问题升级成不可用。
     */
    async loadAccess() {
      try {
        const map = await authApi.accessMap()

        this.authEnabled = map?.authEnabled !== false
        this.isSystemAdmin = Boolean(map?.isSystemAdmin)

        const byCheck = {}
        const byScope = {}
        const byProject = {}
        const minByProject = {}

        for (const row of map?.checks || []) {
          const level = LEVELS[row.level] ?? LEVELS.None

          byCheck[row.checkId] = level
          byScope[scopeKey(row.project, row.check)] = level

          byProject[row.projectId] = Math.max(byProject[row.projectId] ?? 0, level)
          minByProject[row.projectId] = Math.min(minByProject[row.projectId] ?? 99, level)
        }

        this.levelByCheckId = byCheck
        this.levelByScope = byScope
        this.levelByProjectId = byProject
        this.minLevelByProjectId = minByProject
        this.accessLoaded = true
        this.accessError = ''
      } catch (error) {
        this.levelByCheckId = {}
        this.levelByScope = {}
        this.levelByProjectId = {}
        this.minLevelByProjectId = {}
        this.accessLoaded = false
        this.accessError = error?.friendlyMessage || '权限信息加载失败'

        // 401 由拦截器统一处理跳转；其余情况保留错误文案供页面提示
        if (error?.status !== 401) {
          console.warn('[auth] 权限表加载失败，已按最小权限渲染', error)
        }
      }
    },

    async login(userName, password) {
      this.applyMe(await authApi.login(userName, password))
      await this.loadAccess()
    },

    async logout() {
      try {
        await authApi.logout()
      } finally {
        // 无论后端是否成功，本地身份必须清干净：
        // 否则退出后仍按旧权限渲染，看起来像「退出没生效」
        this.$reset()
        this.ready = true
      }
    },

    /**
     * 域认证（Windows 集成认证）。
     *
     * 必须整页跳转，不能走 XHR：Negotiate 依赖浏览器收到 401 后自动完成握手。
     * 跳出去之前在 sessionStorage 留个标记，万一后端返回 401 把用户弹回来，
     * 登录页就能解释「域认证没能完成」，而不是让人对着登录框猜。
     */
    goSso() {
      try {
        sessionStorage.setItem(SSO_TRIED_KEY, '1')
      } catch {
        /* 忽略 */
      }
      window.location.href = authApi.ssoUrl
    },

    /** 登录页挂载时调用：上一轮域认证是否失败过 */
    consumeSsoFailure() {
      let tried = false
      try {
        tried = sessionStorage.getItem(SSO_TRIED_KEY) === '1'
        if (tried) {
          sessionStorage.removeItem(SSO_TRIED_KEY)
        }
      } catch {
        tried = false
      }

      this.ssoFailed = tried
      return tried
    },

    async changePassword(oldPassword, newPassword) {
      await authApi.changePassword(oldPassword, newPassword)
      this.mustChangePassword = false
    },

    // ── 同步查表（渲染期用，不发请求） ──────────────────────────────

    /** 检项级等级 */
    levelOfCheck(checkId) {
      if (!this.authEnabled || this.isSystemAdmin) {
        return LEVELS.RuleManage
      }
      return this.levelByCheckId[checkId] ?? LEVELS.None
    },

    /** 按 项目名/检项名 查等级（规则、附件、回收站、文件行都用它） */
    levelOfScope(project, check) {
      if (!this.authEnabled || this.isSystemAdmin) {
        return LEVELS.RuleManage
      }
      return this.levelByScope[scopeKey(project, check)] ?? LEVELS.None
    },

    /** 项目下可见节点的最高等级（用于「我还有没有能做的事」这类判断） */
    levelOfProject(projectId) {
      if (!this.authEnabled || this.isSystemAdmin) {
        return LEVELS.RuleManage
      }
      return this.levelByProjectId[projectId] ?? LEVELS.None
    },

    /**
     * 能否维护该项目的结构（新建/删除检项、删除项目）。
     *
     * 后端要求「该项目下**所有**检项都有 Manage」。前端只知道可见的那些，
     * 所以这里是有意保守的**必要**条件——全部可见检项都够管理权时先亮出按钮，
     * 真有看不到的检项时由服务端拒绝并给出提示。
     */
    canManageProjectStructure(projectId) {
      if (!this.authEnabled || this.isSystemAdmin) {
        return true
      }
      const min = this.minLevelByProjectId[projectId]
      return typeof min === 'number' && min >= LEVELS.Manage
    },

    /** 等级比较。`level` 传 LEVELS 里的值。 */
    atLeast(actual, level) {
      return actual >= level
    },
  },
})

/**
 * 注册全局 401 出口。必须由 main.js 在 pinia 装好之后调用一次。
 *
 * 只在「已经进过一次应用」时跳登录页：否则启动阶段 /api/auth/me 的 401
 * 会在登录页自己把自己再跳一次，形成死循环。
 */
export function installAuthGuards() {
  setUnauthorizedHandler(() => {
    const auth = useAuthStore()
    if (!auth.ready) {
      return
    }
    auth.authenticated = false
    navigate('/login', { replace: true })
  })
}
