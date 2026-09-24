<script setup>
import { onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { useAuthStore } from '../auth'
import { navigate } from '../router'

/**
 * 登录页。两条通道并列摆在用户面前：
 *   1. 域账号一键登录（Windows 集成认证，免输密码）
 *   2. 本地账户表单（域外机器、手机、keytab 过期时的兜底）
 *
 * 之所以不「自动尝试 SSO」：Negotiate 失败时后端返回的是一个没有内容的 401，
 * 浏览器会停在一张白页上。让用户主动点，失败时能回到本页并看到原因，
 * 比自动跳走再白屏好得多。
 */
const auth = useAuthStore()

const form = reactive({ userName: '', password: '' })
const busy = ref(false)
const ssoHint = ref(false)

onMounted(() => {
  // 上一轮点了「域账号登录」但没成功（多半是 DNS / keytab / 非域机器）
  ssoHint.value = auth.consumeSsoFailure()
})

function loginSso() {
  auth.goSso()
}

async function submit() {
  if (!form.userName.trim() || !form.password) {
    ElMessage.warning('请输入用户名与密码')
    return
  }

  busy.value = true
  try {
    await auth.login(form.userName.trim(), form.password)
    navigate('/', { replace: true })
  } catch (error) {
    // 后端刻意不区分「用户不存在」与「密码错误」，这里照原样展示
    ElMessage.error(error.friendlyMessage || '登录失败')
  } finally {
    busy.value = false
  }
}

async function retryDetect() {
  busy.value = true
  try {
    await auth.bootstrap()
    if (auth.authenticated) {
      navigate('/', { replace: true })
      return
    }
    ElMessage.warning('服务端仍要求登录')
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '连接失败')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="login-page">
    <div class="login-card">
      <div class="login-head">
        <div class="login-title">Office 文档管理工具</div>
        <div class="login-sub">模板与文档的统一管理入口</div>
      </div>

      <el-alert
        v-if="!auth.authEnabled"
        type="success"
        :closable="false"
        show-icon
        title="服务端未启用鉴权"
        description="当前为内网可信形态，无需登录。"
        style="margin-bottom: 16px"
      />

      <el-alert
        v-else-if="ssoHint"
        type="warning"
        :closable="false"
        show-icon
        title="域认证未能完成"
        description="可能是本机未加入域、浏览器未把本站视为内网站点，或内网 DNS 解析不到服务器。可改用下方本地账户登录，或联系管理员。"
        style="margin-bottom: 16px"
      />

      <template v-if="auth.authEnabled">
        <el-button
          type="primary"
          size="large"
          class="sso-button"
          :loading="busy"
          @click="loginSso"
        >
          <el-icon><Lock /></el-icon>&nbsp;使用域账号登录（免密码）
        </el-button>

        <div class="login-divider">
          <span>或使用本地账户</span>
        </div>

        <el-form :model="form" label-position="top" @submit.prevent="submit">
          <el-form-item label="用户名">
            <el-input
              v-model="form.userName"
              size="large"
              placeholder="本地账户登录名"
              autocomplete="username"
              @keyup.enter="submit"
            />
          </el-form-item>

          <el-form-item label="密码">
            <el-input
              v-model="form.password"
              type="password"
              size="large"
              placeholder="密码"
              show-password
              autocomplete="current-password"
              @keyup.enter="submit"
            />
          </el-form-item>

          <el-button type="primary" size="large" class="local-button" :loading="busy" @click="submit">
            登录
          </el-button>
        </el-form>
      </template>

      <el-button v-else type="primary" size="large" class="sso-button" :loading="busy" @click="retryDetect">
        进入系统
      </el-button>

      <div class="login-foot">
        忘记密码请联系管理员重置；域账户密码请在域控制器上修改。
      </div>
    </div>
  </div>
</template>

<style scoped>
.login-page {
  height: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
  background: #1f2d3d;
}

.login-card {
  width: 400px;
  background: #fff;
  border-radius: 8px;
  padding: 32px 32px 24px;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.24);
}

.login-head {
  text-align: center;
  margin-bottom: 24px;
}

.login-title {
  font-size: 20px;
  font-weight: 600;
  color: #1f2d3d;
}

.login-sub {
  margin-top: 6px;
  font-size: 13px;
  color: #909399;
}

.sso-button,
.local-button {
  width: 100%;
}

.login-divider {
  display: flex;
  align-items: center;
  color: #c0c4cc;
  font-size: 12px;
  margin: 20px 0 16px;
}

.login-divider::before,
.login-divider::after {
  content: '';
  flex: 1;
  height: 1px;
  background: #ebeef5;
}

.login-divider span {
  padding: 0 10px;
}

.login-foot {
  margin-top: 20px;
  font-size: 12px;
  color: #a8abb2;
  text-align: center;
  line-height: 1.6;
}
</style>
