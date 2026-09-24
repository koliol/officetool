<script setup>
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { authApi } from '../api'

/**
 * 访问令牌（供外部工具 / 脚本使用）。
 *
 * 为什么需要这一页：开启鉴权后，非浏览器的调用方没有任何登录态可用 ——
 * 它们拿不到 Cookie，也没有 Kerberos 票据（不在浏览器上下文里）。
 * 典型场景是部署验证脚本 `scripts/api-smoke.sh` 与后续的自动化任务。
 * 令牌就是给这类调用方的一条不依赖浏览器状态的身份通道。
 *
 * 注意：**桌面插件（officetool://）并不需要令牌**。它只做两件事：
 * 解析协议地址、把 UNC 路径交给系统 Shell 打开，全程不发 HTTP 请求。
 * 这里提供的令牌是给脚本用的，不是给插件用的。
 *
 * 明文令牌只在创建的那一刻返回一次，之后连管理员都读不出来（库里只有 SHA-256）。
 * 所以创建成功后用醒目弹窗要求当场复制，而不是丢一条 message 就完事。
 */
const tokens = ref([])
const loading = ref(false)

const createDialog = reactive({ visible: false, name: '', busy: false })
const issuedDialog = reactive({ visible: false, token: '', name: '' })
const expiry = ref('365')

const EXPIRY_OPTIONS = [
  { value: '90', label: '90 天' },
  { value: '365', label: '1 年' },
  { value: '1095', label: '3 年' },
  { value: 'never', label: '长期有效（不推荐）' },
]

const hasActive = computed(() => tokens.value.some((t) => !t.isRevoked))

function formatTime(value) {
  if (!value) return '-'
  const d = new Date(value)
  const pad = (n) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(
    d.getMinutes(),
  )}`
}

function statusOf(row) {
  if (row.isRevoked) {
    return { text: '已吊销', type: 'info' }
  }
  if (row.expiresAt && new Date(row.expiresAt).getTime() < Date.now()) {
    return { text: '已过期', type: 'danger' }
  }
  return { text: '有效', type: 'success' }
}

async function load() {
  loading.value = true
  try {
    tokens.value = await authApi.tokens.list()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '加载令牌失败')
    tokens.value = []
  } finally {
    loading.value = false
  }
}

function openCreate() {
  createDialog.name = ''
  expiry.value = '365'
  createDialog.visible = true
}

function computeExpiry() {
  if (expiry.value === 'never') {
    return null
  }

  const days = Number(expiry.value) || 365
  const d = new Date(Date.now() + days * 24 * 60 * 60 * 1000)
  // 去掉毫秒与时区后缀：后端按本地时间解析，带 Z 会被当成 UTC 而偏 8 小时
  const pad = (n) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(
    d.getMinutes(),
  )}:00`
}

async function submitCreate() {
  createDialog.busy = true
  try {
    const created = await authApi.tokens.create(createDialog.name.trim() || '外部工具', computeExpiry())
    createDialog.visible = false

    issuedDialog.name = created.name
    issuedDialog.token = created.token
    issuedDialog.visible = true

    await load()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '创建令牌失败')
  } finally {
    createDialog.busy = false
  }
}

async function copyToken() {
  try {
    await navigator.clipboard.writeText(issuedDialog.token)
    ElMessage.success('令牌已复制，请妥善保存并配置到调用方')
  } catch {
    ElMessage.warning('浏览器拒绝剪贴板访问，请手动选中复制')
  }
}

async function revoke(row) {
  try {
    await ElMessageBox.confirm(
      `确认吊销令牌「${row.name}」？正在使用它的脚本会立刻失去访问权限。`,
      '吊销令牌',
      { type: 'warning', confirmButtonText: '吊销', cancelButtonText: '取消' },
    )
  } catch {
    return
  }

  try {
    await authApi.tokens.revoke(row.id)
    ElMessage.success('令牌已吊销')
    await load()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '吊销失败')
  }
}

onMounted(load)
</script>

