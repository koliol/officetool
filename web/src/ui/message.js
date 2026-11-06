import { ElMessage as RawMessage } from 'element-plus'

/**
 * 全局提示的统一出口。
 *
 * 统一在这里收口，是为了让「提示长什么样」只有一处定义：
 *  - **悬浮**：Element Plus 的 Message 本身就是 fixed 定位，不占文档流，
 *    不会把下方表格往下顶（原先用 el-alert 内联提示时，提示出现会把整页挤一下）；
 *  - **可关闭**：默认带右上角关闭按钮，长文案不会只靠倒计时赌用户手速；
 *  - **堆叠合并**：`grouping` 让同一段文案不重复弹，连续失败只显示一次并计数。
 *
 * 用法与 ElMessage 完全一致（`message.success('已保存')`），
 * 只是不需要在每个页面重复传这些选项。
 */
const DEFAULTS = {
  showClose: true,
  duration: 4500,
  grouping: true,
  offset: 76,
  customClass: 'ot-message',
}

function make(kind) {
  return (options, ...rest) => {
    if (typeof options === 'string') {
      return RawMessage[kind]({ ...DEFAULTS, message: options, type: kind }, ...rest)
    }

    return RawMessage[kind]({ ...DEFAULTS, ...options, type: kind }, ...rest)
  }
}

export const ElMessage = {
  success: make('success'),
  error: make('error'),
  warning: make('warning'),
  info: make('info'),
  closeAll: () => RawMessage.closeAll(),
}
