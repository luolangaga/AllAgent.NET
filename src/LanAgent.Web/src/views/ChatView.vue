<template>
  <div class="chat-view">
    <!-- Empty state -->
    <div v-if="!convStore.activeId" class="empty-state">
      <div class="empty-icon">💬</div>
      <h2>开始一段对话</h2>
      <p>选择一个已有对话，或创建新对话开始使用 AI Agent</p>
      <n-button type="primary" @click="newChat">新建对话</n-button>
    </div>

    <template v-else>
      <!-- Chat header -->
      <div class="chat-header">
        <div class="chat-header-title">
          <span>{{ convStore.active?.title ?? '对话' }}</span>
        </div>
        <div class="chat-header-actions">
          <n-popover trigger="click" :width="240">
            <template #trigger>
              <n-tag :type="thinkingEnabled ? 'warning' : 'default'" size="small" style="cursor:pointer">
                {{ thinkingEnabled ? '🧠 思考中' : '💡 普通' }}
              </n-tag>
            </template>
            <div style="display:flex;flex-direction:column;gap:12px;padding:4px">
              <div style="display:flex;align-items:center;justify-content:space-between">
                <span style="font-size:14px;font-weight:500">深度思考模式</span>
                <n-switch v-model:value="thinkingEnabled" size="small" />
              </div>
              <div v-if="thinkingEnabled">
                <div style="font-size:12px;color:var(--n-text-color-3);margin-bottom:4px">思考深度: {{ thinkingBudget }}</div>
                <n-slider v-model:value="thinkingBudget" :min="512" :max="32000" :step="512" />
              </div>
              <span style="font-size:12px;color:var(--n-text-color-3)">开启后 AI 会先深度思考再回答，适合复杂推理任务</span>
            </div>
          </n-popover>
          <n-select
            v-model:value="selectedApiConfigId"
            :options="apiConfigOptions"
            placeholder="选择 API 配置"
            size="small"
            style="min-width:220px"
            :disabled="!!convStore.streamingMessageId"
          />
          <n-button
            v-if="convStore.streamingMessageId"
            type="error"
            size="small"
            @click="convStore.cancelStream()"
          >
            <template #icon>
              <svg width="14" height="14" viewBox="0 0 14 14" fill="none">
                <rect x="2" y="2" width="10" height="10" rx="2" fill="currentColor"/>
              </svg>
            </template>
            停止
          </n-button>
        </div>
      </div>

      <!-- Message list -->
      <div class="message-list" ref="messageListEl">
        <div v-if="convStore.isLoading" class="loading-messages">
          <n-skeleton v-for="i in 2" :key="i" text :repeat="2" style="margin-bottom:12px" />
        </div>
        <template v-else>
          <TransitionGroup name="msg">
            <MessageBubble
              v-for="msg in visibleMessages"
              :key="msg.id"
              :message="msg"
              :streaming="msg.id === convStore.streamingMessageId"
            />
          </TransitionGroup>
          <div ref="bottomAnchorEl" style="height:1px" />
        </template>
      </div>

      <!-- Input bar -->
      <div class="input-bar">
        <div class="input-bar-inner">
          <n-input
            v-model:value="draft"
            type="textarea"
            :autosize="{ minRows: 1, maxRows: 6 }"
            placeholder="发送消息… (Enter 发送，Shift+Enter 换行)"
            :disabled="!!convStore.streamingMessageId"
            @keydown.enter.exact.prevent="send"
            @keydown.enter.shift.exact.stop
          />
          <n-button
            circle
            type="primary"
            :disabled="!draft.trim() || !!convStore.streamingMessageId || !selectedApiConfigId"
            @click="send"
            title="发送 (Enter)"
          >
            <template #icon>
              <svg width="18" height="18" viewBox="0 0 18 18" fill="none">
                <path d="M2 9l14-7-7 14V9H2z" fill="currentColor"/>
              </svg>
            </template>
          </n-button>
        </div>
        <div class="input-hint" v-if="!selectedApiConfigId">
          请先在 <RouterLink to="/api-configs">API 配置</RouterLink> 中添加并启用一个 API
        </div>
      </div>
    </template>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, watch, nextTick } from 'vue'
