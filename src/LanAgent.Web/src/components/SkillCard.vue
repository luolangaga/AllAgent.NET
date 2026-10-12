<template>
  <n-card :class="{ disabled: !skill.isEnabled }">
    <template #header>
      <div class="skill-card-header">
        <span class="skill-icon">{{ skillIcon }}</span>
        <div class="skill-meta">
          <span class="skill-name">{{ skill.name }}</span>
          <n-tag :type="typeTagType" size="small">{{ typeLabel }}</n-tag>
        </div>
        <n-switch
          :value="skill.isEnabled"
          size="small"
          @update:value="(v: boolean) => emit('toggle', v)"
        />
      </div>
    </template>
    <p class="skill-description">{{ skill.description || '暂无描述' }}</p>
    <template #footer>
      <div class="skill-footer">
        <n-tag type="default" size="small">{{ paramCount }} 个参数</n-tag>
        <n-button v-if="deletable" text size="tiny" type="error" @click="emit('delete')">
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
import { NCard, NTag, NSwitch, NButton } from 'naive-ui'
import type { Skill } from '@/types'

const props = defineProps<{ skill: Skill; deletable?: boolean }>()
const emit = defineEmits<{ toggle: [boolean]; delete: [] }>()

const skillIcon = computed(() => ({
  shell_exec: '⚡', file_read: '📖', file_write: '✏️',
  file_edit: '🔧', list_directory: '📁',
}[props.skill.name] ?? (props.skill.type === 'Builtin' ? '🔩' : '🧩')))

const typeLabel = computed(() => ({
  Builtin: '内置', Shell: '命令行', Custom: '自定义',
}[props.skill.type]))

const typeTagType = computed(() => ({
  Builtin: 'info' as const, Shell: 'warning' as const, Custom: 'success' as const,
}[props.skill.type]))

const paramCount = computed(() => {
  try {
    const p = JSON.parse(props.skill.parametersJson)
    return Object.keys(p.properties ?? {}).length
  } catch { return 0 }
})
</script>

<style scoped>
.disabled { opacity: 0.6; }
.skill-card-header {
  display: flex;
  align-items: center;
  gap: 10px;
}
.skill-icon { font-size: 18px; }
.skill-meta {
  display: flex;
  align-items: center;
  gap: 8px;
  flex: 1;
}
.skill-name { font-weight: 600; font-size: 15px; }
.skill-description {
  font-size: 13px;
  color: var(--n-text-color-3);
  margin: 0;
  line-height: 1.5;
}
.skill-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
</style>
