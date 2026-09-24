<script setup>
import { computed, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { useAppStore } from '../store'
import { useAuthStore } from '../auth'
import ProjectTree from '../components/ProjectTree.vue'
import FilePanel from '../components/FilePanel.vue'
import RulePanel from '../components/RulePanel.vue'

/**
 * 文档工作台：左侧目录树 + 右侧模板/文档/规则列表。
 *
 * 页面形态按**权限等级**自适应，而不是按写死的角色名：
 * 一线执行人员只有只读权时，看不到上传/新建/删除，也看不到提取规则页签；
 * 维护者拿到编辑权后这些入口自动出现。同一套代码，不同人看到不同的界面。
 */
const store = useAppStore()
const auth = useAuthStore()

const activeTab = ref('templates')
const keyword = ref('')

const scopeText = computed(() => {
  if (!store.currentProject) return '未选择项目'
  if (!store.currentCheck) return `${store.currentProject}（全部检项）`
  return `${store.currentProject} / ${store.currentCheck}`
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

const searchPlaceholder = computed(() => `在${activeTab.value === 'rules' ? '提取规则' : '文件'}名中查找`)

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
</script>

<template>
  <div class="workspace">
    <div class="workspace-toolbar">
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

      <div class="toolbar-right">
        <el-tag :type="store.pluginConfirmed ? 'success' : 'info'" size="small" effect="dark">
          {{ store.pluginConfirmed ? '插件已安装' : '插件未标记' }}
        </el-tag>
        <el-button size="small" @click="togglePlugin">
          {{ store.pluginConfirmed ? '取消标记' : '标记已安装' }}
        </el-button>
        <el-button size="small" type="primary" plain @click="downloadPlugin">下载插件</el-button>
      </div>
    </div>

    <el-alert
      v-if="!store.pluginConfirmed"
      type="info"
      :closable="false"
      show-icon
      :title="store.pluginHint"
      description="点击列表中的文件后，可复制共享路径到资源管理器打开；安装桌面插件后可直接唤起本机 Office。"
      style="margin-bottom: 12px"
    />

    <!--
      只读用户看到的界面到这里就结束了：没有上传、没有新建、没有删除。
      这不是「藏起来」而是「不需要」——权限不足的按钮点了只会报 403，
      对一线人员来说那是纯粹的噪音。
    -->
    <el-alert
      v-if="auth.authEnabled && !auth.isSystemAdmin && auth.canAnyManage === false"
      type="info"
      :closable="false"
      show-icon
      title="你当前的权限为查看与生成文档"
      description="可浏览模板、生成文档、打开文件；上传、删除等入口需要管理员授权后才会出现。"
      style="margin-bottom: 12px"
    />

    <div class="workspace-body">
      <aside class="workspace-aside">
        <ProjectTree />
      </aside>

      <main class="workspace-main">
        <el-tabs v-model="activeTab" class="file-tabs">
          <el-tab-pane label="模板列表" name="templates">
            <FilePanel kind="templates" :keyword="keyword" :scope-text="scopeText" />
          </el-tab-pane>

          <el-tab-pane label="文档列表" name="documents">
            <FilePanel kind="documents" :keyword="keyword" :scope-text="scopeText" />
          </el-tab-pane>

          <!-- 提取规则与模板/文档同一套「项目/检项」目录结构 -->
          <el-tab-pane v-if="showRulesTab" label="提取规则列表" name="rules">
            <RulePanel :keyword="keyword" :scope-text="scopeText" />
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
  padding: 12px 16px 0;
  box-sizing: border-box;
}

.workspace-toolbar {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 12px;
}

.global-search {
  max-width: 360px;
}

.toolbar-right {
  margin-left: auto;
  display: flex;
  align-items: center;
  gap: 8px;
}

.workspace-body {
  flex: 1;
  display: flex;
  min-height: 0;
}

.workspace-aside {
  width: 300px;
  flex: none;
  border-right: 1px solid #e4e7ed;
  background: #fafafa;
  overflow: auto;
  padding: 12px;
  box-sizing: border-box;
}

.workspace-main {
  flex: 1;
  min-width: 0;
  padding: 0 0 12px 16px;
  overflow: auto;
}
</style>
