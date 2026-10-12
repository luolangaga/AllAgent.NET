<template>
  <n-card class="provider-card">
    <template #header>
      <div class="provider-card-header">
        <span class="provider-logo">{{ providerLogo }}</span>
        <div>
          <div class="provider-name">{{ config.displayName }}</div>
          <div class="provider-url">{{ config.baseUrl }}</div>
        </div>
        <n-tag :type="config.isEnabled ? 'success' : 'default'" style="margin-left:auto">
          {{ config.isEnabled ? '启用' : '禁用' }}
        </n-tag>
      </div>
    </template>
    <div class="provider-model">模型: {{ config.defaultModel }}</div>
    <div class="provider-meta">
      Temperature: {{ config.temperature }} · Max Tokens: {{ config.maxTokens }}
    </div>
    <template #footer>
      <div class="provider-footer">
        <n-button
          size="small"
          :type="testStatus === 'error' ? 'error' : testStatus === 'ok' ? 'success' : 'default'"
          :loading="testStatus === 'testing'"
          @click="emit('test')"
        >
          {{ testStatus === 'ok' ? '✓ 连接正常' : testStatus === 'error' ? '✗ 连接失败' : '测试连接' }}
        </n-button>
        <n-button text size="small" @click="emit('edit')">
          <template #icon>
            <svg width="14" height="14" viewBox="0 0 14 14" fill="none">
              <path d="M9.5 2.5l2 2-7 7H2.5v-2l7-7z" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
            </svg>
          </template>
        </n-button>
        <n-button text size="small" type="error" @click="emit('delete')">
          <template #icon>
            <svg width="14" height="14" viewBox="0 0 14 14" fill="none">
              <path d="M2 4h10M5 4V2.5h4V4M5.5 6.5v4M8.5 6.5v4M3 4l.8 7.5h6.4L11 4" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round"/>
            </svg>
          </template>
        </n-button>
      </div>
    </template>
  </n-card>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { NCard, NTag, NButton } from 'naive-ui'
import type { ApiConfig } from '@/types'

const props = defineProps<{
  config: ApiConfig
  testStatus: 'idle' | 'testing' | 'ok' | 'error'
}>()

const emit = defineEmits<{ edit: []; delete: []; test: [] }>()

const providerLogo = computed(() => ({
  openai: '🟢', anthropic: '🟠', gemini: '🔵', ollama: '🦙',
}[props.config.providerId] ?? '🔌'))
</script>

<style scoped>
.provider-card-header {
  display: flex;
  align-items: center;
  gap: 12px;
}
.provider-logo { font-size: 28px; }
.provider-name {
  font-weight: 600;
  font-size: 16px;
  color: var(--n-text-color);
}
.provider-url {
  font-size: 12px;
  color: var(--n-text-color-3);
  max-width: 260px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.provider-model {
  font-size: 14px;
  font-weight: 500;
  color: var(--n-text-color-2);
  margin-bottom: 4px;
}
.provider-meta {
  font-size: 13px;
  color: var(--n-text-color-3);
  margin-bottom: 8px;
}
.provider-footer {
  display: flex;
  align-items: center;
  gap: 4px;
}
</style>
