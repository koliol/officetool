<script setup>
import { computed, ref, watch } from 'vue'
import { ElMessage } from '../ui/message'
import { useAppStore } from '../store'
import { useAuthStore } from '../auth'
import { pushNotice } from '../ui/notices'
import ProjectTree from '../components/ProjectTree.vue'
import FilePanel from '../components/FilePanel.vue'
import RulePanel from '../components/RulePanel.vue'

/**
 * 文档工作台：左侧目录 + 右侧模板/文档/规则列表。
 *
 * 页面形态按**权限等级**自适应，而不是按写死的角色名：
 * 一线执行人员只有只读权时，看不到上传/新建/删除，也看不到提取规则页签；
 * 维护者拿到编辑权后这些入口自动出现。同一套代码，不同人看到不同的界面。
 */
const store = useAppStore()
const auth = useAuthStore()

const activeTab = ref('templates')
const keyword = ref('')

/** 当前范围：项目 / 检项 / 全部检项 —— 主区域顶部一直可见，避免"在改哪个目录"要靠猜 */
const scope = computed(() => {
  if (!store.currentProject) {
    return { project: '', check: '', all: false, empty: true }
  }

  return {
    project: store.currentProject,
    check: store.currentCheck,
    all: !store.currentCheck,
    empty: false,
  }
})

/**
 * 是否露出「提取规则」页签。
 *
 * 判断依据是**有没有规则维护权**，而不是「有没有可见规则」：
 * 规则文件在本界面里只有维护入口（上传/改备注/复制/删除），
 * 内容本身由提取脚本消费、不在页面上展示。给只读用户摆一张
 * 全是灰按钮的表，除了增加理解成本没有别的效果。
 */
const showRulesTab = computed(() => auth.canAnyRuleManage)

const searchPlaceholder = computed(
  () => `在${activeTab.value === 'rules' ? '规则' : '文件'}名中查找`,
)

function onGlobalSearch() {
  if (!keyword.value) {
    ElMessage.info('已清空关键字')
  }
}

function togglePlugin() {
  const next = !store.pluginConfirmed
  store.setPluginConfirmed(next)
  ElMessage.success(next ? '已标记本机安装插件，点击文件将直接调用本机 Office' : '已取消插件标记')
}

function downloadPlugin() {
  const url = store.config?.pluginDownloadUrl
  if (!url) {
    ElMessage.warning('未配置插件下载地址，请联系管理员获取')
    return
  }
  window.open(url, '_blank')
}

/**
 * 状态类提示改为悬浮通知（原先三条 el-alert 内联在页面顶部，
 * 一进来就把列表往下顶，而且出现/消失时整页会跳）。
 * 带 key：用户关掉后本次会话不再打扰，刷新或换账号会重新提示一次。
 */
function syncNotices() {
  if (!store.pluginConfirmed) {
    pushNotice({
      key: 'plugin-missing',
      type: 'info',
      title: '未检测到桌面插件',
      description:
        '不装也能用：点击文件会给出共享路径，复制到资源管理器即可打开。装好后点击文件可直接唤起本机 Office。',
      actionLabel: '本机已安装，标记一下',
      onAction: togglePlugin,
    })
  }

  if (auth.authEnabled && !auth.isSystemAdmin && auth.canAnyManage === false) {
    pushNotice({
      key: 'readonly-scope',
      type: 'warning',
      title: '当前为只读权限',
      description: '可以浏览模板、生成文档、打开文件；上传、删除等入口要管理员授权后才会出现。',
    })
  }
}

watch(
  () => [store.pluginConfirmed, auth.accessLoaded, auth.isSystemAdmin, auth.canAnyManage],
  syncNotices,
  { immediate: true },
)
</script>

<template>
  <div class="workspace">
    <div class="page-head">
      <div>
        <h1 class="page-head__title">文档工作台</h1>
        <p class="page-head__sub">模板复用 · 文档生成 · 提取规则维护</p>
      </div>

      <div class="scope-bar" :class="{ 'is-empty': scope.empty }">
        <el-icon><FolderOpened /></el-icon>
        <template v-if="scope.empty">未选择目录</template>
        <template v-else>
          <span>{{ scope.project }}</span>
          <span class="scope-sep">/</span>
          <span v-if="scope.check">{{ scope.check }}</span>
          <span v-else class="scope-tag">全部检项</span>
        </template>
      </div>

      <div class="page-head__actions">
        <el-input
          v-model="keyword"
          class="global-search"
          :placeholder="searchPlaceholder"
          clearable
          @keyup.enter="onGlobalSearch"
          @clear="onGlobalSearch"
        >
          <template #prefix>
            <el-icon><Search /></el-icon>
          </template>
        </el-input>

        <el-tag :type="store.pluginConfirmed ? 'success' : 'info'" size="small" effect="dark">
          {{ store.pluginConfirmed ? '插件已就绪' : '插件未标记' }}
        </el-tag>

        <el-button size="small" type="primary" plain @click="downloadPlugin">
          <el-icon><Download /></el-icon>&nbsp;下载插件
        </el-button>
      </div>
    </div>

    <div class="workspace-body">
      <aside class="workspace-aside ot-panel">
        <ProjectTree />
      </aside>

      <main class="workspace-main ot-panel">
        <el-tabs v-model="activeTab" class="file-tabs">
          <el-tab-pane label="模板列表" name="templates">
            <FilePanel kind="templates" :keyword="keyword" :scope-text="scope.project" />
          </el-tab-pane>

          <el-tab-pane label="文档列表" name="documents">
            <FilePanel kind="documents" :keyword="keyword" :scope-text="scope.project" />
          </el-tab-pane>

          <!-- 提取规则与模板/文档同一套「项目/检项」目录结构 -->
          <el-tab-pane v-if="showRulesTab" label="提取规则" name="rules">
            <RulePanel :keyword="keyword" :scope-text="scope.project" />
          </el-tab-pane>
        </el-tabs>
      </main>
    </div>
  </div>
</template>

<style scoped>
.workspace {
  height: 100%;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.workspace-body {
  flex: 1;
  display: flex;
  gap: 14px;
  min-height: 0;
}

.workspace-aside {
  width: 316px;
  flex: none;
  overflow: hidden;
}

.workspace-main {
  flex: 1;
  min-width: 0;
  overflow: hidden;
}

.global-search {
  width: 260px;
}

/* 页签栏与面板留白对齐：让"卡片"内边距看起来一致 */
.file-tabs {
  height: 100%;
  display: flex;
  flex-direction: column;
}

.file-tabs :deep(.el-tabs__header) {
  margin: 0;
  padding: 0 16px;
  flex: none;
}

.file-tabs :deep(.el-tabs__content) {
  flex: 1;
  min-height: 0;
}

.file-tabs :deep(.el-tab-pane) {
  height: 100%;
}
</style>
