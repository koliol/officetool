<script setup>
import { computed, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { useAppStore } from '../store'
import AttachmentDialog from './AttachmentDialog.vue'

const props = defineProps({
  visible: { type: Boolean, default: false },
  file: { type: Object, default: null },
})

const emit = defineEmits(['update:visible'])

const store = useAppStore()

/** 附件弹窗（需求：上传附件入口在「打开文件」弹窗里也要有） */
const attachmentVisible = ref(false)

const protocolUrl = computed(() => (props.file ? store.buildProtocolUrl(props.file) : ''))

function close() {
  emit('update:visible', false)
}

async function copyPath() {
  if (!props.file) return
  try {
    await navigator.clipboard.writeText(props.file.accessPath)
    ElMessage.success('路径已复制，可粘贴到资源管理器打开')
  } catch {
    ElMessage.warning('浏览器拒绝剪贴板访问，请手动选中路径复制')
  }
}

function openWithPlugin() {
  if (!props.file) return
  // 触发自定义协议；浏览器会询问是否允许打开 officetool
  window.location.href = protocolUrl.value
  close()
}

function markInstalled() {
  store.setPluginConfirmed(true)
  openWithPlugin()
}
</script>

<template>
  <el-dialog
    :model-value="visible"
    title="打开文件"
    width="620px"
    @update:model-value="emit('update:visible', $event)"
  >
    <template v-if="file">
      <el-alert
        type="warning"
        :closable="false"
        show-icon
        title="未检测到桌面插件"
        description="可复制下方路径到资源管理器打开；若已安装插件，点击“用桌面插件打开”后浏览器会询问是否允许，选择允许即可。"
        style="margin-bottom: 14px"
      />

      <el-descriptions :column="1" border size="small" style="margin-bottom: 14px">
        <el-descriptions-item label="文件名">{{ file.fileName }}</el-descriptions-item>
        <el-descriptions-item label="项目 / 检项">
          {{ file.project }} / {{ file.check }}
        </el-descriptions-item>
        <el-descriptions-item label="相对路径">{{ file.relativePath }}</el-descriptions-item>
      </el-descriptions>

      <div style="margin-bottom: 6px; color: #606266; font-size: 13px">共享路径</div>
      <div class="unc-path">{{ file.accessPath }}</div>

      <div style="margin-top: 10px; color: #909399; font-size: 12px">
        协议链接：{{ protocolUrl }}
      </div>
    </template>

    <template #footer>
      <el-button @click="close">关闭</el-button>
      <el-button v-if="file" @click="attachmentVisible = true">
        <el-icon><Upload /></el-icon>&nbsp;上传附件
      </el-button>
      <el-button @click="copyPath">复制路径</el-button>
      <el-button type="primary" @click="openWithPlugin">用桌面插件打开</el-button>
      <el-button link type="primary" @click="markInstalled">
        我已安装插件，以后直接打开
      </el-button>
    </template>
  </el-dialog>

  <!-- 上传附件（与「打开文件」同一层级，目标文档即当前文件） -->
  <AttachmentDialog
    v-model:visible="attachmentVisible"
    :project="file?.project || ''"
    :check="file?.check || ''"
    :target-document="file"
  />
</template>
