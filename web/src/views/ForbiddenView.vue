<script setup>
import { useAuthStore } from '../auth'
import { navigate } from '../router'

/**
 * 无权限页。
 *
 * 明确区分「没有权限」与「资源不存在」：读不到东西时后端刻意返回 404
 * （避免泄露项目清单），但**页面级的**管理入口是明确按角色隐藏的，
 * 这里如实告诉用户原因与下一步，而不是让他去猜。
 */
const auth = useAuthStore()
</script>

<template>
  <div class="forbidden">
    <el-result icon="warning" title="没有访问权限">
      <template #sub-title>
        <div class="lines">
          <p>「用户管理」「用户组」「授权配置」只对系统管理员开放。</p>
          <p v-if="auth.userName">
            当前登录：<strong>{{ auth.displayLabel }}</strong>（{{ auth.userName }}）
          </p>
          <p>如需权限，请联系管理员把你加入对应的用户组。</p>
        </div>
      </template>

      <template #extra>
        <el-button type="primary" @click="navigate('/')">回到工作台</el-button>
        <el-button @click="auth.logout()">退出登录</el-button>
      </template>
    </el-result>
  </div>
</template>

<style scoped>
.forbidden {
  height: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
}

.lines p {
  margin: 4px 0;
}
</style>