<template>
  <div class="token-view">
    <el-alert
      type="info"
      :closable="false"
      show-icon
      title="令牌是给外部工具 / 脚本用的"
      style="margin-bottom: 14px"
    >
      <template #default>
        <div class="hint">
          开启鉴权后，没有浏览器登录态的调用方需要一个令牌才能访问接口。请求时带上
          <code>Authorization: Bearer &lt;令牌&gt;</code> 即可。
          <br />
          <strong>桌面插件不需要令牌</strong>：它只解析 <code>officetool://</code> 协议并把共享路径交给
          Windows 打开，全程不调用接口。
          <br />
          明文令牌<strong>只在生成时显示一次</strong>，请当场复制保存；若丢失只能重新生成。
        </div>
      </template>
    </el-alert>

    <div class="table-toolbar">
      <span style="color: #606266">
        <strong>我的令牌</strong>
        <el-tag v-if="hasActive" type="success" size="small" style="margin-left: 8px">已配置</el-tag>
        <el-tag v-else type="info" size="small" style="margin-left: 8px">尚未生成</el-tag>
      </span>

      <span class="spacer" />

      <el-button type="primary" @click="openCreate">
        <el-icon><Key /></el-icon>&nbsp;生成新令牌
      </el-button>
      <el-button @click="load">
        <el-icon><RefreshRight /></el-icon>&nbsp;刷新
      </el-button>
    </div>

    <el-table v-loading="loading" :data="tokens" border stripe size="small">
      <el-table-column prop="name" label="用途备注" min-width="160" show-overflow-tooltip />
      <el-table-column label="状态" width="90">
        <template #default="{ row }">
          <el-tag :type="statusOf(row).type" size="small">{{ statusOf(row).text }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="创建时间" width="150">
        <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
      </el-table-column>
      <el-table-column label="最近使用" width="150">
        <template #default="{ row }">{{ formatTime(row.lastUsedAt) }}</template>
      </el-table-column>
      <el-table-column label="过期时间" width="150">
        <template #default="{ row }">{{ row.expiresAt ? formatTime(row.expiresAt) : '长期有效' }}</template>
      </el-table-column>

      <el-table-column label="操作" width="96" fixed="right">
        <template #default="{ row }">
          <el-button link type="danger" size="small" :disabled="row.isRevoked" @click="revoke(row)">
            吊销
          </el-button>
        </template>
      </el-table-column>

      <template #empty>
        <span style="color: #909399">还没有令牌。需要脚本或外部工具访问接口时在这里生成。</span>
      </template>
    </el-table>

    <el-dialog v-model="createDialog.visible" title="生成访问令牌" width="460px">
      <el-form label-width="90px" @submit.prevent>
        <el-form-item label="用途备注">
          <el-input
            v-model="createDialog.name"
            placeholder="如：部署冒烟脚本 / 检测室 03 号机"
            maxlength="64"
            @keyup.enter="submitCreate"
          />
        </el-form-item>

        <el-form-item label="有效期">
          <el-select v-model="expiry" style="width: 100%">
            <el-option v-for="o in EXPIRY_OPTIONS" :key="o.value" :label="o.label" :value="o.value" />
          </el-select>
        </el-form-item>
      </el-form>

      <div class="hint">备注用于日后区分是哪台机器或哪个脚本在用，吊销时能认出来。</div>

      <template #footer>
        <el-button @click="createDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="createDialog.busy" @click="submitCreate">生成</el-button>
      </template>
    </el-dialog>

    <!-- 明文只此一次：用「必须点复制」的弹窗，不用一闪而过的提示 -->
    <el-dialog v-model="issuedDialog.visible" title="令牌已生成" width="560px" :close-on-click-modal="false">
      <el-alert
        type="warning"
        :closable="false"
        show-icon
        title="请立即复制并保存"
        description="关闭本窗口后将无法再次查看这枚令牌，只能重新生成。"
        style="margin-bottom: 14px"
      />

      <div style="margin-bottom: 6px; color: #606266; font-size: 13px">
        令牌（{{ issuedDialog.name }}）
      </div>
      <div class="unc-path">{{ issuedDialog.token }}</div>

      <template #footer>
        <el-button type="primary" @click="copyToken">复制令牌</el-button>
        <el-button @click="issuedDialog.visible = false">我已保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.token-view {
  padding: 12px 16px 16px;
  height: 100%;
  overflow: auto;
  box-sizing: border-box;
}

.hint {
  color: #909399;
  font-size: 12px;
  line-height: 1.7;
}

code {
  background: #f5f7fa;
  padding: 1px 4px;
  border-radius: 3px;
  font-family: Consolas, Monaco, monospace;
}
</style>
