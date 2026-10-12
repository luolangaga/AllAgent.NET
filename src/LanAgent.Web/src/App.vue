<template>
  <n-config-provider :theme="naiveTheme" :locale="zhCN" :date-locale="dateZhCN">
    <n-message-provider>
      <n-dialog-provider>
        <AppShell />
      </n-dialog-provider>
    </n-message-provider>
  </n-config-provider>
</template>

<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { darkTheme, zhCN, dateZhCN } from 'naive-ui'
import AppShell from '@/components/AppShell.vue'
import { useSettingsStore } from '@/stores/settings'
import { useConversationsStore } from '@/stores/conversations'
import { useSkillsStore } from '@/stores/skills'
import { useApiConfigsStore } from '@/stores/apiConfigs'

const settingsStore = useSettingsStore()
const convStore = useConversationsStore()
const skillsStore = useSkillsStore()
const apiStore = useApiConfigsStore()

const naiveTheme = computed(() => {
  const resolved = settingsStore.theme === 'system'
    ? (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
    : settingsStore.theme
  return resolved === 'dark' ? darkTheme : null
})

async function safeLoad(name: string, loader: () => Promise<void>) {
  try {
    await loader()
  } catch (e: any) {
    console.error(`[App] Failed to load ${name}:`, e)
    // Do not block other stores if one fails
  }
}

onMounted(async () => {
  await Promise.all([
    safeLoad('settings', () => settingsStore.load()),
    safeLoad('conversations', () => convStore.loadAll()),
    safeLoad('skills', () => skillsStore.loadAll()),
    safeLoad('apiConfigs', () => apiStore.loadAll()),
  ])
})
</script>
