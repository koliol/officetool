<script setup>
import { computed, reactive, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { documentApi, ruleApi } from '../api'
import { useAppStore } from '../store'

/**
 * 「复制到…」目标路径选择弹窗。
 *
 * 文档与提取规则共用：两者都要求用户**显式选择目标项目/检项**，
 * 而不是直接在当前文件夹里生成一份副本。
 *
 * 目标只提交项目/检项**编码**，由服务端按配置拼路径 ——
 * 前端不构造路径，也就无法绕出存储根。
 */
const props = defineProps({
  visible: { type: Boolean, default: false },
  /** 'document' | 'rule' */
  kind: { type: String, required: true },
  project: { type: String, default: '' },
  check: { type: String, default: '' },
  fileNames: { type: Array, default: () => [] },
})

const emit = defineEmits(['update:visible', 'copied'])

const store = useAppStore()

const busy = ref(false)
const target = reactive({ project: '', check: '', newFileName: '' })

const isDocument = computed(() => props.kind === 'document')

const title = computed(() => (isDocument.value ? '复制文档到…' : '复制提取规则到…'))

const scopeText = computed(() =>
  props.project ? (props.check ? `${props.project} / ${props.check}` : props.project) : '未选择',
)

/** 项目/检项在左侧树里已预取，这里直接复用，不再多发一次请求 */
const targetChecks = computed(() => {
  const found = store.projects.find((p) => p.name === target.project)
  return found?.checks || []
})

const canSubmit = computed(() => Boolean(target.project && target.check) && !busy.value)

/** 目标与源完全相同：后端会拒绝，这里提前给出更清楚的提示 */
const sameAsSource = computed(
  () => target.project === props.project && target.check === props.check,
)

watch(
  () => props.visible,
  (open) => {
    if (!open) return
    target.project = ''
    target.check = ''
    target.newFileName = ''
  },
)

watch(
  () => target.project,
  () => {
    target.check = ''
  },
)

function close() {
  emit('update:visible', false)
}

async function submit() {
  if (!canSubmit.value) return

  if (sameAsSource.value) {
    ElMessage.warning('目标文件夹与源文件夹相同，请选择不同的项目或检项。')
    return
  }

  if (!props.fileNames.length) {
    ElMessage.warning('没有选择要复制的文件。')
    return
  }

  busy.value = true
  try {
    const result = isDocument.value
      ? await documentApi.copy({
          project: props.project,
          check: props.check,
          fileName: props.fileNames[0],
          targetProject: target.project,
          targetCheck: target.check,
          newFileName: target.newFileName.trim() || null,
        })
      : await ruleApi.copy({
          project: props.project,
          check: props.check,
          fileNames: props.fileNames,
          targetProject: target.project,
          targetCheck: target.check,
        })

    const where = `${result.targetProject} / ${result.targetCheck}`

    if (result.copied.length) {
      ElMessage.success(
        `已复制 ${result.copied.length} 个文件到 ${where}：${result.copied.join('、')}`,
      )
    }

    if (result.skipped.length) {
      // 批量复制允许部分成功：目标已存在的明确回报，不静默覆盖
      ElMessage.warning({
        message: `以下 ${result.skipped.length} 个文件在目标已存在，已跳过：${result.skipped.join('、')}`,
        duration: 6000,
        showClose: true,
      })
    }

    store.bumpDataVersion()
    emit('copied', result)
    close()
  } catch (error) {
    ElMessage.error(error.friendlyMessage || '复制失败')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <el-dialog
    :model-value="visible"
    :title="title"
    width="560px"
    @update:model-value="emit('update:visible', $event)"
  >
    <el-form label-width="90px" @submit.prevent>
      <el-form-item label="来源">
        <el-input :model-value="scopeText" disabled />
      </el-form-item>

      <el-form-item label="要复制的">
        <div class="copy-files">
          <el-tag v-for="name in fileNames" :key="name" size="small" type="info">{{ name }}</el-tag>
          <span v-if="!fileNames.length" style="color: #909399; font-size: 13px">未选择文件</span>
        </div>
      </el-form-item>

      <el-divider content-position="left">复制到</el-divider>

      <el-form-item label="目标项目">
        <el-select v-model="target.project" placeholder="请选择项目" style="width: 100%">
          <el-option v-for="p in store.projects" :key="p.name" :label="p.name" :value="p.name" />
        </el-select>
      </el-form-item>

      <el-form-item label="目标检项">
        <el-select
          v-model="target.check"
          placeholder="请先选择项目"
          :disabled="!target.project"
          style="width: 100%"
        >
          <el-option v-for="c in targetChecks" :key="c.name" :label="c.name" :value="c.name" />
        </el-select>
        <div v-if="target.project && !targetChecks.length" class="hint">
          该项目下还没有检项，请先在左侧树里新建检项。
        </div>
      </el-form-item>

      <el-form-item v-if="isDocument && fileNames.length === 1" label="新文件名">
        <el-input v-model="target.newFileName" placeholder="留空沿用原名（重名自动加 _副本）" clearable />
      </el-form-item>

      <div v-if="sameAsSource" class="hint warn">
        目标与来源相同，请选择不同的项目或检项。
      </div>

      <div v-else class="hint">
        <template v-if="isDocument">
          目标已有同名文件时会自动命名为 <code>原名_副本.ext</code>，不会覆盖。
        </template>
        <template v-else>
          规则复制时<strong>备注会一并带过去</strong>；目标已存在同名的规则会被跳过并单独提示，不会覆盖。
        </template>
      </div>
    </el-form>

    <template #footer>
      <el-button @click="close">取消</el-button>
      <el-button type="primary" :loading="busy" :disabled="!canSubmit" @click="submit">
        确认复制
      </el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.copy-files {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  max-height: 96px;
  overflow-y: auto;
}

.hint {
  color: #909399;
  font-size: 12px;
  padding-left: 90px;
  line-height: 1.6;
}

.hint.warn {
  color: #e6a23c;
}
</style>
