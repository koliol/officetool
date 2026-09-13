<script setup>
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useAppStore } from './store'
import ProjectTree from './components/ProjectTree.vue'
import FilePanel from './components/FilePanel.vue'
import RulePanel from './components/RulePanel.vue'

const store = useAppStore()
const activeTab = ref('templates')
const keyword = ref('')
const initError = ref('')

onMounted(async () => {
  try {
    await store.init()
  } catch (error) {
    initError.value = error.friendlyMessage || '初始化失败'
  }
})

const scopeText = computed(() => {
  if (!store.currentProject) return '未选择项目'
  return store.currentCheck
    ? `${store.currentProject} / ${store.currentCheck}`
    : `${store.currentProject}（全部检项）`
})

function onGlobalSearch() {
  // 全局搜索与面板内关键字共用同一条件
  ElMessage.info(keyword.value ? `按关键字查找：${keyword.value}` : '已清空关键字')
}

function togglePlugin() {
  const next = !store.pluginConfirmed
  store.setPluginConfirmed(next)
  ElMessage.success(next ? '已标记本机安装插件，点击文件将直接调用本机 Office' : '已取消插件标记')
}

function downloadPlugin() {
  const url = store.config?.pluginDownloadUrl
  if (!url) {
    ElMessage.warning('未配置插件下载地址')
    return
  }
  window.open(url, '_blank')
  ElMessage.info('若内网未分发安装包，请联系管理员获取')
}
</script>

<template>
  <div class="app-shell">
    <div class="app-header">
      <div class="brand">Office 文档管理工具</div>
      <el-input
        v-model="keyword"
        class="global-search"
        placeholder="全局搜索文件名"
        clearable
        @keyup.enter="onGlobalSearch"
      >
        <template #prefix>
          <el-icon><Search /></el-icon>
        </template>
      </el-input>

      <div class="header-right">
        <el-tag :type="store.pluginConfirmed ? 'success' : 'info'" size="small" effect="dark">
          {{ store.pluginConfirmed ? '插件已安装' : '插件未标记' }}
        </el-tag>
        <el-button size="small" @click="togglePlugin">
          {{ store.pluginConfirmed ? '取消标记' : '标记已安装' }}
        </el-button>
        <el-button size="small" type="primary" plain @click="downloadPlugin">下载插件</el-button>
      </div>
    </div>

    <div class="app-body" style="display: flex">
      <div class="app-aside" style="width: 300px">
        <ProjectTree />
      </div>

      <div class="app-main" style="flex: 1">
        <el-alert v-if="initError" type="error" :closable="false" show-icon :title="initError" style="margin-bottom: 12px" />

        <el-alert
          v-if="!store.pluginConfirmed"
          type="info"
          :closable="false"
          show-icon
          :title="store.pluginHint"
          description="点击列表中的文件后，可复制共享路径到资源管理器打开；安装桌面插件后可直接唤起本机 Office。"
          style="margin-bottom: 12px"
        />

        <el-tabs v-model="activeTab" class="file-tabs">
          <el-tab-pane label="模板列表" name="templates">
            <FilePanel kind="templates" :keyword="keyword" :scope-text="scopeText" />
          </el-tab-pane>
          <el-tab-pane label="文档列表" name="documents">
            <FilePanel kind="documents" :keyword="keyword" :scope-text="scopeText" />
          </el-tab-pane>
          <!-- 提取规则与模板/文档同一套「项目/检项」目录结构 -->
          <el-tab-pane label="提取规则列表" name="rules">
            <RulePanel :keyword="keyword" :scope-text="scopeText" />
          </el-tab-pane>
        </el-tabs>
      </div>
    </div>
  </div>
</template>
