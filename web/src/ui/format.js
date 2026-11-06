/**
 * 展示用的格式化函数。
 *
 * 这些函数原先在 7 个组件里各复制了一份（`formatSize` 4 处、`formatTime` 6 处），
 * 内容逐字相同 —— 意味着「把 1024 改成 1000」「补上秒」这类调整要改七遍，
 * 漏一处就会出现两个页面对同一份文件显示不同大小。
 */

/** 字节数 → 人类可读。空值显示为 -。 */
export function formatSize(bytes) {
  if (bytes === null || bytes === undefined) return '-'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(2)} MB`
}

/** 时间 → yyyy-MM-dd HH:mm。 */
export function formatTime(value, fallback = '-') {
  if (!value) return fallback

  const d = new Date(value)
  const pad = (n) => String(n).padStart(2, '0')

  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(
    d.getMinutes(),
  )}`
}

/** 时间 → yyyy-MM-dd（只到天，用于"加入日期"这类不需要时刻的列）。 */
export function formatDate(value, fallback = '-') {
  if (!value) return fallback

  const d = new Date(value)
  const pad = (n) => String(n).padStart(2, '0')

  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
}
