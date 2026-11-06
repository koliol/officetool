import { reactive } from 'vue'

/**
 * 悬浮通知栈。
 *
 * 与 ElMessage 的分工：
 *  - **ElMessage** 说「刚刚发生了什么」（一次操作的结果，几秒后自己消失）；
 *  - **NoticeStack** 说「你需要知道的状态」（插件未安装、权限只读、初始化失败），
 *    这类提示既不能一闪而过，也不该像原先的 el-alert 那样插在文档流里把页面顶下去。
 *
 * 所以这里做成右下角悬浮卡片：不占布局、可关闭、可以带一个动作按钮。
 * 关闭状态默认只在**本次会话**记住（sessionStorage），刷新后重新提示一次 ——
 * 让用户知道「这个状态还在」，但不至于每次点页面都被打断。
 */
let seq = 0

export const notices = reactive([])

const SEEN_KEY = 'officetool.dismissedNotices'

function loadSeen() {
  try {
    return new Set(JSON.parse(sessionStorage.getItem(SEEN_KEY) || '[]'))
  } catch {
    return new Set()
  }
}

function saveSeen(set) {
  try {
    sessionStorage.setItem(SEEN_KEY, JSON.stringify([...set]))
  } catch {
    /* 隐私模式下不可用，忽略 */
  }
}

/**
 * 推入一条通知。
 * @param {object} notice
 * @param {string} notice.key 去重键。带 key 的通知在本会话被人关掉后不再出现。
 * @param {'info'|'success'|'warning'|'error'} notice.type
 * @param {string} notice.title
 * @param {string} notice.description
 * @param {number} notice.timeout 毫秒；0 表示只能手动关闭
 * @param {string} notice.actionLabel 可选的动作按钮文案
 * @param {Function} notice.onAction
 */
export function pushNotice(notice) {
  const { key = '' } = notice

  if (key) {
    if (loadSeen().has(key)) {
      return null
    }

    // 同一个 key 已经挂着：更新内容而不是叠一张新卡
    const existing = notices.find((n) => n.key === key)
    if (existing) {
      Object.assign(existing, notice)
      return existing.id
    }
  }

  const item = { id: ++seq, timeout: 0, description: '', type: 'info', ...notice }
  notices.push(item)

  if (item.timeout > 0) {
    window.setTimeout(() => dismissNotice(item.id), item.timeout)
  }

  return item.id
}

export function dismissNotice(id) {
  const index = notices.findIndex((n) => n.id === id)
  if (index < 0) {
    return
  }

  const [removed] = notices.splice(index, 1)

  // 带 key 的（状态类）提示记住用户的关闭动作；一次性提示不必记
  if (removed?.key) {
    const seen = loadSeen()
    seen.add(removed.key)
    saveSeen(seen)
  }
}

export function clearNotices() {
  notices.splice(0, notices.length)
}

/** 换账号时用：让本轮会话的「已关闭」记录也一起清掉 */
export function resetNoticeMemory() {
  try {
    sessionStorage.removeItem(SEEN_KEY)
  } catch {
    /* 忽略 */
  }
  clearNotices()
}
