<script setup>
/**
 * 页面标题栏。
 *
 * 之前每个页面直接从筛选条开始，进来第一眼不知道这是哪一页 ——
 * 导航高亮是唯一的线索，而它离内容很远。统一在这里给标题与一句说明，
 * 右侧留给该页的主操作。
 */
defineProps({
  title: { type: String, required: true },
  subtitle: { type: String, default: '' },
})
</script>

<template>
  <div class="page-head">
    <div>
      <h1 class="page-head__title">{{ title }}</h1>
      <p v-if="subtitle" class="page-head__sub">{{ subtitle }}</p>
    </div>

    <div v-if="$slots.actions" class="page-head__actions">
      <slot name="actions" />
    </div>
  </div>
</template>
