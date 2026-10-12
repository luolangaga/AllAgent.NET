<template>
  <n-layout class="app-shell" has-sider>
    <!-- Sidebar -->
    <n-layout-sider
      bordered
      collapse-mode="width"
      :collapsed-width="64"
      :width="240"
      :collapsed="sidebarCollapsed"
      show-trigger="bar"
      @collapse="sidebarCollapsed = true"
      @expand="sidebarCollapsed = false"
    >
      <div class="sider-header">
        <span v-if="!sidebarCollapsed" class="app-title">LanAgent</span>
      </div>

      <n-button
        type="primary"
        block
        :size="sidebarCollapsed ? 'small' : 'medium'"
        class="new-chat-btn"
        @click="newChat"
      >
        <template #icon>
          <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
            <path d="M8 2v12M2 8h12" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/>
          </svg>
        </template>
        <span v-if="!sidebarCollapsed">新对话</span>
      </n-button>

      <div class="conv-list">
        <ConversationItem
          v-for="conv in convStore.conversations"
          :key="conv.id"
          :conversation="conv"
          :active="conv.id === convStore.activeId"
          :collapsed="sidebarCollapsed"
          @select="selectConv(conv.id)"
          @rename="convStore.renameConversation(conv.id, $event)"
          @delete="convStore.deleteConversation(conv.id)"
        />
      </div>

      <div class="sider-footer">
        <n-button
          text
          :size="sidebarCollapsed ? 'small' : 'medium'"
          @click="router.push('/skills')"
        >
          <template #icon>
            <svg width="18" height="18" viewBox="0 0 18 18" fill="none">
              <path d="M7 2.5A4.5 4.5 0 002.5 7c0 1.8 1.06 3.36 2.6 4.1L2.5 15.5h3l1.5-2.5h2l1.5 2.5h3l-2.6-4.4A4.5 4.5 0 007 2.5z" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
              <circle cx="12.5" cy="5.5" r="2" stroke="currentColor" stroke-width="1.2"/>
            </svg>
          </template>
          <span v-if="!sidebarCollapsed">工具</span>
        </n-button>
        <n-button
          text
          :size="sidebarCollapsed ? 'small' : 'medium'"
          @click="router.push('/api-configs')"
        >
          <template #icon>
            <svg width="18" height="18" viewBox="0 0 18 18" fill="none">
              <path d="M9 2a7 7 0 100 14A7 7 0 009 2z" stroke="currentColor" stroke-width="1.2"/>
              <path d="M5.5 9h7M9 5.5v7" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>
            </svg>
          </template>
          <span v-if="!sidebarCollapsed">API</span>
        </n-button>
        <n-button
          text
          :size="sidebarCollapsed ? 'small' : 'medium'"
          @click="router.push('/mcp-configs')"
        >
          <template #icon>
            <svg width="18" height="18" viewBox="0 0 18 18" fill="none">
              <path d="M3 3h5v5H3zM10 3h5v5h-5zM3 10h5v5H3zM10 10h5v5h-5z" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
            </svg>
          </template>
          <span v-if="!sidebarCollapsed">MCP</span>
        </n-button>
        <n-button
          text
          :size="sidebarCollapsed ? 'small' : 'medium'"
          @click="router.push('/settings')"
        >
          <template #icon>
            <svg width="18" height="18" viewBox="0 0 18 18" fill="none">
              <circle cx="9" cy="9" r="2.5" stroke="currentColor" stroke-width="1.2"/>
              <path d="M9 1.5v2M9 14.5v2M1.5 9h2M14.5 9h2M3.7 3.7l1.4 1.4M12.9 12.9l1.4 1.4M3.7 14.3l1.4-1.4M12.9 5.1l1.4-1.4" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>
            </svg>
          </template>
          <span v-if="!sidebarCollapsed">设置</span>
        </n-button>
      </div>
    </n-layout-sider>

    <!-- Main content -->
    <n-layout-content class="main-area">
      <RouterView />
    </n-layout-content>
  </n-layout>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { NLayout, NLayoutSider, NLayoutContent, NButton } from 'naive-ui'
import { useConversationsStore } from '@/stores/conversations'
import ConversationItem from './ConversationItem.vue'

const sidebarCollapsed = ref(false)
const convStore = useConversationsStore()
const router = useRouter()

async function newChat() {
  const conv = await convStore.createConversation()
  router.push(`/chat/${conv.id}`)
}

async function selectConv(id: string) {
  await convStore.selectConversation(id)
  router.push(`/chat/${id}`)
}
</script>

<style scoped>
.app-shell {
  height: 100vh;
}
.sider-header {
  display: flex;
  align-items: center;
  height: 52px;
  padding: 0 16px;
  border-bottom: 1px solid var(--n-border-color);
}
.app-title {
  font-size: 18px;
  font-weight: 700;
  background: linear-gradient(135deg, var(--n-color-target), #7c3aed);
  -webkit-background-clip: text;
  -webkit-text-fill-color: transparent;
  background-clip: text;
}
.new-chat-btn {
  margin: 12px;
  width: calc(100% - 24px);
}
.conv-list {
  flex: 1;
  overflow-y: auto;
  padding: 8px;
}
.sider-footer {
  display: flex;
  justify-content: center;
  gap: 4px;
  padding: 8px;
  border-top: 1px solid var(--n-border-color);
}
.main-area {
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
</style>
