import { defineStore } from 'pinia'
import { projectApi, systemApi, templateApi, documentApi } from './api'

const PLUGIN_FLAG = 'officetool.pluginConfirmed'

export const useAppStore = defineStore('app', {
  state: () => ({
    config: null,
    projects: [],
    checks: [],
    currentProject: '',
    currentCheck: '',
    /** 用户是否确认本机装了桌面插件（浏览器无法可靠探测自定义协议） */
    pluginConfirmed: localStorage.getItem(PLUGIN_FLAG) === '1',
    /**
     * 全局数据版本号：任一写操作后自增，各列表面板监听它重新拉取，
     * 避免「模板页生成文档后，文档页看不到新文件」这类跨面板不同步。
     */
    dataVersion: 0,
    loading: false,
  }),

  getters: {
    protocol: (s) => s.config?.protocol || 'officetool',
    allowedExtensions: (s) => s.config?.allowedExtensions || [],
    /** 未安装插件时的提示文案（设计文档 §8.3） */
    pluginHint: (s) =>
      s.pluginConfirmed
        ? '已标记本机安装了桌面插件'
        : '未检测到桌面插件，可复制下方路径到资源管理器打开',
  },

  actions: {
    async init() {
      this.config = await systemApi.config()
      await this.loadProjects()
    },

    async loadProjects() {
      this.loading = true
      try {
        // 一次拉全树，避免对每个项目再请求 checks（N+1）
        const tree = await projectApi.tree()
        const projects = tree.map((p) => ({
          ...p,
          checks: (p.checks || []).map((c) => ({ ...c, projectName: p.name })),
        }))
        this.projects = projects
      } finally {
        this.loading = false
      }
    },

    async selectProject(name) {
      this.currentProject = name
      this.currentCheck = ''
      this.checks = name ? await projectApi.checks(name) : []
    },

    /** 树节点点击：项目或检项都能定位（检项需要连同所属项目一起设上） */
    async pick(projectName, checkName) {
      this.currentProject = projectName
      this.currentCheck = checkName || ''
      this.checks = projectName ? await projectApi.checks(projectName) : []
    },

    selectCheck(name) {
      this.currentCheck = name
    },

    async loadChecks() {
      if (!this.currentProject) {
        this.checks = []
        return
      }
      this.checks = await projectApi.checks(this.currentProject)
    },

    async createProject(name) {
      await projectApi.create(name)
      await this.loadProjects()
      await this.selectProject(name)
    },

    async removeProject(name) {
      await projectApi.remove(name)
      if (this.currentProject === name) {
        this.currentProject = ''
        this.currentCheck = ''
        this.checks = []
      }
      await this.loadProjects()
    },

    async createCheck(name) {
      await projectApi.createCheck(this.currentProject, name)
      await this.loadChecks()
      // 树的子节点来自 loadProjects 里缓存的 checks，必须一并刷新，
      // 否则新建的检项不会出现在左侧树里。
      await this.loadProjects()
      this.currentCheck = name
    },

    async removeCheck(name) {
      await projectApi.removeCheck(this.currentProject, name)
      if (this.currentCheck === name) this.currentCheck = ''
      await this.loadChecks()
      await this.loadProjects()
    },

    setPluginConfirmed(value) {
      this.pluginConfirmed = value
      localStorage.setItem(PLUGIN_FLAG, value ? '1' : '0')
    },

    /** 写操作后调用，通知所有面板刷新 */
    bumpDataVersion() {
      this.dataVersion += 1
    },

    /** 构造 officetool://open?path=<URL 编码的客户端路径>（设计文档 §8.3、§9.2） */
    buildProtocolUrl(file) {
      return `${this.protocol}://open?path=${encodeURIComponent(file.accessPath)}`
    },

    listTemplates: (params) => templateApi.list(params),
    listDocuments: (params) => documentApi.list(params),

    /** 供左侧树按需展开时拉取检项 */
    async listChecksFor(project) {
      return await projectApi.checks(project)
    },
  },
})
