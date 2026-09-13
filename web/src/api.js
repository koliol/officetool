import axios from 'axios'

const client = axios.create({
  baseURL: '/api',
  timeout: 60000,
})

// 统一把后端 ApiError 转成可直接展示的中文提示
client.interceptors.response.use(
  (response) => response,
  (error) => {
    const payload = error.response?.data
    const message = payload?.message || error.message || '请求失败'
    error.friendlyMessage = message
    return Promise.reject(error)
  },
)

export const api = client

export const systemApi = {
  health: () => client.get('/health').then((r) => r.data),
  config: () => client.get('/system/config').then((r) => r.data),
  sync: () => client.post('/system/sync').then((r) => r.data),
  logs: (params) => client.get('/logs', { params }).then((r) => r.data),
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
