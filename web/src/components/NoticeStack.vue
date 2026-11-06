<script setup>
import { notices, dismissNotice } from '../ui/notices'

/**
 * 右下角悬浮通知栈。
 *
 * 之所以不用 el-alert：内联提示会占文档流，出现/消失时整页会跳一下，
 * 而且一旦有多条就变成一堵墙。悬浮卡片不参与布局，可以叠放、可以关闭。
 */
const ICONS = {
  info: 'InfoFilled',
  success: 'CircleCheckFilled',
  warning: 'WarningFilled',
  error: 'CircleCloseFilled',
}
</script>

<template>
  <transition-group name="notice" tag="div" class="notice-stack">
    <article
      v-for="item in notices"
      :key="item.id"
      class="notice-card"
      :class="`is-${item.type}`"
      role="status"
    >
      <span class="notice-accent" aria-hidden="true"></span>

      <el-icon class="notice-icon" :size="18">
        <component :is="ICONS[item.type] || ICONS.info" />
      </el-icon>

      <div class="notice-body">
        <p class="notice-title">{{ item.title }}</p>
        <p v-if="item.description" class="notice-desc">{{ item.description }}</p>

        <el-button
          v-if="item.actionLabel"
          class="notice-action"
          size="small"
          type="primary"
          plain
          @click="item.onAction && item.onAction()"
        >
          {{ item.actionLabel }}
        </el-button>
      </div>

      <button class="notice-close" type="button" title="关闭提示" @click="dismissNotice(item.id)">
        <el-icon :size="14"><Close /></el-icon>
      </button>
    </article>
  </transition-group>
</template>

<style scoped>
.notice-stack {
  position: fixed;
  right: 20px;
  bottom: 20px;
  z-index: 2100;
  display: flex;
  flex-direction: column;
  gap: 10px;
  width: min(380px, calc(100vw - 40px));
  pointer-events: none;
}

.notice-card {
  position: relative;
  display: flex;
  gap: 10px;
  padding: 12px 36px 12px 16px;
  background: var(--ot-surface);
  border: 1px solid var(--ot-border);
  border-radius: 8px;
  box-shadow: var(--ot-shadow-lg);
  pointer-events: auto;
  overflow: hidden;
}

/* 左侧色条：颜色即语义，比小图标更快被扫到 */
.notice-accent {
  position: absolute;
  left: 0;
  top: 0;
  bottom: 0;
  width: 4px;
  background: var(--ot-accent, var(--el-color-primary));
}

.is-info {
  --ot-accent: var(--ot-info);
}

.is-success {
  --ot-accent: var(--ot-success);
}

.is-warning {
  --ot-accent: var(--ot-warning);
}

.is-error {
  --ot-accent: var(--ot-danger);
}

.notice-icon {
  margin-top: 1px;
  color: var(--ot-accent);
  flex: none;
}

.notice-body {
  min-width: 0;
}

.notice-title {
  margin: 0;
  font-size: 13px;
  font-weight: 700;
  color: var(--ot-text);
  line-height: 1.5;
}

.notice-desc {
  margin: 4px 0 0;
  font-size: 12px;
  line-height: 1.6;
  color: var(--ot-text-muted);
}

.notice-action {
  margin-top: 8px;
}

.notice-close {
  position: absolute;
  top: 8px;
  right: 8px;
  width: 22px;
  height: 22px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  padding: 0;
  border: none;
  border-radius: 4px;
  background: transparent;
  color: var(--ot-text-faint);
  cursor: pointer;
}

.notice-close:hover {
  background: var(--ot-surface-2);
  color: var(--ot-text);
}

.notice-enter-active,
.notice-leave-active {
  transition: opacity 0.18s ease, transform 0.18s ease;
}

.notice-enter-from,
.notice-leave-to {
  opacity: 0;
  transform: translateY(8px);
}
</style>