import { useRoute, useRouter, RouterLink } from 'vue-router'
import { NButton, NSelect, NInput, NSkeleton, NTag, NSwitch, NSlider, NPopover, useMessage } from 'naive-ui'
import { useConversationsStore } from '@/stores/conversations'
import { useApiConfigsStore } from '@/stores/apiConfigs'
import { useSettingsStore } from '@/stores/settings'
import MessageBubble from '@/components/MessageBubble.vue'

const convStore = useConversationsStore()
const apiStore = useApiConfigsStore()
const settingsStore = useSettingsStore()
const route = useRoute()
const router = useRouter()
const message = useMessage()

const draft = ref('')
const messageListEl = ref<HTMLElement>()
const bottomAnchorEl = ref<HTMLElement>()
const selectedApiConfigId = ref('')
const thinkingEnabled = ref(false)
const thinkingBudget = ref(8000)

const visibleMessages = computed(() =>
  convStore.messages.filter(m => m.role !== 'system')
)

const apiConfigOptions = computed(() =>
  apiStore.configs
    .filter(c => c.isEnabled)
    .map(c => ({ label: `${c.displayName} · ${c.defaultModel}`, value: c.id }))
)

// Pick default API config
watch(() => apiStore.configs, (configs) => {
  if (!selectedApiConfigId.value && configs.length > 0) {
    const preferred = configs.find(c => c.isEnabled && c.providerId === settingsStore.defaultProviderId)
    selectedApiConfigId.value = preferred?.id ?? configs.find(c => c.isEnabled)?.id ?? ''
  }
}, { immediate: true })

// Load conversation from route param
watch(() => route.params.id, async (id) => {
  if (id && typeof id === 'string' && id !== convStore.activeId) {
    await convStore.selectConversation(id)
  }
}, { immediate: true })

// Auto-scroll on new messages
watch(
  () => [convStore.messages.length, convStore.streamingMessageId],
  async () => {
    await nextTick()
    bottomAnchorEl.value?.scrollIntoView({ behavior: 'smooth' })
  }
)

async function newChat() {
  const conv = await convStore.createConversation()
  router.push(`/chat/${conv.id}`)
}

async function send() {
  const text = draft.value.trim()
  if (!text || !selectedApiConfigId.value) return
  draft.value = ''
  try {
    await convStore.sendMessage(text, selectedApiConfigId.value, thinkingEnabled.value, thinkingBudget.value)
  } catch (e: any) {
    message.error(e.message ?? '发送失败')
  }
}
</script>

<style scoped>
.chat-view {
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: hidden;
}
.chat-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: var(--space-3) var(--space-5);
  border-bottom: 1px solid var(--n-border-color);
  flex-shrink: 0;
  height: 52px;
}
.chat-header-title {
  font-size: var(--font-size-400);
  font-weight: var(--font-weight-semibold);
  color: var(--n-text-color);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 400px;
}
.chat-header-actions {
  display: flex;
  align-items: center;
  gap: var(--space-3);
}
.message-list {
  flex: 1;
  overflow-y: auto;
  padding: var(--space-4) 0;
}
.loading-messages {
  padding: var(--space-6);
}
.input-bar {
  flex-shrink: 0;
  padding: var(--space-3) var(--space-4) var(--space-4);
  border-top: 1px solid var(--n-border-color);
}
.input-bar-inner {
  display: flex;
  align-items: flex-end;
  gap: var(--space-2);
}
.input-hint {
  font-size: var(--font-size-200);
  color: var(--n-text-color-3);
  margin-top: var(--space-2);
  text-align: center;
}
.input-hint a {
  color: var(--n-color-target);
  text-decoration: none;
}
.empty-icon { font-size: 48px; }
</style>
