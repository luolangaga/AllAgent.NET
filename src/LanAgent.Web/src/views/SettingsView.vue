<template>
  <div class="settings-view page-container">
    <div class="page-header">
      <h1>设置</h1>
    </div>

    <n-card title="外观" class="section-card">
      <div class="setting-row">
        <div>
          <div class="setting-row-label">主题</div>
          <div class="setting-row-desc">选择界面颜色主题</div>
        </div>
        <n-button-group>
          <n-button
            v-for="t in themes"
            :key="t.value"
            :type="settings.theme === t.value ? 'primary' : 'default'"
            size="small"
            @click="setTheme(t.value)"
          >{{ t.label }}</n-button>
        </n-button-group>
      </div>
      <div class="setting-row">
        <div>
          <div class="setting-row-label">语言</div>
          <div class="setting-row-desc">界面显示语言</div>
        </div>
        <n-select
          :value="settings.language"
          :options="langOptions"
          style="width:160px"
          @update:value="(v: 'zh' | 'en') => { settings.language = v; settings.save() }"
        />
      </div>
    </n-card>

    <n-card title="模型" class="section-card">
      <div class="setting-row">
        <div>
          <div class="setting-row-label">默认 API 配置</div>
          <div class="setting-row-desc">新对话默认使用的 API 提供商</div>
        </div>
        <n-select
          :value="settings.defaultProviderId"
          :options="providerOptions"
          style="width:240px"
          @update:value="(v: string) => { settings.defaultProviderId = v; settings.save() }"
        />
      </div>
      <div class="setting-row">
        <div>
          <div class="setting-row-label">流式输出</div>
          <div class="setting-row-desc">实时显示 AI 生成内容（推荐开启）</div>
        </div>
        <n-switch
          :value="settings.streamingEnabled"
          @update:value="(v: boolean) => { settings.streamingEnabled = v; settings.save() }"
        />
      </div>
    </n-card>

    <n-card title="关于" class="section-card">
      <div class="setting-row">
        <div class="setting-row-label">LanAgent</div>
        <span style="color:var(--n-text-color-3);font-size:var(--font-size-200)">v1.0.0</span>
      </div>
      <div class="setting-row">
        <div>
          <div class="setting-row-label">数据存储位置</div>
          <div class="setting-row-desc">SQLite 数据库，存储对话历史、技能和配置</div>
        </div>
        <code style="font-size:var(--font-size-200);color:var(--n-text-color-3)">%AppData%\LanAgent\lanagent.db</code>
      </div>
    </n-card>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { NCard, NButton, NButtonGroup, NSelect, NSwitch } from 'naive-ui'
import { useSettingsStore } from '@/stores/settings'
import { useApiConfigsStore } from '@/stores/apiConfigs'

const settings = useSettingsStore()
const apiStore = useApiConfigsStore()

const themes = [
  { value: 'system' as const, label: '跟随系统' },
  { value: 'light' as const, label: '浅色' },
  { value: 'dark' as const, label: '深色' },
]

const langOptions = [
  { label: '中文', value: 'zh' },
  { label: 'English', value: 'en' },
]

const providerOptions = computed(() => [
  { label: '不指定', value: '' },
  ...apiStore.configs.map(c => ({ label: c.displayName, value: c.id })),
])

function setTheme(t: 'light' | 'dark' | 'system') {
  settings.theme = t
  settings.save()
}
</script>

<style scoped>
.section-card {
  margin-bottom: var(--space-6);
}
</style>
