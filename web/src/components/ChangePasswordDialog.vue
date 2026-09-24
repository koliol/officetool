<script setup>
import { computed, reactive, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { useAuthStore } from '../auth'

/**
 * 修改密码。
 *
 * `forced` 用于「首次登录必须改密」：此时关不掉、也没有取消按钮。
 * 引导部署时生成的初始密码会写进启动日志，如果允许跳过，
 * 它就永远不会被改掉——那等于系统里存在一个公开的固定管理员密码。
 */
const props = defineProps({
  visible: { type: Boolean, default: false },
  forced: { type: Boolean, default: false },
})

const emit = defineEmits(['update:visible'])

const auth = useAuthStore()

const form = reactive({ oldPassword: '', newPassword: '', confirmPassword: '' })
const busy = ref(false)

const closable = computed(() => !props.forced)

const title = computed(() => (props.forced ? '首次登录，请设置新密码' : '修改密码'))

/** 域账户的密码不在本系统里，改密入口对它没有意义 */
const isLocalAccount = computed(() => !auth.authEnabled || auth.source === 'Local')

watch(
  () => props.visible,
  (open) => {
    if (open) {
      form.oldPassword = ''
      form.newPassword = ''
      form.confirmPassword = ''
    }
  },
)

function close() {
  if (props.forced) {
    return
  }
  emit('update:visible', false)
}

async function submit() {
  if (!form.oldPassword) {
    ElMessage.warning('请输入当前密码')
    return
  }

  if (form.newPassword.length < 8) {
    ElMessage.warning('新密码至少 8 位')
    return
  }

  if (form.newPassword !== form.confirmPassword) {
    ElMessage.warning('两次输入的新密码不一致')
    return
  }

  busy.value = true
  try {
    await auth.changePassword(form.oldPassword, form.newPassword)
    ElMessage.success('密码已修改')
    emit('update:visible', false)
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '修改失败')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <el-dialog
    :model-value="visible"
    :title="title"
    width="440px"
    :close-on-click-modal="closable"
    :close-on-press-escape="closable"
    :show-close="closable"
    @update:model-value="emit('update:visible', $event)"
  >
    <el-alert
      v-if="props.forced"
      type="warning"
      :closable="false"
      show-icon
      title="初始密码由系统生成并写入启动日志，必须更换后才能继续使用。"
      style="margin-bottom: 14px"
    />

    <el-alert
      v-else-if="!isLocalAccount"
      type="info"
      :closable="false"
      show-icon
      title="域账户的密码请在域控制器上修改，本系统不保存域密码。"
      style="margin-bottom: 14px"
    />

    <el-form :model="form" label-width="86px" @submit.prevent>
      <el-form-item label="当前密码">
        <el-input v-model="form.oldPassword" type="password" show-password autocomplete="current-password" />
      </el-form-item>

      <el-form-item label="新密码">
        <el-input
          v-model="form.newPassword"
          type="password"
          show-password
          placeholder="至少 8 位"
          autocomplete="new-password"
        />
      </el-form-item>

      <el-form-item label="确认新密码">
        <el-input
          v-model="form.confirmPassword"
          type="password"
          show-password
          autocomplete="new-password"
          @keyup.enter="submit"
        />
      </el-form-item>
    </el-form>

    <template #footer>
      <el-button v-if="closable" @click="close">取消</el-button>
      <el-button type="primary" :loading="busy" @click="submit">确定</el-button>
    </template>
  </el-dialog>
</template>
